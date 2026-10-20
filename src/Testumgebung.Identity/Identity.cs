/**
 * @file
 * @brief Definiert anbieterunabhängige Identitäten, Rechte und die Konfiguration der Anmeldung.
 */

using System.Text.Json;
using System.Text.Json.Serialization;
using Testumgebung.Contracts;

namespace Testumgebung.Identity;

/// <summary>Von einem Identitätsanbieter bestätigte Person. Die Anwendung prüft nie Passwörter selbst.</summary>
/// <param name="Upn">Anmeldename (z. B. m.weber@firma.example) – Schlüssel für die Rechte.</param>
/// <param name="IsConfirmed">true = von Microsoft Entra ID bestätigt; false = simulierte Anmeldung (Entwicklungsmodus).</param>
/// <param name="Roles">App-Rollen aus dem Entra-Token (Claim „roles“), z. B. „Testumgebung.Entwickeln“.</param>
public sealed record UserIdentity(string Upn, string DisplayName, string Source, bool IsConfirmed, string? TenantId = null, string? ObjectId = null,
    IReadOnlyList<string>? Roles = null)
{
    public override string ToString() => $"{DisplayName} <{Upn}>";
}

public sealed class SignInException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class SignInCanceledException() : Exception("Anmeldung abgebrochen.");

/// <summary>Anmeldung über einen externen Anbieter (Entra ID über MSAL/WAM) oder simuliert.</summary>
public interface IIdentityProvider
{
    string Name { get; }

    /// <summary>Anmelden; zeigt ggf. den Kontodialog des Betriebssystems (WAM). Blockiert die Prüfengine nicht.</summary>
    Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken);

    Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken);
}

/// <summary>Simulierte Anmeldung ohne Entra (Entwicklungsmodus, Tests). Wird als unbestätigt gekennzeichnet.</summary>
public sealed class SimulatedIdentityProvider(Func<string> displayName) : IIdentityProvider
{
    public string Name => "Simuliert (keine Entra-Anmeldung konfiguriert)";

    public Task<UserIdentity> SignInAsync(bool selectAccount, CancellationToken cancellationToken)
    {
        var name = displayName().Trim();
        if (name.Length == 0) throw new SignInException("Namen für die simulierte Anmeldung angeben.");
        var upn = name.Contains('@') ? name : ToUpn(name);
        return Task.FromResult(new UserIdentity(upn, name.Contains('@') ? name.Split('@')[0] : name, "Simuliert", IsConfirmed: false));
    }

    public Task SignOutAsync(UserIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;

    public static string ToUpn(string name)
    {
        var local = new string(name.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal)
            .Select(c => c switch { 'ä' => 'a', 'ö' => 'o', 'ü' => 'u', 'ß' => 's', _ => c })
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return local + "@simuliert.local";
    }
}

/// <summary>Rechtezuordnung eines Kontos auf diesem Prüfplatz.</summary>
public sealed class RightsEntry
{
    public string Upn { get; set; } = "";
    public string? Name { get; set; }
    public bool Develop { get; set; }
    public bool Release { get; set; }

    [JsonIgnore]
    public UserRights Rights => (Develop ? UserRights.Develop : UserRights.None) | (Release ? UserRights.Release : UserRights.None);
}

/// <summary>
/// Zugangskonfiguration je Prüfplatz (<c>%ProgramData%\Testumgebung\zugang.json</c>, für alle Windows-Benutzer gleich,
/// sollte nur von Administratoren änderbar sein): Entra-App (Client-ID, Mandant) und lokale Rechte je Konto.
/// Ohne Datei entstehen keine automatischen Rechte; lokale Entwicklung muss beim Start oder im Anmeldepanel ausdrücklich aktiviert werden.
/// </summary>
public sealed class AccessConfig
{
    public int Format { get; set; } = 1;

    /// <summary>Anwendungs-ID der Entra-App-Registrierung (leer = simulierte Anmeldung).</summary>
    public string? ClientId { get; set; }

