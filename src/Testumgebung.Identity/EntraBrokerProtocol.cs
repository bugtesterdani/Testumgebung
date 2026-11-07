namespace Testumgebung.Identity;

/// <summary>Local HTTPS protocol between the Windows broker and the desktop application.</summary>
public static class EntraBrokerProtocol
{
    public const string Origin = "https://localhost:5001";
    public const string Prefix = "/_testumgebung/entra/v1";
    public const string RequestHeader = "X-Testumgebung-Entra";
    public const string RequestVersion = "1";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsCallback(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.IsWellFormedOriginalString()
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == "localhost"
        && uri.Port == 5001
        && value.IndexOfAny(['@', '?', '#']) < 0
        && !uri.AbsolutePath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

public sealed record EntraBrokerAttempt(string BrowserKey, string ResultKey, string TenantId, string ClientId, string Callback);
public sealed record EntraBrokerResult(string Status, string? Upn = null, string? Name = null,
    string? TenantId = null, string? ObjectId = null, string[]? Roles = null);
