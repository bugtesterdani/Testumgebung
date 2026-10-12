using Testumgebung.Contracts;

namespace Testumgebung.Identity;

/// <summary>
/// Angemeldete Person und ihre Rechte (Abschnitt 6.1). Rechte entstehen erst, wenn der Anbieter die Identität bestätigt
/// hat, und gehören genau dieser Person (A01). Anmelden/Abmelden beeinflusst laufende Tests nicht – die Prüfengine
/// kennt die Anmeldung nicht. Abmelden entfernt die erweiterten Rechte sofort.
/// </summary>
public sealed class SessionService
{
    private readonly IClock _clock;
    private DateTimeOffset _lastActivity;
    private int _signInGeneration;

    public SessionService(IIdentityProvider provider, AccessConfig? config, IClock? clock = null, bool allowDevelopmentMode = false)
    {
        Provider = provider;
        Config = config;
        AllowDevelopmentMode = allowDevelopmentMode;
        _clock = clock ?? SystemClock.Instance;
        _lastActivity = _clock.Now;
    }

    public IIdentityProvider Provider { get; private set; }

    /// <summary>Fehlende Konfiguration gewährt nur bei ausdrücklich aktiviertem Entwicklungsmodus Rechte.</summary>
    public AccessConfig? Config { get; private set; }

    public bool AllowDevelopmentMode { get; private set; }
    public bool CanEnableDevelopmentMode => !AllowDevelopmentMode && Config is null && !IsSigningIn && !IsSignedIn;

    /// <summary>Bewusste lokale Entwicklung ohne eingerichtete Produktionsanmeldung; keine automatische Rechtevergabe.</summary>
    public void EnableDevelopmentMode()
    {
        if (!CanEnableDevelopmentMode) throw new InvalidOperationException("Lokale Entwicklung ist nur ohne Zugangskonfiguration aktivierbar.");
        AllowDevelopmentMode = true;
        LastError = null;
        Changed?.Invoke();
    }
    public bool IsDevelopmentMode => AllowDevelopmentMode && Config is not { UsesEntra: true };

    public UserIdentity? User { get; private set; }
    public UserRights Rights { get; private set; }
    public bool IsSigningIn { get; private set; }
    public string? LastError { get; private set; }

    public bool IsSignedIn => User is not null;
    public bool CanDevelop => (Rights & UserRights.Develop) != 0;
    public bool CanRelease => (Rights & UserRights.Release) != 0;

    public event Action? Changed;

    /// <summary>Für das Audit-Log: (Benutzer, Aktion, Details).</summary>
    public event Action<string, string, string>? Audited;