    /// <summary>Mandanten-ID; Anmeldungen aus anderen Mandanten werden abgelehnt.</summary>
    public string? TenantId { get; set; }

    /// <summary>Automatische Abmeldung nach Inaktivität in Minuten (0 = aus). Ein laufender Test wird dabei nie fortgesetzt oder gestoppt.</summary>
    public int InactivityMinutes { get; set; }

    public List<RightsEntry> Users { get; set; } = [];

    /// <summary>Explicitly configured local simulation; never a confirmed production identity.</summary>
    public bool AllowLocalDevelopment { get; set; }

    [JsonIgnore]
    public bool SupportsLocalDevelopment => AllowLocalDevelopment && LoadError is null
        && string.IsNullOrWhiteSpace(ClientId) && string.IsNullOrWhiteSpace(TenantId)
        && RightsSource.Equals("Lokal", StringComparison.OrdinalIgnoreCase) && Users.Any(u => u.Develop);

    /// <summary>
    /// Rechtequelle (Abschnitt 15): „Lokal“ (Liste je Prüfplatz, Standard), „AppRollen“ (zentral über Entra-App-Rollen) oder
    /// „Beide“ (Vereinigung). App-Rollen gelten nur für bestätigte Entra-Anmeldungen.
    /// </summary>
    public string RightsSource { get; set; } = "Lokal";

    /// <summary>Wert der App-Rolle für „Entwickeln“.</summary>
    public string DevelopRole { get; set; } = "Testumgebung.Entwickeln";

    /// <summary>Wert der App-Rolle für „Freigeben“.</summary>
    public string ReleaseRole { get; set; } = "Testumgebung.Freigeben";

    [JsonIgnore] public bool UsesAppRoles => RightsSource.Equals("AppRollen", StringComparison.OrdinalIgnoreCase) || RightsSource.Equals("Beide", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public bool UsesLocalList => !RightsSource.Equals("AppRollen", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rechte aus Rollen und/oder lokaler Liste.</summary>
    public UserRights RightsFor(UserIdentity identity)
    {
        var rights = UsesLocalList ? RightsFor(identity.Upn) : UserRights.None;
        if (UsesAppRoles && identity.IsConfirmed && identity.Roles is { } roles)
        {
            if (roles.Contains(DevelopRole, StringComparer.OrdinalIgnoreCase)) rights |= UserRights.Develop;
            if (roles.Contains(ReleaseRole, StringComparer.OrdinalIgnoreCase)) rights |= UserRights.Release;
        }
        return rights;
    }

    /// <summary>Datei vorhanden, aber nicht lesbar – dann gelten keine Rechte.</summary>
    [JsonIgnore] public string? LoadError { get; set; }

    [JsonIgnore] public bool UsesEntra => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(TenantId);

    public UserRights RightsFor(string upn)
        => Users.FirstOrDefault(u => string.Equals(u.Upn.Trim(), upn.Trim(), StringComparison.OrdinalIgnoreCase))?.Rights ?? UserRights.None;
}

public sealed class AccessConfigStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Testumgebung", "zugang.json");

    public bool Exists => File.Exists(Path);

    /// <summary>null = keine Datei; lokale Entwicklung erfordert eine ausdrückliche Aktivierung.</summary>
    public AccessConfig? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            return JsonSerializer.Deserialize<AccessConfig>(File.ReadAllText(Path), Json) ?? new AccessConfig();
        }
        catch (JsonException ex)
        {
            // Beschädigte Zugangsdatei: keine Rechte statt Entwicklungsmodus (sicherer Zustand).
            return new AccessConfig { Users = [], ClientId = null, TenantId = null, Format = -1, InactivityMinutes = 0 }.WithError(ex.Message);
        }
    }

    /// <summary>Speichern – scheitert ohne Schreibrecht (typisch: nur Administratoren).</summary>
    public void Save(AccessConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));
        File.Move(temp, Path, overwrite: true);
    }
}

internal static class AccessConfigExtensions
{
    public static AccessConfig WithError(this AccessConfig c, string error)
    {
        c.LoadError = error;
        return c;
    }
}
