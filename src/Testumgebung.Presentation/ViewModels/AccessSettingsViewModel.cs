/**
 * @file
 * @brief Bearbeitet Mandant, Anbieter und Rechtezuordnung mit den Zugangsbeschränkungen der Oberfläche.
 */

using System.Collections.ObjectModel;
using Testumgebung.Identity;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Releases;

namespace Testumgebung.Presentation.ViewModels;

public sealed class RightsEditRow : ObservableObject
{
    private string _upn = "";
    private string _name = "";
    private bool _develop;
    private bool _release;

    public string Upn { get => _upn; set => SetProperty(ref _upn, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool Develop { get => _develop; set => SetProperty(ref _develop, value); }
    public bool Release { get => _release; set => SetProperty(ref _release, value); }
}

/// <summary>
/// Zugang je Prüfplatz (<c>zugang.json</c> unter ProgramData): Entra-App (Client-ID, Mandant), Inaktivitätssperre und
/// lokale Rechte je Konto. Speichern gelingt nur mit Schreibrecht auf die Datei (vorgesehen: Administratoren).
/// </summary>
public sealed class AccessSettingsViewModel : ObservableObject
{
    private readonly IdentityServices _identity;
    private string _clientId = "";
    private string _tenantId = "";
    private int _inactivityMinutes;
    private string _rightsSource = "Lokal";
    private string _developRole = "Testumgebung.Entwickeln";
    private string _releaseRole = "Testumgebung.Freigeben";
    private string? _status;

    public AccessSettingsViewModel(IdentityServices identity)
    {
        _identity = identity;
        AddCommand = new RelayCommand(() => Users.Add(new RightsEditRow { Develop = true }));
        RemoveCommand = new RelayCommand(p => { if (p is RightsEditRow r) Users.Remove(r); });
        SaveCommand = new RelayCommand(() => Save(), () => _identity.Store is not null);
        ReloadCommand = new RelayCommand(Load);
        Load();
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }

    public ObservableCollection<RightsEditRow> Users { get; } = [];

    public string Path => _identity.Store?.Path ?? "(keine Zugangsdatei – Tests)";

    public string ModeText => _identity.Session.Config switch
    {
        null when _identity.Session.IsDevelopmentMode => "Expliziter Entwicklungsmodus (--development): simulierte Anmeldung mit allen Rechten.",
        null => "Produktivbetrieb: Zugangsdatei fehlt – keine erweiterten Rechte. Entra-Zugang konfigurieren.",
        { LoadError: { } e } => "Zugangsdatei beschädigt – niemand hat Rechte: " + e,
        { UsesEntra: true } => "Anmeldung über Microsoft Entra ID (MSAL/WAM); Rechte aus der Liste unten.",
        _ when _identity.Session.IsDevelopmentMode => "Expliziter Entwicklungsmodus: simulierte Anmeldung; Rechte aus der Liste unten.",
        { SupportsLocalDevelopment: true } => "Lokale Entwicklung eingerichtet: Im Anmeldefenster ausdrücklich lokal anmelden. Rechte aus der Benutzerliste; keine bestätigte Firmenanmeldung.",
        _ => "Produktivbetrieb: Entra-App fehlt – keine erweiterten Rechte.",
    };

    public string ClientId { get => _clientId; set => SetProperty(ref _clientId, value?.Trim() ?? ""); }
    public string TenantId { get => _tenantId; set => SetProperty(ref _tenantId, value?.Trim() ?? ""); }
    public int InactivityMinutes { get => _inactivityMinutes; set => SetProperty(ref _inactivityMinutes, Math.Max(0, value)); }

    public IReadOnlyList<string> RightsSources { get; } = ["Lokal", "AppRollen", "Beide"];
    public string RightsSource { get => _rightsSource; set => SetProperty(ref _rightsSource, string.IsNullOrWhiteSpace(value) ? "Lokal" : value); }
    public string DevelopRole { get => _developRole; set => SetProperty(ref _developRole, value?.Trim() ?? ""); }
    public string ReleaseRole { get => _releaseRole; set => SetProperty(ref _releaseRole, value?.Trim() ?? ""); }

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Load()
    {
        var c = _identity.Store?.Load();
        LoadIdentitySettings(c);
        RightsSource = c?.RightsSource ?? "Lokal";
        DevelopRole = c?.DevelopRole ?? "Testumgebung.Entwickeln";
        ReleaseRole = c?.ReleaseRole ?? "Testumgebung.Freigeben";
        Users.Clear();
        foreach (var u in c?.Users ?? []) Users.Add(new RightsEditRow { Upn = u.Upn, Name = u.Name ?? "", Develop = u.Develop, Release = u.Release });
        RefreshSession();
    }

    /// <summary>Called on the UI dispatcher after sign-in, including when this page was created before login.</summary>
    public void RefreshSession()
    {
        if (_identity.Session.Config is null && _identity.Store?.Exists != true && Users.Count == 0
            && _identity.Session.CanDevelop && _identity.Session.User is { } user)
            Users.Add(new RightsEditRow { Upn = user.Upn, Name = user.DisplayName, Develop = true, Release = _identity.Session.CanRelease });
        OnPropertyChanged(nameof(ModeText));
    }

    private void LoadIdentitySettings(AccessConfig? c)
    {
        ClientId = c?.ClientId ?? "";
        TenantId = c?.TenantId ?? "";
        InactivityMinutes = c?.InactivityMinutes ?? 0;
    }

    public bool Save()
    {
        if (_identity.Store is not { } store) return false;
        if (Users.Any(u => string.IsNullOrWhiteSpace(u.Upn)))
        {
            Status = "Jede Zeile braucht einen Anmeldenamen (UPN).";
            return false;
        }
        if (RightsSource != "Lokal" && string.IsNullOrEmpty(ClientId))
        {
            Status = "App-Rollen setzen eine Entra-App (Client-ID und Mandant) voraus.";
            return false;
        }
        if (string.IsNullOrEmpty(ClientId) != string.IsNullOrEmpty(TenantId))
        {
            Status = "Client-ID und Mandanten-ID gehören zusammen.";
            return false;
        }
        var config = new AccessConfig
        {
            ClientId = ClientId.Length == 0 ? null : ClientId,
            TenantId = TenantId.Length == 0 ? null : TenantId,
            InactivityMinutes = InactivityMinutes,
            AllowLocalDevelopment = _identity.Session.IsDevelopmentMode && ClientId.Length == 0 && TenantId.Length == 0,
            RightsSource = RightsSource,
            DevelopRole = DevelopRole.Length == 0 ? "Testumgebung.Entwickeln" : DevelopRole,
            ReleaseRole = ReleaseRole.Length == 0 ? "Testumgebung.Freigeben" : ReleaseRole,
            Users = Users.Select(u => new RightsEntry { Upn = u.Upn.Trim(), Name = u.Name.Length == 0 ? null : u.Name, Develop = u.Develop, Release = u.Release }).ToList(),
        };
        if (!ValidateReachableAccess(config)) return false;
        try
        {
            store.Save(config);
        }
        catch (UnauthorizedAccessException)
        {
            Status = $"Keine Schreibberechtigung für {store.Path} – die Zugangsdatei darf nur ein Administrator ändern.";
            return false;
        }
        catch (IOException ex)
        {
            Status = "Speichern fehlgeschlagen: " + ex.Message;
            return false;
        }
        _identity.Reload();
        Status = $"Gespeichert ({DateTime.Now:HH:mm:ss}). Angemeldete Personen wurden neu bewertet.";
        OnPropertyChanged(nameof(ModeText));
        return true;
    }

    /// <summary>Validate the future mapping before writing, so rejected edits leave file and session intact.</summary>
    private bool ValidateReachableAccess(AccessConfig config)
    {
        if (config.AllowLocalDevelopment)
            foreach (var user in config.Users)
                if (!user.Upn.Contains('@')) user.Upn = SimulatedIdentityProvider.ToUpn(user.Upn);
        if (config.Users.Any(u => !System.Net.Mail.MailAddress.TryCreate(u.Upn, out var address)
            || !string.Equals(address.Address, u.Upn, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "Ungültiger Anmeldename. Einen vollständigen UPN wie name@firma.example verwenden; lokale Namen werden im Entwicklungsmodus normalisiert.";
            return false;
        }
        if (config.Users.GroupBy(u => u.Upn, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            Status = "Anmeldenamen sind nach der Normalisierung doppelt vorhanden. Jede Person nur einmal zuordnen.";
            return false;
        }
        var session = _identity.Session;
        if (session.CanDevelop && session.User is { } current && config.UsesEntra == (session.Config?.UsesEntra ?? false)
            && (config.RightsFor(current) & Testumgebung.Contracts.UserRights.Develop) == 0)
        {
            Status = $"Nicht gespeichert: {current.Upn} würde den Zugang zu den Einstellungen verlieren. Aktuelles Konto mit Entwickeln beibehalten; zum Entfernen zuerst mit einem anderen berechtigten Konto anmelden.";
            return false;
        }
        return true;
    }

}
