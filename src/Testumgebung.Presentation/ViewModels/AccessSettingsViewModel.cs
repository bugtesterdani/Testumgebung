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
            RightsSource = RightsSource,
            DevelopRole = DevelopRole.Length == 0 ? "Testumgebung.Entwickeln" : DevelopRole,
            ReleaseRole = ReleaseRole.Length == 0 ? "Testumgebung.Freigeben" : ReleaseRole,
            Users = Users.Select(u => new RightsEntry { Upn = u.Upn.Trim(), Name = u.Name.Length == 0 ? null : u.Name, Develop = u.Develop, Release = u.Release }).ToList(),
        };
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
}
