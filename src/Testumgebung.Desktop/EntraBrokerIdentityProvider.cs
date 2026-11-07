using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Testumgebung.Identity;

namespace Testumgebung.Desktop;

/// <summary>Authenticates the local service by certificate and retrieves only the identity for this attempt.</summary>
internal sealed class EntraBrokerIdentityProvider(
    AccessConfig config, Func<HttpClient>? createClient = null, Action<Uri>? openBrowser = null) : IIdentityProvider
{
    public string Name => "Microsoft Entra ID (lokaler HTTPS-Dienst)";

    public async Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken)
    {
        if (!EntraBrokerProtocol.IsFingerprint(config.EntraBrokerTlsSha256) ||
            !EntraBrokerProtocol.IsCallback(config.EntraRedirectUri))
            throw new SignInException("Umleitungs-URI oder Zertifikat-Fingerabdruck des lokalen Entra-Dienstes ist ungültig.");

        using var client = createClient?.Invoke() ?? CreateHttpClient();
        client.DefaultRequestHeaders.Add(EntraBrokerProtocol.RequestHeader, EntraBrokerProtocol.RequestVersion);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(EntraBrokerProtocol.Lifetime);
        var token = deadline.Token;
        EntraBrokerAttempt? attempt = null;
        try
        {
            using var response = await client.PostAsync(EntraBrokerProtocol.Prefix + "/start", null, token);
            response.EnsureSuccessStatusCode();
            attempt = await response.Content.ReadFromJsonAsync<EntraBrokerAttempt>(token);
            if (attempt is null || !MatchesConfiguration(attempt))
                throw new SignInException("Mandant, App-ID oder Rückrufadresse des Entra-Dienstes stimmen nicht mit den Zugangseinstellungen überein.");

            var browserUri = new Uri(EntraBrokerProtocol.Origin + EntraBrokerProtocol.Prefix + "/browser/" + attempt.BrowserKey);
            (openBrowser ?? OpenBrowser)(browserUri);
            var identity = await WaitForIdentityAsync(client, attempt.ResultKey, token);
            token.ThrowIfCancellationRequested();
            attempt = null;
            return identity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new SignInCanceledException();
        }
        catch (OperationCanceledException)
        {
            throw new SignInException("Zeitlimit der Anmeldung am lokalen Entra-Dienst überschritten.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or System.ComponentModel.Win32Exception)
        {
            throw new SignInException("Der lokale Entra-Dienst oder Systembrowser ist nicht erreichbar. Dienststatus und TLS-Zertifikat prüfen.");
        }
        finally
        {
            if (attempt is not null && EntraBrokerProtocol.IsFingerprint(attempt.ResultKey))
                await CancelAttemptAsync(client, attempt.ResultKey);
        }
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None && certificate is not null &&
                certificate.GetCertHashString(HashAlgorithmName.SHA256)
                    .Equals(config.EntraBrokerTlsSha256, StringComparison.OrdinalIgnoreCase)
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(EntraBrokerProtocol.Origin),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static void OpenBrowser(Uri uri)
    {
        using var browser = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private bool MatchesConfiguration(EntraBrokerAttempt attempt) =>
        EntraBrokerProtocol.IsFingerprint(attempt.BrowserKey) &&
        EntraBrokerProtocol.IsFingerprint(attempt.ResultKey) &&
        SameId(attempt.TenantId, config.TenantId) && SameId(attempt.ClientId, config.ClientId) &&
        EntraBrokerProtocol.IsCallback(attempt.Callback) &&
        new Uri(attempt.Callback).AbsoluteUri == new Uri(config.EntraRedirectUri!).AbsoluteUri;

    private static bool SameId(string? left, string? right) =>
        Guid.TryParse(left, out var first) && Guid.TryParse(right, out var second) && first == second;

    private async Task<UserIdentity> WaitForIdentityAsync(HttpClient client, string key, CancellationToken token)
    {
        while (true)
        {
            using var request = CreateRequest("/result", key);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<EntraBrokerResult>(token);
            if (result?.Status == "pending")
            {
                await Task.Delay(500, token);
                continue;
            }
            if (result is not { Status: "ok", Upn: { Length: > 0 } upn } ||
                !SameId(result.TenantId, config.TenantId) || !Guid.TryParse(result.ObjectId, out _))
                throw new SignInException("Der lokale Entra-Dienst hat keine bestätigte Benutzeridentität für diesen Mandanten geliefert.");
            return new(upn, result.Name ?? upn, "Microsoft Entra ID", true, config.TenantId, result.ObjectId, result.Roles);
        }
    }

    private static async Task CancelAttemptAsync(HttpClient client, string key)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var request = CreateRequest("/cancel", key);
        try
        {
            using var response = await client.SendAsync(request, deadline.Token);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // The service also expires abandoned attempts independently.
        }
    }

    private static HttpRequestMessage CreateRequest(string path, string key) =>
        new(HttpMethod.Post, EntraBrokerProtocol.Prefix + path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", key) }
        };

    public Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;
}
