using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Testumgebung.Identity;

namespace Testumgebung.Desktop;

/// <summary>
/// Anmeldung mit persönlichem Firmenkonto über MSAL und den Windows-Kontomanager (WAM, Abschnitt 6.1).
/// Kein Passwort im Produktcode. Es wird immer die Kontoauswahl gezeigt: Der Prüfplatz läuft unter der Windows-Sitzung
/// des Werkers – dessen Konto darf nicht stillschweigend als Entwickler angemeldet werden.
/// </summary>
internal sealed class MsalIdentityProvider : IIdentityProvider
{
    private static readonly string[] Scopes = ["User.Read"];
    private readonly Func<IEnumerable<string>, Prompt, CancellationToken, Task<AuthenticationResult>> _acquireToken;
    private readonly Func<IAccount, Task> _removeAccount;
    private IAccount? _account;

    public MsalIdentityProvider(string clientId, string tenantId, Func<IntPtr> parentWindow)
    {
        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithDefaultRedirectUri()
            .WithParentActivityOrWindow(parentWindow)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Testumgebung – Anmeldung" })
            .Build();
        _acquireToken = (scopes, prompt, cancellationToken) => app.AcquireTokenInteractive(scopes).WithPrompt(prompt).ExecuteAsync(cancellationToken);
        _removeAccount = app.RemoveAsync;
    }

    internal MsalIdentityProvider(Func<IEnumerable<string>, Prompt, CancellationToken, Task<AuthenticationResult>> acquireToken, Func<IAccount, Task> removeAccount)
    { _acquireToken = acquireToken; _removeAccount = removeAccount; }

    public string Name => "Microsoft Entra ID (WAM)";

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
