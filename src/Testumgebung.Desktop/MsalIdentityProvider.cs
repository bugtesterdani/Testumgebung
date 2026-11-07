/**
 * @file
 * @brief Übersetzt die Windows-/MSAL-Anmeldung in die anbieterunabhängige Identität der Anwendung.
 */

using System.Net;
using System.Net.Sockets;
using Microsoft.Identity.Client;
using Testumgebung.Identity;

namespace Testumgebung.Desktop;

/// <summary>
/// Anmeldung mit persönlichem Firmenkonto über MSAL im Systembrowser.
/// Kein Passwort im Produktcode. Es wird immer die Kontoauswahl gezeigt: Der Prüfplatz läuft unter der Windows-Sitzung
/// des Werkers – dessen Konto darf nicht stillschweigend als Entwickler angemeldet werden.
/// </summary>
internal sealed class MsalIdentityProvider : IIdentityProvider
{
    private static readonly string[] Scopes = ["User.Read"];
    private readonly Func<IEnumerable<string>, Prompt, CancellationToken, Task<AuthenticationResult>> _acquireToken;
    private readonly Func<IAccount, Task> _removeAccount;
    private IAccount? _account;

    public MsalIdentityProvider(string clientId, string tenantId, string? redirectUri = null)
    {
        IPublicClientApplication? signedInApp = null;
        _acquireToken = async (scopes, prompt, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var app = CreateApplication(clientId, tenantId, redirectUri);
            var result = await app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false).WithPrompt(prompt).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            signedInApp = app;
            return result;
        };
        _removeAccount = account => signedInApp?.RemoveAsync(account) ?? Task.CompletedTask;
    }

    internal static IPublicClientApplication CreateApplication(string clientId, string tenantId, string? redirectUri)
    {
        var address = string.IsNullOrWhiteSpace(redirectUri) ? AccessConfig.DefaultEntraRedirectUri : redirectUri.Trim();
        if (!AccessConfig.IsValidEntraRedirectUri(address))
            throw new SignInException("Entra-Umleitungs-URI ungültig. " + AccessConfig.EntraRedirectUriHint);

        var uri = new Uri(address);
        if (uri.IsDefaultPort && uri.AbsolutePath != "/")
            address = AddEphemeralPort(uri);

        return PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri(address)
            .Build();
    }

    private static string AddEphemeralPort(Uri uri)
    {
        // MSAL 4.92 drops the path when it chooses the port itself.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return new UriBuilder(uri) { Port = ((IPEndPoint)listener.LocalEndpoint).Port }.Uri.AbsoluteUri;
        }
        catch (SocketException)
        {
            throw new SignInException("Für die Entra-Anmeldung konnte kein freier lokaler Port ermittelt werden.");
        }
    }

    internal MsalIdentityProvider(Func<IEnumerable<string>, Prompt, CancellationToken, Task<AuthenticationResult>> acquireToken, Func<IAccount, Task> removeAccount)
    { _acquireToken = acquireToken; _removeAccount = removeAccount; }

    public string Name => "Microsoft Entra ID (Systembrowser)";

    public async Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken)
    {
        AuthenticationResult result;
        try
        {
            result = await _acquireToken(Scopes, Prompt.SelectAccount, cancellationToken).ConfigureAwait(true);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            throw new SignInCanceledException();
        }
        catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS7000218:", StringComparison.Ordinal))
        {
            throw new SignInException("Entra-Anmeldung fehlgeschlagen (AADSTS7000218). Entra erwartet einen App-Nachweis beim Einlösen des Anmeldecodes. Die IT muss prüfen, ob die Registrierung den Desktop-Redirect als öffentliche Clientanwendung unterstützt oder ein vertraulicher Anmeldedienst erforderlich ist.", ex);
        }
        catch (MsalException ex)
        {
            throw new SignInException("Entra-Anmeldung fehlgeschlagen: " + ex.Message, ex);
        }
        _account = result.Account;
        var name = result.ClaimsPrincipal?.FindFirst("name")?.Value ?? result.Account.Username;
        // App-Rollen (zentrale Rechte, Abschnitt 15): Claim „roles“ des ID-Tokens
        var roles = result.ClaimsPrincipal?.FindAll("roles").Select(c => c.Value).ToList() ?? [];
        return new UserIdentity(result.Account.Username, name, "Microsoft Entra ID", IsConfirmed: true, result.TenantId, result.UniqueId, roles);
    }

    public async Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            if (_account is not null) await _removeAccount(_account).ConfigureAwait(true);
        }
        catch (MsalException ex)
        {
            throw new SignInException(ex.Message, ex);
        }
        finally
        {
            _account = null;
        }
    }
}
