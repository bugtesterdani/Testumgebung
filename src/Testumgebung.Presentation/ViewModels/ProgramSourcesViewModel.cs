using System.Collections.ObjectModel;
using Testumgebung.Contracts;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Programs;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Releases;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Developer workspace and central assignments. Local editing never publishes a package.</summary>
public sealed partial class ProgramSourcesViewModel : ObservableObject
{
    private readonly ReleaseHub _hub;
    private readonly EditorViewModel _editor;
    private readonly SessionService _session;
    private readonly IUiDispatcher _ui;
    private readonly IReadOnlyList<StationViewModel> _stations;
    private ProgramCatalog _catalog = ProgramCatalog.Empty;
    private ProgramSource? _selected;
    private bool _busy, _ready;
    private string _stationName = "";
    private readonly string? _clientSettingsFile;
    private string? _status;
    public ProgramSourcesViewModel(ReleaseHub hub, EditorViewModel editor, SessionService session, IUiDispatcher ui, IReadOnlyList<StationViewModel> stations, string? clientSettingsFile = null)
    {
        _clientSettingsFile = clientSettingsFile;
        _hub = hub; _editor = editor; _session = session; _ui = ui; _stations = stations;
        StationName = stations.FirstOrDefault()?.Name ?? "";
        try { var clients = ProgramSourceClientSettings.Load(_clientSettingsFile); GitClient = clients.Git; SvnClient = clients.Svn; _hub.Service.SourceProviders = clients.Providers(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { _status = "Lokale Clientkonfiguration: " + ex.Message; }
        SaveClientsCommand = new(SaveClients, () => !_busy && _session.CanDevelop);
        RefreshCommand = new(() => _ = RefreshAsync());
        UpgradeCommand = new(() => _ = UpgradeAsync(), () => !_busy && _session.CanRelease);
        NewSourceCommand = new(() => { Selected = null; Name = _editor.Project.Definition.Name; Repository = Line = ProjectPath = ""; NotifyFields(); }, () => !_busy && _session.CanDevelop);
        SaveSourceCommand = new(() => _ = SaveSourceAsync(), () => !_busy && _ready && _session.CanDevelop);
        SaveAssignmentCommand = new(() => _ = SaveAssignmentAsync(), () => !_busy && _ready && _session.CanRelease);
        ClearAssignmentCommand = new(() => _ = ClearAssignmentAsync(), () => !_busy && _ready && _session.CanRelease);
        SetDefaultCommand = new(() => _ = SetDefaultAsync(), () => !_busy && _ready && _session.CanRelease);
        RemoteDraftCommand = new(() => _ = CreateRemoteDraftAsync(), () => !_busy && _ready && _session.CanDevelop);
        OpenLocalCommand = new(OpenLocal, () => !_busy && _session.CanDevelop);
        session.Changed += () => _ui.Post(RaiseCommands);
    }
    public ObservableCollection<ProgramSource> Sources { get; } = [];
    public ObservableCollection<ReleaseInfo> Versions { get; } = [];
    public IReadOnlyList<string> Providers => _hub.Service.SourceProviders.Names;
    public IReadOnlyList<string> StationNames => _stations.Select(s => s.Name).ToArray();
    public string GitClient { get; set; } = "git";
    public string SvnClient { get; set; } = "svn";
    public string Provider { get; set; } = "Git";
    public string Name { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Line { get; set; } = "main";
    public string ProjectPath { get; set; } = "";
    public string Revision { get; set; } = "";
    public string NewVersion { get; set; } = "";
    public string LocalProject { get; set; } = "";
    public string StationName
    {
        get => _stationName;
        set { if (SetProperty(ref _stationName, value)) LoadAssignmentFields(); }
    }
    private void LoadAssignmentFields()
    {
        var assignment = _catalog.Stations.FirstOrDefault(s => s.Station == StationKey);
        if (assignment is null) return;
        Selected = Sources.FirstOrDefault(s => s.Id == assignment.SourceId);
        AutoUpdate = assignment.AutoUpdate; SelectedVersion = Versions.FirstOrDefault(r => r.ReleaseId == assignment.ReleaseId) ?? Versions.FirstOrDefault();
        OnPropertiesChanged(nameof(AutoUpdate), nameof(SelectedVersion));
    }
    private string StationKey => _stations.FirstOrDefault(s => s.Name == StationName)?.ProgramStationId ?? "";
    public string ComputerName => "Rechner: " + Environment.MachineName;
    public bool AutoUpdate { get; set; } = true;
    public bool MaintenanceConfirmed { get; set; }
    public bool BackupConfirmed { get; set; }
    public ReleaseInfo? SelectedVersion { get; set; }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public string SchemaText => _ready ? "Programmkatalog-Schema v1 bereit" : "Programmkatalog nicht eingerichtet. Datenbank auf Programmkatalog-Schema v1 aktualisieren.";
    public string DefaultText => _catalog.DefaultReleaseId is { } id ? _hub.All.FirstOrDefault(r => r.ReleaseId == id)?.SourceTitle ?? id : "Kein zentrales Standardprogramm";
    public string AssignmentText => string.Join("\n", _catalog.Stations.Select(s => $"{s.Station}: {_catalog.Source(s.SourceId)?.Name} / {_catalog.Source(s.SourceId)?.Line} · {(s.AutoUpdate ? "automatisch" : "festgesetzt: " + s.ReleaseId)}"));
    public ProgramSource? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            if (value is not null) { Name = value.Name; Provider = value.Provider; Repository = value.Repository; Line = value.Line; ProjectPath = value.ProjectPath; }
            NotifyFields(); RebuildVersions();
        }
    }
    public RelayCommand SaveClientsCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand NewSourceCommand { get; }
    public RelayCommand SaveSourceCommand { get; }
    public RelayCommand SaveAssignmentCommand { get; }
    public RelayCommand ClearAssignmentCommand { get; }
    public RelayCommand SetDefaultCommand { get; }
    public RelayCommand RemoteDraftCommand { get; }
    public RelayCommand OpenLocalCommand { get; }

    private void NotifyFields() => OnPropertiesChanged(nameof(Name), nameof(Provider), nameof(Repository), nameof(Line), nameof(ProjectPath));
    private void RebuildVersions()
    {
        Versions.Clear();
        foreach (var release in _hub.All.Where(r => r.ReleasedAt is not null && r.State is ReleaseState.Released or ReleaseState.Archived)
                     .Where(r => Selected is null || r.Source?.Source == Selected).OrderByDescending(r => r.ReleasedAt)) Versions.Add(release);
        SelectedVersion = Versions.FirstOrDefault(); OnPropertyChanged(nameof(SelectedVersion));
    }
    private void RaiseCommands()
    {
        foreach (var command in new[] { SaveClientsCommand, UpgradeCommand, NewSourceCommand, SaveSourceCommand, SaveAssignmentCommand, ClearAssignmentCommand, SetDefaultCommand, RemoteDraftCommand, OpenLocalCommand }) command.RaiseCanExecuteChanged();
    }
    private IProgramCatalogStore Store => _hub.Service.ProgramCatalog ?? throw new InvalidOperationException("Programmkatalog ist nicht konfiguriert.");
    private Actor Actor() => new(_session.User?.DisplayName ?? "", _session.Rights);
    private void Require(bool release = false)
    {
        if (!(release ? _session.CanRelease : _session.CanDevelop)) throw new InvalidOperationException("Berechtigung fehlt.");
    }
    private async Task Execute(Func<Task> work)
    {
        if (_busy) return; _busy = true; RaiseCommands();
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = ex.Message; }
        finally { _busy = false; RaiseCommands(); }
    }
    public Task RefreshAsync() => Execute(LoadAsync);
    private async Task LoadAsync()
    {
        _ready = await Store.IsReadyAsync();
        _catalog = _ready ? await Store.ReadAsync() : ProgramCatalog.Empty;
        var id = Selected?.Id; Sources.Clear(); foreach (var source in _catalog.Sources) Sources.Add(source);
        Selected = Sources.FirstOrDefault(s => s.Id == id) ?? Sources.FirstOrDefault();
        RebuildVersions(); LoadAssignmentFields(); OnPropertiesChanged(nameof(SchemaText), nameof(DefaultText), nameof(AssignmentText));
    }
    public Task UpgradeAsync() => Execute(async () =>
    {
        Require(true);
        if (_stations.Any(s => s.IsRunning) || !MaintenanceConfirmed || !BackupConfirmed) throw new InvalidOperationException("Alle Plätze in Wartung nehmen, Sicherung bestätigen und lokale Prüfungen beenden.");
        await Store.UpgradeAsync(); await _hub.Service.AuditAsync(Actor().User, "Programmkatalog-Schema v1 eingerichtet", "Wartung und Sicherung bestätigt"); await LoadAsync(); Status = "Programmkatalog-Schema v1 eingerichtet. Bestehende Ergebnis- und Freigabetabellen bleiben erhalten.";
    });
    private Task Save(ProgramCatalog catalog, string action) => SaveCore(catalog, action);
    private async Task SaveCore(ProgramCatalog catalog, string action)
    {
        await Store.SaveAsync(catalog); await _hub.Service.AuditAsync(Actor().User, action, "Programmkatalog aktualisiert");
        await _hub.RefreshAsync(); await LoadAsync(); Status = action;
    }
    public Task SaveSourceAsync() => Execute(async () =>
    {
        Require(); var source = new ProgramSource(Selected?.Id ?? Guid.NewGuid().ToString("N"), Name.Trim(), Provider, Repository.Trim(), Line.Trim(), ProjectPath.Trim());
        ProgramSourceValidation.Validate(source);
        if (Selected is not null && Selected != source && _hub.All.Any(r => r.Source?.Source.Id == source.Id))
            throw new InvalidOperationException("Diese Quelle hat bereits Freigaben. Für eine andere Linie oder Repository-Konfiguration eine neue Quelle anlegen.");
        await Save(_catalog with { Sources = [.. _catalog.Sources.Where(s => s.Id != source.Id), source] }, "Programmquelle gespeichert");
        Selected = Sources.Single(s => s.Id == source.Id);
    });
    public Task SaveAssignmentAsync() => Execute(async () =>
    {
        Require(true); var source = Selected ?? throw new InvalidOperationException("Quelle auswählen.");
        if (!StationNames.Contains(StationName)) throw new InvalidOperationException("Prüfplatz auswählen.");
        if (!AutoUpdate && SelectedVersion?.Source?.Source != source) throw new InvalidOperationException("Freigegebene feste Version der Quelle auswählen.");
        var assignment = new ProgramAssignment(StationKey, source.Id, AutoUpdate, AutoUpdate ? null : SelectedVersion!.ReleaseId);
        await Save(_catalog with { Stations = [.. _catalog.Stations.Where(s => s.Station != StationKey), assignment] }, "Platzzuordnung gespeichert");
    });
    public Task ClearAssignmentAsync() => Execute(async () => { Require(true); await Save(_catalog with { Stations = _catalog.Stations.Where(s => s.Station != StationKey).ToArray() }, "Platzzuordnung entfernt"); });
    public Task SetDefaultAsync() => Execute(async () =>
    {
        Require(true); var release = SelectedVersion ?? throw new InvalidOperationException("Freigegebene Standardversion auswählen.");
        if (release.ReleasedAt is null) throw new InvalidOperationException("Standard muss freigegeben sein.");
        await Save(_catalog with { DefaultReleaseId = release.ReleaseId }, "Zentrales Standardprogramm gespeichert");
    });
    public Task CreateRemoteDraftAsync() => Execute(async () =>
    {
        Require(); var source = Selected ?? throw new InvalidOperationException("Quelle auswählen und speichern.");
        var draft = await _hub.Service.CreateRemoteDraftAsync(source, Revision.Trim(), NewVersion, Actor());
        await _hub.RefreshAsync(); RebuildVersions(); Status = $"Remote-Entwurf v{draft.Version} erstellt. In Releases Entwicklungsprüfung und Freigabe durchführen. Quelle: {draft.Source!.Revision}";
    });
    private void SaveClients()
    {
        try { Require(); var clients = new ProgramSourceClientSettings(GitClient.Trim(), SvnClient.Trim()); clients.Save(_clientSettingsFile); _hub.Service.SourceProviders = clients.Providers(); Status = "Clientpfade nur für dieses Windows-Benutzerkonto gespeichert."; }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = ex.Message; }
    }
    private void OpenLocal()
    {
        try
        {
            Require(); var station = _stations.Single(s => s.Name == StationName);
            if (station.IsRunning) throw new InvalidOperationException("Lokale Prüfung zuerst beenden.");
            var file = Path.GetFullPath(LocalProject); var project = new ProjectSerializer().Load(file);
            _editor.Load(project, file); station.UseDraft = true;
            Status = "Lokale Entwicklung geöffnet. Nur der gewählte Platz verwendet den Entwurf; keine zentrale Freigabe geändert.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Status = ex.Message; }
    }
}
