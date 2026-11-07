using System.Security.Principal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testumgebung.EntraBroker;
using Testumgebung.Identity;

const string serviceName = "TestumgebungEntra";
const string attemptItem = "tu_attempt";
var standalone = args is ["--standalone"];
if (args is ["--help"])
{
    Console.WriteLine("Testumgebung.EntraBroker --standalone");
    Console.WriteLine("Als separate Anwendung unter dem vorgesehenen Windows-Konto starten. Ohne Argument startet der Windows-Dienst.");
    return;
}
if ((!standalone && args.Length != 0) || (!standalone && !WindowsServiceHelpers.IsWindowsService()))
{
    Console.Error.WriteLine("Über die Windows-Dienstverwaltung starten oder --standalone verwenden. Hilfe: --help");
    Environment.ExitCode = 2;
    return;
}

using var account = WindowsIdentity.GetCurrent();
if (standalone && (account.IsSystem || new WindowsPrincipal(account).IsInRole(WindowsBuiltInRole.Administrator)))
    throw new InvalidOperationException("Die separate Anmeldeanwendung unter einem eigenen Windows-Standardkonto starten.");
var reader = standalone
    ? account.User ?? throw new InvalidOperationException("Das Windows-Konto für die Anmeldeanwendung konnte nicht bestimmt werden.")
    : (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = [],
    EnvironmentName = Environments.Production
});
if (!standalone) builder.Host.UseWindowsService(options => options.ServiceName = serviceName);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), serviceName);
var storage = new BrokerStorage(reader, settingsDirectory);
var config = new ConfigurationBuilder()
    .AddJsonFile(storage.RequireProtectedFile("broker.json"))
    .Build().GetSection("EntraBroker");
var tenantId = RequireGuid("TenantId");
var clientId = RequireGuid("ClientId");
var callback = config["Callback"] ?? EntraBrokerProtocol.Origin;
if (!EntraBrokerProtocol.IsCallback(callback))
    throw new InvalidOperationException("Die registrierte HTTPS-Callback-Adresse muss localhost:5001 verwenden.");
var callbackPath = PathString.FromUriComponent(new Uri(callback).AbsolutePath);
using var tls = BrokerCredentials.LoadCertificate(config["TlsCertificateSha256"]);
var fingerprint = config["AppCertificateSha256"];
using var appCertificate = string.IsNullOrWhiteSpace(fingerprint) ? null : BrokerCredentials.LoadCertificate(fingerprint);
var secret = appCertificate is null ? BrokerCredentials.ReadSecret(storage.RequireProtectedFile("client-secret.dpapi")) : null;
var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
var attempts = new PendingSignIns(TimeProvider.System);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
    options.ListenLocalhost(5001, endpoint => endpoint.UseHttps(tls));
});
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAuthentication()
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Authority = authority;
        options.ClientId = clientId;
        options.ClientSecret = secret;
        options.CallbackPath = callbackPath;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.FormPost;
        options.UsePkce = true;
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.RemoteAuthenticationTimeout = EntraBrokerProtocol.Lifetime;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.SaveTokens = false;
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = callback;
                context.ProtocolMessage.Prompt = "select_account";
                return Task.CompletedTask;
            },
            OnAuthorizationCodeReceived = async context =>
            {
                var request = context.TokenEndpointRequest!;
                request.RedirectUri = callback;
                if (appCertificate is null) return;

                var metadata = await context.Options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
                request.ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                request.ClientAssertion = BrokerCredentials.CreateAssertion(appCertificate, clientId, metadata.TokenEndpoint);
            },
            OnTicketReceived = async context =>
            {
                context.HandleResponse();
                var principal = context.Principal!;
                var upn = principal.FindFirst("preferred_username")?.Value;
                var oid = principal.FindFirst("oid")?.Value;
                var tid = principal.FindFirst("tid")?.Value;
                var valid = !string.IsNullOrWhiteSpace(upn) && Guid.TryParse(oid, out _) && tid == tenantId;
                if (context.Properties?.Items.TryGetValue(attemptItem, out var key) == true && key is not null)
                {
                    var result = valid
                        ? new EntraBrokerResult("ok", upn, principal.FindFirst("name")?.Value, tid, oid,
                            principal.FindAll("roles").Select(claim => claim.Value).ToArray())
                        : new EntraBrokerResult("failed");
                    valid = attempts.Complete(key, result) && valid;
                }
                else valid = false;
                await context.Response.WriteAsync(valid
                    ? "Anmeldung abgeschlossen. Dieses Browserfenster kann geschlossen werden."
                    : "Anmeldung fehlgeschlagen. Bitte in der Testumgebung erneut starten.");
            },
            OnRemoteFailure = async context =>
            {
                context.HandleResponse();
                if (context.Properties?.Items.TryGetValue(attemptItem, out var key) == true && key is not null)
                    attempts.Complete(key, new("failed"));
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Entra-Anmeldung fehlgeschlagen. Bitte die Registrierung und den App-Nachweis prüfen.");
            }
        };
    });

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps || !string.Equals(context.Request.Host.Value, "localhost:5001", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 400;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});
app.UseAuthentication();

var api = app.MapGroup(EntraBrokerProtocol.Prefix);
api.MapPost("/start", (HttpRequest request) =>
{
    if (!IsDesktopRequest(request)) return Results.BadRequest();
    var attempt = attempts.Create(tenantId, clientId, callback);
    return attempt is null ? Results.StatusCode(429) : Results.Ok(attempt);
});
api.MapGet("/browser/{key}", (string key) =>
{
    if (!attempts.Begin(key)) return Results.NotFound();
    var properties = new AuthenticationProperties();
    properties.Items[attemptItem] = key;
    return Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]);
});
api.MapPost("/result", (HttpRequest request) =>
{
    var key = GetResultKey(request);
    if (key is null) return Results.BadRequest();
    var result = attempts.Take(key);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
api.MapPost("/cancel", (HttpRequest request) =>
{
    var key = GetResultKey(request);
    if (key is null) return Results.BadRequest();
    attempts.Cancel(key);
    return Results.NoContent();
});
app.Run();

string RequireGuid(string name) => Guid.TryParse(config[name], out var value)
    ? value.ToString()
    : throw new InvalidOperationException($"EntraBroker:{name} muss eine GUID sein.");

static bool IsDesktopRequest(HttpRequest request) =>
    request.Headers[EntraBrokerProtocol.RequestHeader] == EntraBrokerProtocol.RequestVersion;

static string? GetResultKey(HttpRequest request)
{
    var header = request.Headers.Authorization.ToString();
    if (!IsDesktopRequest(request) || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
    var key = header[7..];
    return EntraBrokerProtocol.IsFingerprint(key) ? key : null;
}
