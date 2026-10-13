using System.Collections.ObjectModel;
using Testumgebung.Contracts;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Identity;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Persistence.Stations;

namespace Testumgebung.Presentation.ViewModels;

public enum ShellPage
{
    /// <summary>Werkeransicht: Prüfen.</summary>
    Operator,
    /// <summary>Entwicklung: Ablaufeditor.</summary>
    Editor,
    /// <summary>Entwicklung: Debugger des gewählten Prüfplatzes.</summary>
    Debug,
    /// <summary>Werkeransicht: Protokoll.</summary>
    Protocol,
    /// <summary>Entwicklung: Baustein-Bibliothek (CodeBlocks).</summary>
    Library,
    /// <summary>Entwicklung: Ergebnisdateien, Outbox und Meldungen.</summary>
    Results,
    /// <summary>Entwicklung: Datenbank, MQTT, Ergebnisordner.</summary>
    Settings,
    /// <summary>Releases: Entwurf, Entwicklungsprüfung, Freigabe, Audit-Log (Entwickeln oder Freigeben).</summary>
    Releases,
    /// <summary>Entwicklung: Kurven – Import, Messkurve, Editor, Versionen, Ausgabe.</summary>
    Curves,
    /// <summary>Entwicklung: Prüfplatzkonfiguration – Geräte, Zuordnung, Reservierungen, Zugriffsprotokoll.</summary>
    Stations,
    /// <summary>Entwicklung: Auswahl- und Auftragsablauf je Prüfplatz – Methoden, Kennungsregeln, Simulation.</summary>
    Selection,
    /// <summary>Pilot: Systemprüfung, Konfiguration sichern und wiederherstellen.</summary>
    System,
    /// <summary>Auswertung und Berichte (Entwickeln oder Freigeben).</summary>
    Reports,
}

