using System.Security.Cryptography;
using System.Text;

namespace Testumgebung.Identity;

/// <summary>Optional local authentication. It confirms a password, never a Microsoft/company identity.</summary>
public sealed class LocalPasswordIdentityProvider(AccessConfig config, Func<(string User, string Password)> credentials) : IIdentityProvider
{
    public const string Source = "Lokales Passwort";
    public string Name => "Lokaler Benutzer mit Passwort";
    public async Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken)
    {
        var input = credentials();
        var upn = input.User.Contains('@') ? input.User.Trim() : SimulatedIdentityProvider.ToUpn(input.User.Trim());
        var entry = config.Users.FirstOrDefault(u => string.Equals(u.Upn.Trim(), upn, StringComparison.OrdinalIgnoreCase));
        var valid = entry is not null && await Task.Run(() => LocalPasswordHash.Verify(input.Password, entry.PasswordHash), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!config.UsesLocalPasswords || config.LoadError is not null || !valid) throw new SignInException("Benutzername oder Passwort ist ungültig.");
        return new(entry!.Upn.Trim(), entry.Name ?? entry.Upn, Source, false, ObjectId: Fingerprint(entry));
    }
    public Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;
    internal static bool Matches(UserIdentity identity, AccessConfig config)
    {
        var user = config.Users.FirstOrDefault(u => string.Equals(u.Upn.Trim(), identity.Upn, StringComparison.OrdinalIgnoreCase));
        return identity.Source == Source && !identity.IsConfirmed && user is not null && LocalPasswordHash.IsValid(user.PasswordHash)
            && identity.ObjectId == Fingerprint(user);
    }
    private static string Fingerprint(RightsEntry entry)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Upn.Trim().ToUpperInvariant() + "\n" + entry.PasswordHash)));
}

/// <summary>Versioned PBKDF2-SHA256 with random per-password salt. Hashes can be stored in zugang.json; plaintext never is.</summary>
public static class LocalPasswordHash
{
    private const int Iterations = 600000;
    public static string Create(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024) throw new ArgumentException("Ein Passwort mit 1 bis 1024 Zeichen angeben.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool IsValid(string? stored) => Parse(stored) is not null;
    public static bool Verify(string password, string? stored)
    {
        if (password.Length is 0 or > 1024 || Parse(stored) is not { } parsed) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, parsed.Salt, parsed.Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
    }
    private sealed record Parameters(int Iterations, byte[] Salt, byte[] Hash);
    private static Parameters? Parse(string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations is < 100000 or > 1000000) return null;
        try
        {
            var salt = Convert.FromBase64String(parts[2]); var hash = Convert.FromBase64String(parts[3]);
            return salt.Length == 16 && hash.Length == 32 ? new(iterations, salt, hash) : null;
        }
        catch (FormatException) { return null; }
    }
}
