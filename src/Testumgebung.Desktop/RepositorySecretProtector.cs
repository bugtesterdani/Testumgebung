using Testumgebung.Presentation.Settings;
namespace Testumgebung.Desktop;

/// <summary>Repository credentials never accept the legacy plaintext fallback used by old general settings files.</summary>
internal sealed class RepositorySecretProtector : ISecretProtector
{
    private readonly DpapiSecretProtector _inner = new();
    public string Protect(string plain) => _inner.Protect(plain);
    public string? Unprotect(string stored) => stored.StartsWith("dpapi:", StringComparison.Ordinal) ? _inner.Unprotect(stored) : null;
}