/// <summary>
/// Hauptfenster: Prüfplätze, Werker-/Entwickleransicht innerhalb derselben Anwendung.
/// Anmeldung über <see cref="SessionService"/> (Entra ID über MSAL/WAM oder simuliert); Rechte „Entwickeln“ und „Freigeben“
/// getrennt. Abmelden entfernt die erweiterten Rechte, laufende Tests laufen weiter.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly IUiDispatcher _ui;
    private StationViewModel _selectedStation;
    private ShellPage _page = ShellPage.Operator;
    private readonly IdentityServices _identity;
    private string _loginName = "M. Weber";

    public ShellViewModel(IUiDispatcher? ui = null, IFileDialogService? dialogs = null, TestProject? project = null, IClock? clock = null,
        Testumgebung.Domain.Code.ICodeRuntime? codeRuntime = null, Testumgebung.Persistence.Results.ResultPipeline? results = null,
        Settings.ResultServices? services = null, Settings.SettingsStore? settingsStore = null,
        IdentityServices? identity = null, ReleaseHub? releases = null,
        StationCoordinator? stations = null, StationSetupStore? stationStore = null)
    {
        var dependencies = ResolveDependencies(ui, identity, results, services, stations, clock);
        results = dependencies.Results;
        _identity = dependencies.Identity;
        Releases = releases;
        _ui = dependencies.Ui;
        var catalog = BlockCatalog.Default;
        var serializer = new ProjectSerializer(catalog);
        Editor = CreateEditor();
        Editor.CodeRuntime = codeRuntime;
        Library = CreateLibrary();
        IResourceManager resources = dependencies.Resources;
        Resources = resources;

        _clockForStations = clock;
        _codeRuntime = codeRuntime;
        _results = results;
        _serializer = serializer;
        Coordinator = stations ?? new StationCoordinator(StationSetup.Default(Editor.Project), resources, clock) { IsSuggestion = true };
        InitializeStations();
        _selectedStation = Stations[0];
        PairedStations = new PairedStationsViewModel(Stations);
        Results = CreateResults();
        Results.Incomplete.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IncompleteRunsHint));
        ReportsPage = CreateReportsPage();
        var pipeline = results;
        CurvesPage = CreateCurvesPage();
        SettingsPage = CreateSettingsPage();
        ReleasesPage = CreateReleasesPage();
        AccessPage = new AccessSettingsViewModel(_identity);
        ConnectProjectEvents();
        StationsPage = new StationsViewModel(Coordinator, Editor, stationStore, _ui, () => Stations.Any(st => st.IsRunning));
        var accessStore = _identity.Store;
        SystemPage = CreateSystemPage();
        SelectionPage = CreateSelectionPage();
        SelectStationCommand = new RelayCommand(p => { if (p is StationViewModel s) SelectedStation = s; });
        NavigateCommand = CreateNavigationCommand();
        LoginCommand = new RelayCommand(() => _ = LoginAsync(false), () => !IsSignedIn && !Session.IsSigningIn);
        DevelopmentLoginCommand = new RelayCommand(() => _ = LoginDevelopmentAsync(), () => CanEnableDevelopmentLogin);
        SwitchAccountCommand = new RelayCommand(() => _ = LoginAsync(true), () => !Session.IsSigningIn);
        LogoutCommand = new RelayCommand(() => _ = LogoutAsync(), () => IsSignedIn || Session.IsSigningIn);
        ExecuteSelectedCommand = new RelayCommand(
            () => _ = SelectedStation.Debug.ExecuteTargetedAsync(Editor.SelectedNode!.Id, SelectedStation.CreateTargetedRun),
            () => IsDeveloper && Editor.SelectedNode is { IsSection: false } && !SelectedStation.IsRunning);
        StartSimulationCommand = new RelayCommand(() =>
        {
            if (!SelectedStation.CanStart) return;
            _ = SelectedStation.StartSimulationAsync();
            Page = ShellPage.Editor;
        }, () => IsDeveloper && SelectedStation.CanStart && !SelectedStation.Selection.IsActive);
        void ConnectProjectEvents()
        {
            Session.Changed += () => _ui.Post(RaiseUser);
            Session.Audited += (user, action, details) => Audit(user, action, details);
            Editor.Breakpoints = _selectedStation.Debug.Controller;
            Editor.ProjectReplaced += () => OnPropertiesChanged(nameof(ProjectName), nameof(Version));
            // Werkeransicht: ruhende Prüfplätze zeigen das Layout des geöffneten Projekts sofort.
            Editor.LayoutChanged += () => { foreach (var st in Stations) st.RefreshIdleLayout(); };
            Editor.ProjectReplaced += () => { foreach (var st in Stations) { st.RefreshIdleLayout(); st.RefreshAvailablePrograms(); } };
            Coordinator.SetupChanged += () => _ui.Post(SyncStations);
            // Ohne Konfigurationsdatei folgt der Vorschlag dem geöffneten Projekt.
            Editor.ProjectReplaced += () =>
            {
                if (Coordinator.IsSuggestion && Coordinator.RunningStations.Count == 0)
                    Coordinator.Replace(StationSetup.Default(Editor.Project, Stations.Select(st => st.Name).ToList()));
            };
        }

        RelayCommand CreateNavigationCommand()
        {
            return new RelayCommand(p =>
            {
                if (p is ShellPage page) Page = page;
                else if (p is string s && Enum.TryParse<ShellPage>(s, out var parsed)) Page = parsed;
            }, p => Allowed(p switch
            {
                ShellPage page => page,
                string text when Enum.TryParse<ShellPage>(text, out var parsed) => parsed,
                _ => ShellPage.Operator,
            }));
        }

        ResultsViewModel CreateResults()
        {
            return new ResultsViewModel(results, _ui, dialogs, () => Session.User?.Upn ?? Environment.UserName);
        }

        CurvesViewModel CreateCurvesPage()
        {
            return new CurvesViewModel(Editor, dialogs, () => Session.User?.Upn ?? "Entwickler", () => pipeline?.Files, _ui);
        }

        SelectionPageViewModel CreateSelectionPage()
        {
            return new SelectionPageViewModel(Coordinator, stationStore, dialogs, () => SelectedStation.AvailableTests(),
                () => Stations.Any(st => st.IsRunning || st.Selection.IsActive), _ui, codeRuntime);

        }

        SettingsViewModel? CreateSettingsPage()
        {
            if (services is not null)
            {
                var page = new SettingsViewModel(services, settingsStore ?? new Settings.SettingsStore(Settings.SettingsStore.DefaultPath, Settings.PlainSecretProtector.Instance), _ui,
                    ValidateStationDisplay, () => Coordinator.Setup.Stations.Select(s => s.Name).ToList());
                Coordinator.SetupChanged += () => _ui.Post(page.RefreshStationNames);
                services.Applied += () => _ui.Post(Results.SettingsChanged);
                services.Applied += () => _ui.Post(() => ApplyStationDisplay(services.Current));
                ApplyStationDisplay(services.Current);
                return page;
            }
            return null;
        }

        string? ValidateStationDisplay(Settings.AppSettings settings)
        {
            if (!Session.CanDevelop) return "Die Einstellungen dürfen nur mit Entwicklerberechtigung geändert werden.";
            var current = services!.Current;
            var changed = settings.ShowPairedStations != current.ShowPairedStations || settings.FirstPairedStation != current.FirstPairedStation
                || settings.SecondPairedStation != current.SecondPairedStation;
            if (changed && (PairedStations.IsBusy || Coordinator.RunningStations.Count > 0 || Stations.Any(s => s.IsRunning || s.Selection.IsActive)))
                return "Die Prüfplatzanzeige kann erst nach Ende aller Prüfungen und Aufträge geändert werden.";
            if (settings.ShowPairedStations && (settings.FirstPairedStation == settings.SecondPairedStation
                || !Stations.Any(s => s.Name == settings.FirstPairedStation) || !Stations.Any(s => s.Name == settings.SecondPairedStation)))
                return "Für die gemeinsame Anzeige zwei verschiedene vorhandene Prüfplätze auswählen.";
            return null;
        }

        void ApplyStationDisplay(Settings.AppSettings settings)
        {
            PairedStations.First = Stations.FirstOrDefault(s => s.Name == settings.FirstPairedStation) ?? Stations.FirstOrDefault();
            PairedStations.Second = Stations.FirstOrDefault(s => s.Name == settings.SecondPairedStation)
                ?? Stations.FirstOrDefault(s => !ReferenceEquals(s, PairedStations.First));
            PairedStations.Enabled = settings.ShowPairedStations;
        }

        ReleasesViewModel? CreateReleasesPage() => releases is null ? null : new ReleasesViewModel(releases, Session, Editor, _ui, Stations);

        EditorViewModel CreateEditor()
        {
            return new EditorViewModel(project ?? SampleProjects.AcPruefung(catalog), catalog, serializer, dialogs);
        }

        LibraryViewModel CreateLibrary()
        {
            return new LibraryViewModel(Editor)
            {
                OpenGroupBlock = block =>
                {
                    Page = ShellPage.Editor;
                    Editor.SelectedNode = Editor.FindNode(block.Block.Body.Id);
                },
            };
        }

        ReportsViewModel CreateReportsPage()
        {
            return new ReportsViewModel(() => results?.Files?.Folder ?? Testumgebung.Persistence.Results.ResultFileStore.DefaultFolder, dialogs, _ui,
                () => services?.Database is { CanQuery: true } db ? db : null);
        }

        SystemPageViewModel CreateSystemPage()
        {
            var settingsPath = settingsStore?.Path ?? Settings.SettingsStore.DefaultPath;
            return new SystemPageViewModel(
                () => new Pilot.SystemCheckContext
                {
                    Services = services,
                    Coordinator = Coordinator,
                    StationStore = stationStore,
                    AccessStore = accessStore,
                    PythonExecutable = (codeRuntime as Testumgebung.Workers.CodeRuntime)?.Options.PythonExecutable,
                },
                Pilot.ConfigBackup.DefaultSources(settingsPath, accessStore?.Path ?? AccessConfigStore.DefaultPath, stationStore?.Path ?? StationSetupStore.DefaultPath),
                dialogs, () => Stations.Any(st => st.IsRunning || st.Selection.IsActive), _ui,
                afterRestore: () =>
                {
                    // Prüfplätze sofort übernehmen; Einstellungen und Zugang nach Neustart.
                    try
                    {
                        if (stationStore?.Load() is { } restored && Coordinator.RunningStations.Count == 0)
                        {
                            Coordinator.Replace(restored);
                            Coordinator.IsSuggestion = false;
                        }
                    }
                    catch (StationSetupException ex)
                    {
                        Coordinator.LoadError = ex.Message;
                    }
                }, pythonRuntime: codeRuntime as Testumgebung.Workers.CodeRuntime);
        }

        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EditorViewModel.SelectedNode)) return;
            ExecuteSelectedCommand.RaiseCanExecuteChanged();
            SelectedStation.Debug.SelectPatchNode(Editor.SelectedNode is { IsSection: false } n ? n.Id : null);
        };
    }

    private (IUiDispatcher Ui, IdentityServices Identity, Testumgebung.Persistence.Results.ResultPipeline? Results, IResourceManager Resources)
        ResolveDependencies(IUiDispatcher? ui, IdentityServices? identity, Testumgebung.Persistence.Results.ResultPipeline? results,
            Settings.ResultServices? services, StationCoordinator? stations, IClock? clock)
        => (ui ?? SynchronizationContextDispatcher.FromCurrent(), identity ?? IdentityServices.Simulated(() => LoginName),
            results ?? services?.Pipeline, stations?.Resources ?? new ResourceManager(clock ?? SystemClock.Instance));

    private readonly IClock? _clockForStations;
    private readonly Testumgebung.Domain.Code.ICodeRuntime? _codeRuntime;
    private readonly Testumgebung.Persistence.Results.ResultPipeline? _results;
    private readonly ProjectSerializer _serializer;

    /// <summary>Gemeinsamer Zustand aller Prüfplätze (Etappe 9).</summary>
    public StationCoordinator Coordinator { get; }

    private void InitializeStations()
    {
        foreach (var config in Coordinator.Setup.Stations) AddStation(config.Name);
        if (Stations.Count == 0) AddStation(StationSetup.StationName(1));
    }

    private StationViewModel AddStation(string name)
    {
        var station = new StationViewModel(name, () => Editor.Project, Resources, _ui, _serializer, _clockForStations,
            () => Session.User?.Upn ?? "Werker", _codeRuntime, _results, Releases, Coordinator);
        station.DevelopmentRecorded += d => Audit(d.User, "Entwicklungseingriff: " + d.Kind, $"{station.Name} · {d.Details}");
        station.Debug.MarkNode = (id, marker) =>
        {
            if (ReferenceEquals(station, SelectedStation) && Editor.FindNode(id) is { } n) n.RunMarker = marker;
        };
        ConnectStationEvents(station);
        Stations.Add(station);
        return station;
    }

    private void ConnectStationEvents(StationViewModel station)
    {
        station.PropertyChanged += (_, e) =>
        {
            if (ReferenceEquals(station, SelectedStation) && e.PropertyName == nameof(StationViewModel.IsRunning))
            {
                ExecuteSelectedCommand?.RaiseCanExecuteChanged();
                StartSimulationCommand?.RaiseCanExecuteChanged();
            }
            if (ReferenceEquals(station, SelectedStation) && e.PropertyName is nameof(StationViewModel.IsRunning)
                    or nameof(StationViewModel.RunningVersionText) or nameof(StationViewModel.ReleasedVersionText))
                OnPropertyChanged(nameof(ActiveSoftwareText));
        };
        station.StorageReports.CollectionChanged += (_, _) => Results?.Refresh();
        ConnectStationDebugger(station);
    }

    private void ConnectStationDebugger(StationViewModel station)
    {
        station.Debug.PropertyChanged += (_, e) =>
        {
            if (ReferenceEquals(station, SelectedStation) &&
                ((e.PropertyName == nameof(DebugViewModel.IsPaused) && station.Debug.IsPaused)
                || (e.PropertyName == nameof(DebugViewModel.IsCodePaused) && station.Debug.IsCodePaused)))
                WorkspaceToolTab = 2;
        };
    }

    /// <summary>Prüfplätze an die (neu gespeicherte) Konfiguration angleichen; laufende Prüfplätze bleiben.</summary>
    private void SyncStations()
    {
        var names = Coordinator.Setup.Stations.Select(s => s.Name).ToList();
        foreach (var gone in Stations.Where(s => !names.Contains(s.Name) && !s.IsRunning).ToList())
            Stations.Remove(gone);
        foreach (var name in names.Where(n => Stations.All(s => s.Name != n)))
            AddStation(name);
        // Reihenfolge wie in der Konfiguration
        for (var i = 0; i < names.Count; i++)
        {
            var at = Stations.ToList().FindIndex(s => s.Name == names[i]);
            if (at >= 0 && at != i && i < Stations.Count) Stations.Move(at, i);
        }
        if (!Stations.Contains(_selectedStation)) SelectedStation = Stations.First();
        foreach (var station in Stations) station.RaiseStartState();
    }

    public string AppTitle => "Testumgebung";
    public EditorViewModel Editor { get; }
    private int _workspaceToolTab;
    public int WorkspaceToolTab { get => _workspaceToolTab; set => SetProperty(ref _workspaceToolTab, value); }
    public LibraryViewModel Library { get; }

    /// <summary>Prüfplatzkonfiguration (Etappe 9).</summary>
    public StationsViewModel StationsPage { get; }

    /// <summary>Auswertung und Berichte.</summary>
    public ReportsViewModel ReportsPage { get; }

    /// <summary>Systemprüfung und Konfigurationssicherung (Pilot).</summary>
    public SystemPageViewModel SystemPage { get; }

    /// <summary>Auswahl- und Auftragsablauf (Abschnitt 5).</summary>
    public SelectionPageViewModel SelectionPage { get; }

    /// <summary>Kurven (Etappe 8).</summary>
    public CurvesViewModel CurvesPage { get; }

    /// <summary>Ergebnisdateien, Outbox und Meldungen (Etappe 5).</summary>
    public ResultsViewModel Results { get; }

    /// <summary>Hinweis in der Werkeransicht: unvollständige Prüfläufe nach Absturz/Stromausfall warten auf Entscheidung.</summary>
    public string? IncompleteRunsHint => Results.HasIncomplete
        ? $"{Results.Incomplete.Count} unvollständige(r) Prüflauf/-läufe aus einer früheren Sitzung (Absturz oder Stromausfall). Entwickler/Administrator: unter „Ergebnisse“ als abgebrochen speichern oder verwerfen."
        : null;

    /// <summary>Einstellungen (nur wenn die Anwendung Speicherdienste bereitstellt).</summary>
    public SettingsViewModel? SettingsPage { get; }

    public bool HasSettings => SettingsPage is not null;

    /// <summary>Release-Verwaltung (nur mit Release-Ablage).</summary>
    public ReleaseHub? Releases { get; }

    public ReleasesViewModel? ReleasesPage { get; }

    public bool HasReleases => ReleasesPage is not null;

    /// <summary>Menüpunkt „Releases“ für Entwickeln oder Freigeben.</summary>
    public bool ShowReleasesNav => HasReleases && (Session.CanDevelop || Session.CanRelease);

    /// <summary>Auswertung: Entwickeln oder Freigeben (Qualitätssicht).</summary>
    public bool CanSeeReports => Session.CanDevelop || Session.CanRelease;

    public SessionService Session => _identity.Session;

    /// <summary>Zugang (Entra-App, Rechte je Konto) – Teil der Einstellungen.</summary>
    public AccessSettingsViewModel AccessPage { get; }

    public IdentityServices Identity => _identity;
    public IResourceManager Resources { get; }
    public ObservableCollection<StationViewModel> Stations { get; } = [];
    public PairedStationsViewModel PairedStations { get; }

    public RelayCommand SelectStationCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand DevelopmentLoginCommand { get; }
    public bool CanEnableDevelopmentLogin => Session.CanEnableDevelopmentMode;
    public RelayCommand LogoutCommand { get; }
    public RelayCommand SwitchAccountCommand { get; }
    public RelayCommand ExecuteSelectedCommand { get; }
    public RelayCommand StartSimulationCommand { get; }

    public string ProjectName => Editor.ProjectName;
    public string Version => Editor.Version;

    public StationViewModel SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedStation)) return;
            foreach (var n in Editor.AllNodes()) n.RunMarker = RunMarker.None;
            _selectedStation = value;
            Editor.Breakpoints = value.Debug.Controller;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveSoftwareText));
            ExecuteSelectedCommand.RaiseCanExecuteChanged();
            StartSimulationCommand.RaiseCanExecuteChanged();
            if (value.Debug.IsPaused) WorkspaceToolTab = 2;
        }
    }

    public ShellPage Page
    {
        get => _page;
        set
        {
            if (!Allowed(value)) return;
            if (SetProperty(ref _page, value)) OnPropertyChanged(nameof(ViewTitle));
        }
    }

    /// <summary>Recht „Entwickeln“ der angemeldeten, bestätigten Person.</summary>
    public bool IsDeveloper => Session.CanDevelop;

    /// <summary>Recht „Freigeben“.</summary>
    public bool CanRelease => Session.CanRelease;

    public bool IsSignedIn => Session.IsSignedIn;
    public bool IsSigningIn => Session.IsSigningIn;
    public string? LoginError => Session.LastError;
    public bool IsSimulatedLogin => Session.Provider is SimulatedIdentityProvider;
    public string UserText => Session.User?.DisplayName ?? (Session.IsSigningIn ? "Anmeldung läuft …" : "Anmelden");
    public string UserDetail => Session.User is { } u ? $"{u.Upn} · {SessionService.RightsText(Session.Rights)}{(u.IsConfirmed ? "" : " · simuliert")}" : "";
    public string ViewTitle => IsSignedIn && Page is not (ShellPage.Operator or ShellPage.Protocol) ? "ENTWICKLERANSICHT" : "WERKERANSICHT";

    /// <summary>Kopfzeile „Aktive Prüfsoftware“.</summary>
    public string ActiveSoftwareText => SelectedStation.IsRunning ? SelectedStation.RunningVersionText
        : HasReleases ? SelectedStation.ReleasedVersionText : $"v{Editor.Version} (Editor)";

    private bool Allowed(ShellPage page) => page switch
    {
        ShellPage.Operator or ShellPage.Protocol => true,
        ShellPage.Releases => HasReleases && (Session.CanDevelop || Session.CanRelease),
        ShellPage.Settings => Session.CanDevelop && HasSettings,
        ShellPage.Reports => Session.CanDevelop || Session.CanRelease,
        _ => Session.CanDevelop,
    };

    /// <summary>Name für die simulierte Anmeldung.</summary>
    public string LoginName
    {
        get => _loginName;
        set => SetProperty(ref _loginName, value);
    }

    /// <summary>Anmelden (simuliert: sofort). Die Prüfengine läuft unabhängig weiter (A01).</summary>
    public void Login() => _ = LoginAsync(false);

    public async Task LoginDevelopmentAsync()
    {
        if (!CanEnableDevelopmentLogin) return;
        Session.EnableDevelopmentMode();
        await LoginAsync(false).ConfigureAwait(true);
    }

    public async Task LoginAsync(bool selectAccount)
    {
        var ok = await Session.SignInAsync(selectAccount).ConfigureAwait(true);
        if (!ok) return;
        Page = Session.CanDevelop ? ShellPage.Editor : Session.CanRelease && HasReleases ? ShellPage.Releases : ShellPage.Operator;
    }

    /// <summary>Abmelden entfernt erhöhte Rechte; laufende Tests laufen weiter (auch ein pausierter Lauf wird nicht fortgesetzt).</summary>
    public void Logout() => _ = LogoutAsync();

    public async Task LogoutAsync()
    {
        foreach (var s in Stations) s.UseDraft = false;
        await Session.SignOutAsync().ConfigureAwait(true);
        if (!Allowed(Page)) Page = ShellPage.Operator;
    }

    /// <summary>Benutzereingabe (Inaktivitätssperre).</summary>
    public void NoteActivity() => Session.NoteActivity();

    /// <summary>Regelmäßig aufrufen; meldet bei eingestellter Inaktivität ab.</summary>
    public void CheckInactivity()
    {
        if (Session.CheckInactivity()) _ui.Post(() => { if (!Allowed(Page)) Page = ShellPage.Operator; });
    }

    private void Audit(string user, string action, string details)
    {
        if (Releases is null) return;
        _ = Task.Run(async () =>
        {
            try { await Releases.Service.AuditAsync(user, action, details).ConfigureAwait(false); }
            catch (Exception) { /* Audit-Ablage nicht erreichbar: Anzeige über Releases-Seite */ }
        });
    }

    private string? _lastUser;

    /// <summary>
    /// Abmelden (auch durch Inaktivität) während eines pausierten Entwicklungslaufs (Abschnitt 15): der Lauf bleibt pausiert,
    /// wird weder fortgesetzt noch übernimmt er Änderungen; Hinweis am Prüfplatz und Audit-Eintrag.
    /// </summary>
    private void HandlePausedOnSignOut()
    {
        var user = Session.User?.Upn;
        if (_lastUser is { } previous && user is null)
        {
            foreach (var s in Stations.Where(s => s.Debug.IsPaused))
            {
                s.SetHint($"Entwicklungslauf pausiert – {previous} hat sich abgemeldet. Der Lauf wird nicht automatisch fortgesetzt; " +
                          "ein Entwickler setzt ihn fort, oder Werkerabbruch beendet ihn (Exit-Gruppen laufen).");
                Audit(previous, "Abmeldung während Pause", $"{s.Name}: Lauf pausiert an {s.Debug.NodeName}, nicht fortgesetzt");
            }
        }
        _lastUser = user;
    }

    private void RaiseUser()
    {
        HandlePausedOnSignOut();
        if (!Allowed(Page)) Page = ShellPage.Operator;
        OnPropertiesChanged(nameof(IsDeveloper), nameof(CanRelease), nameof(IsSignedIn), nameof(IsSigningIn), nameof(LoginError),
            nameof(UserText), nameof(UserDetail), nameof(ViewTitle), nameof(IsSimulatedLogin), nameof(ShowReleasesNav), nameof(CanSeeReports), nameof(CanEnableDevelopmentLogin));
        LoginCommand.RaiseCanExecuteChanged();
        DevelopmentLoginCommand.RaiseCanExecuteChanged();
        LogoutCommand.RaiseCanExecuteChanged();
        SwitchAccountCommand.RaiseCanExecuteChanged();
        NavigateCommand.RaiseCanExecuteChanged();
        ExecuteSelectedCommand.RaiseCanExecuteChanged();
        StartSimulationCommand.RaiseCanExecuteChanged();
    }
}