    /// <summary>Andere Zugangskonfiguration übernehmen (z. B. nach Bearbeitung durch einen Administrator).</summary>
    public void Reconfigure(IIdentityProvider provider, AccessConfig? config)
    {
        var wasSigned = User;
        Provider = provider;
        Config = config;
        if (wasSigned is not null)
        {
            // Rechte neu bewerten; passt die Anmeldung nicht mehr zur Konfiguration, abmelden.
            var (rights, error) = Evaluate(wasSigned);
            if (error is not null || rights == UserRights.None) Clear("Zugangskonfiguration geändert");
            else Rights = rights;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Anmelden. Läuft asynchron neben der Prüfung; bis zur Bestätigung gelten keine erweiterten Rechte.
    /// <paramref name="selectAccount"/> = „Firmenkonto wechseln“.
    /// </summary>
    public async Task<bool> SignInAsync(bool selectAccount = false, CancellationToken cancellationToken = default)
    {
        var generation = ++_signInGeneration;
        var previous = User;
        if (selectAccount && previous is not null) Clear(null); // Wechsel: alte Rechte sofort entziehen
        IsSigningIn = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            var identity = await Provider.SignInAsync(selectAccount, cancellationToken).ConfigureAwait(false);
            if (generation != _signInGeneration) return false; // überholt (z. B. Abmelden während des Dialogs)
            var (rights, error) = Evaluate(identity);
            if (error is not null)
            {
                LastError = error;
                Audited?.Invoke(identity.Upn, "Anmeldung abgelehnt", error);
                return false;
            }
            if (rights == UserRights.None)
            {
                LastError = $"{identity.Upn} hat auf diesem Prüfplatz keine Rechte (Entwickeln/Freigeben).";
                Audited?.Invoke(identity.Upn, "Anmeldung ohne Rechte", "Keine Zuordnung in der Zugangskonfiguration");
                return false;
            }
            User = identity;
            Rights = rights;
            _lastActivity = _clock.Now;
            Audited?.Invoke(identity.Upn, "Anmeldung", $"{identity.Source}{(identity.IsConfirmed ? " (bestätigt)" : " (simuliert)")} · Rechte: {RightsText(rights)}");
            return true;
        }
        catch (SignInCanceledException)
        {
            LastError = "Anmeldung abgebrochen.";
            return false;
        }
        catch (SignInException ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            if (generation == _signInGeneration) IsSigningIn = false;
            Changed?.Invoke();
        }
    }

    public async Task SignOutAsync(string reason = "Abmeldung")
    {
        _signInGeneration++; // laufenden Anmeldedialog entwerten
        IsSigningIn = false;
        var user = User;
        Clear(reason);
        if (user is not null)
        {
            try { await Provider.SignOutAsync(user, CancellationToken.None).ConfigureAwait(false); }
            catch (SignInException) { /* Konto bleibt im Betriebssystem; Rechte sind bereits entzogen */ }
        }
    }

    public void NoteActivity() => _lastActivity = _clock.Now;

    /// <summary>Optionale Inaktivitätssperre: meldet ab (Rechte weg), der Test läuft unverändert weiter.</summary>
    public bool CheckInactivity()
    {
        if (User is null || Config is not { InactivityMinutes: > 0 } c) return false;
        if (_clock.Now - _lastActivity < TimeSpan.FromMinutes(c.InactivityMinutes)) return false;
        _ = SignOutAsync($"Automatische Abmeldung nach {c.InactivityMinutes} min Inaktivität");
        return true;
    }

    public static string RightsText(UserRights r) => r switch
    {
        UserRights.Develop | UserRights.Release => "Entwickeln, Freigeben",
        UserRights.Develop => "Entwickeln",
        UserRights.Release => "Freigeben",
        _ => "keine",
    };

    private (UserRights Rights, string? Error) Evaluate(UserIdentity identity)
    {
        if (Config is null) return AllowDevelopmentMode
            ? (UserRights.Develop | UserRights.Release, null)
            : (UserRights.None, "Zugangskonfiguration fehlt. Im Produktivbetrieb werden keine erweiterten Rechte vergeben.");
        if (Config.LoadError is not null) return (UserRights.None, "Zugangskonfiguration beschädigt: " + Config.LoadError);
        if (!AllowDevelopmentMode && !Config.UsesEntra)
            return (UserRights.None, "Im Produktivbetrieb ist eine bestätigte Entra-Anmeldung erforderlich.");
        if (Config.UsesEntra)
        {
            if (!identity.IsConfirmed) return (UserRights.None, "Nur bestätigte Firmenkonten (Microsoft Entra ID) sind zugelassen.");
            if (!string.Equals(identity.TenantId, Config.TenantId, StringComparison.OrdinalIgnoreCase))
                return (UserRights.None, "Konto gehört nicht zum konfigurierten Mandanten.");
        }
        return (Config.RightsFor(identity), null);
    }

    private void Clear(string? reason)
    {
        var user = User;
        User = null;
        Rights = UserRights.None;
        if (user is not null && reason is not null) Audited?.Invoke(user.Upn, "Abmeldung", reason);
        Changed?.Invoke();
    }
}
