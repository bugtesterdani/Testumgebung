using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Testumgebung.Contracts;

namespace Testumgebung.Presentation.Settings;

public sealed class RemoteSettingsClient
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _token;
    private RemoteSettingsReport? _receipt;
    private bool _polling;

    internal RemoteSettingsClient(Uri endpoint, string token, HttpClient? client)
    {
        _client = client ?? Client;
        _endpoint = endpoint;
        _token = token;
    }

    public static RemoteSettingsClient? FromEnvironment() => TryCreate(
        Environment.GetEnvironmentVariable("TESTUMGEBUNG_REMOTE_URL"),
        Environment.GetEnvironmentVariable("TESTUMGEBUNG_REMOTE_ID"),
        Environment.GetEnvironmentVariable("TESTUMGEBUNG_REMOTE_TOKEN"));

    public static RemoteSettingsClient? TryCreate(string? address, string? id, string? token, HttpClient? client = null)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !ValidId(id) || token is not { Length: 64 } || !token.All(Uri.IsHexDigit))
            return null;
        var endpoint = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/agent/" + id);
        return new(endpoint, token, client);
    }

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 64 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // The caller uses the desktop dispatcher for snapshots and application, including after awaiting the exchange.
    public async Task PollAsync(Func<RemoteSettingsReport> snapshot, Func<RemoteSettingsChange, string?> apply, Action<Exception> onError)
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var report = _receipt ?? snapshot();
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = JsonContent.Create(report);
            using var response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            _receipt = null;
            if (response.StatusCode == HttpStatusCode.NoContent) return;
            var change = await response.Content.ReadFromJsonAsync<RemoteSettingsChange>();
            if (change is null) return;
            if (apply(change) is { } status)
                _receipt = snapshot() with { Revision = change.Revision, Status = status };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
            or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            onError(ex);
        }
        finally { _polling = false; }
    }
}
