using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Testumgebung.Contracts;
using Testumgebung.RemoteAdmin;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65_536);
const string manage = "Testumgebung.Fernverwaltung";
var tenant = RequireGuid("Entra:TenantId");
var client = RequireGuid("Entra:ClientId");
var secret = builder.Configuration["Entra:ClientSecret"];
if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Entra:ClientSecret fehlt.");
var databasePath = Path.GetFullPath(builder.Configuration["DatabasePath"] ?? "data/fernverwaltung.db");
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var database = new RemoteSettingsDatabase(databasePath);
database.Initialize();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
}).AddCookie(options =>
{
    options.Cookie.Name = "__Host-Testumgebung-Fernverwaltung";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = false;
}).AddOpenIdConnect(options =>
{
    options.Authority = $"https://login.microsoftonline.com/{tenant:D}/v2.0";
    options.ClientId = client.ToString("D");
    options.ClientSecret = secret;
    options.ResponseType = "code";
    options.UsePkce = true;
    options.MapInboundClaims = false;
    options.TokenValidationParameters.RoleClaimType = "roles";
});
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
builder.Services.AddAuthorization(options => options.AddPolicy(manage, policy => policy.RequireRole(manage)
    .RequireClaim("tid", tenant.ToString("D")).RequireClaim("oid")));

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (HttpContext context, IAntiforgery forgery) =>
{
    var token = forgery.GetAndStoreTokens(context).RequestToken!;
    var stations = database.Stations();
    return Results.Content(AdminPage.Render(stations, database.History, token), "text/html; charset=utf-8");
}).RequireAuthorization(manage);

app.MapPost("/stations/{id}", async (string id, HttpContext context, IAntiforgery forgery) =>
{
    if (!await ValidForm(context, forgery)) return Results.BadRequest("Ungültiges Formular.");
    var form = await context.Request.ReadFormAsync();
    if (!long.TryParse(form["version"], CultureInfo.InvariantCulture, out var version)
        || !int.TryParse(form["errors"], CultureInfo.InvariantCulture, out var errors)
        || !int.TryParse(form["outbox"], CultureInfo.InvariantCulture, out var outbox))
        return Results.BadRequest("Ungültige Einstellung.");
    var settings = new RemoteStationSettings(form.ContainsKey("paired"), form["first"].ToString(), form["second"].ToString(),
        errors, outbox, form.ContainsKey("folder"), form.ContainsKey("calibration"));
    var actor = context.User.FindFirst("oid")!.Value;
    return database.Request(id, version, actor, settings) ? Results.Redirect("/")
        : Results.Conflict("Keine Änderung angenommen. Ansicht neu laden und Prüfplatz bzw. ausstehende Änderung prüfen.");
}).RequireAuthorization(manage);

app.MapPost("/logout", async (HttpContext context, IAntiforgery forgery) =>
{
    if (!await ValidForm(context, forgery)) return Results.BadRequest();
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization(manage);

app.MapPost("/agent/{id}", async (string id, HttpContext context) =>
{
    if (!ValidStationToken(id, context.Request.Headers.Authorization)) return Results.Unauthorized();
    var report = await context.Request.ReadFromJsonAsync<RemoteSettingsReport>();
    if (!ValidReport(report)) return Results.BadRequest();
    var pending = database.Exchange(id, report!);
    return pending is null ? Results.NoContent() : Results.Json(pending);
}).AllowAnonymous();

app.Run();

Guid RequireGuid(string name) => Guid.TryParse(builder.Configuration[name], out var value) && value != Guid.Empty
    ? value : throw new InvalidOperationException($"{name} fehlt oder ist ungültig.");

static async Task<bool> ValidForm(HttpContext context, IAntiforgery forgery)
{
    if (!context.Request.HasFormContentType) return false;
    return await forgery.IsRequestValidAsync(context);
}

bool ValidStationToken(string id, string? authorization)
{
    if (id.Length is 0 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        return false;
    var expected = app.Configuration[$"StationTokenHashes:{id}"];
    if (expected is not { Length: 64 } || authorization is not { Length: 71 } || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        return false;
    if (!expected.All(Uri.IsHexDigit)) return false;
    return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), SHA256.HashData(Encoding.ASCII.GetBytes(authorization[7..])));
}

static bool ValidReport(RemoteSettingsReport? report)
{
    if (report is not { Current.IsValid: true, Stations.Length: <= 50 }
        || report.Stations.Any(name => name is null or { Length: > 120 })) return false;
    return report.Revision is null ? report.Status is null
        : report.Revision > 0 && report.Status is "applied" or "conflict" or "invalid";
}
