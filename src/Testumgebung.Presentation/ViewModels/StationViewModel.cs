using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Domain.Storage;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Hardware;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public enum StationStatusKind
{
    Idle,
    Running,
    Waiting,
    Paused,
    Aborting,
}

public enum ItemState
{
    Pending,
    Running,
    Done,
    Skipped,
    Aborted,
}

/// <summary>Zeile der Ablaufübersicht (Fortschritt des aktuellen Prüflings).</summary>
public sealed class OverviewItemViewModel(string nodeId, int number, string name) : ObservableObject
{
    private ItemState _state;
    private ResultRating _rating;

    public string NodeId { get; } = nodeId;
    public int Number { get; } = number;
    public string Name { get; } = name;

    public ItemState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public ResultRating Rating
    {
        get => _rating;
        set => SetProperty(ref _rating, value);
    }
}

/// <summary>Exit-Gruppe in der Abbruchfolge (innen → außen).</summary>
public sealed class ExitItemViewModel(string nodeId, int number, string name, string description) : ObservableObject
{
    private ItemState _state;
    private ResultRating? _rating;

    public string NodeId { get; } = nodeId;
    public int Number { get; } = number;
    public string Name { get; } = name;
    public string Description { get; } = description;

    public ItemState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(StateText));
        }
    }

    public ResultRating? Rating
    {
        get => _rating;
        set => SetProperty(ref _rating, value);
    }

    public string StateText => State switch
    {
        ItemState.Pending => "Folgt",
        ItemState.Running => "Läuft …",
        ItemState.Done => "Erledigt",
        ItemState.Skipped => "Nicht betreten",
        ItemState.Aborted => "Abgebrochen",
        _ => "",
    };
}

public enum ProtocolKind
{
    Run,
    Step,
    Measure,
    Operator,
    Abort,
    Error,
    Info,
}

/// <summary>Protokollzeile (Werker- und Entwickleransicht).</summary>
public sealed record ProtocolEntry(
    DateTimeOffset Time,
    ProtocolKind Kind,
    string Text,
    string Value,
    string Unit,
    string Limits,
    ResultRating? Rating,
    string Status,
    bool IsHeader = false,
    string Iteration = "")
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string RatingText => Rating?.ToDisplay() ?? "—";
}

/// <summary>Große Werkerwarnung zur DB-Speicherung eines Runs.</summary>
public sealed record StorageWarning(string RunId, string DutId, string Text, string Detail);

/// <summary>Messwert des aktuellen Prüflings (Bezeichnung, Wert, Einheit, Grenzwerte, Bewertung).</summary>
public sealed record MeasurementRow(string Name, string Value, string Unit, string Limits, ResultRating Rating);

/// <summary>
/// Ein Prüfplatz: eigener Start und Abbruch, eigene Anzeige, eigener Lauf (Abschnitt 4).
/// Implementiert die Werkerinteraktion für die Engine.
/// </summary>
public sealed partial class StationViewModel : ObservableObject, IOperatorInterface, IVersionSource
{
    private const int MaxProtocol = 2000;

    private readonly IUiDispatcher _ui;
    private readonly Func<TestProject> _projectSource;
    private readonly ProjectSerializer _serializer;
    private readonly IResourceManager _resources;
    private readonly IClock _clock;
    private readonly Dictionary<string, OverviewItemViewModel> _overviewByNode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dutIdWriters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _orderWriters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperatorContentViewModel> _contentById = new(StringComparer.Ordinal);

    private readonly Testumgebung.Domain.Code.ICodeRuntime? _codeRuntime;
    private readonly ResultPipeline? _results;
    private readonly ReleaseHub? _releases;
    private bool _useDraft;
    private ReleaseInfo? _activeRelease;
    private string? _runningReleaseId;
    private string? _rejectedReleaseId;
    private TestProject? _runningProject;
    private bool _runIsDraft;
    private string? _pendingVersionText;
    private StorageResolver? _storage;
    private string? _lastResultFile;
    private string? _storageError;
    private TestRun? _run;
    private Task<RunStatus>? _runTask;
    private EngineState _state = EngineState.Idle;
    private string _statusText = "Bereit";
    private StationStatusKind _statusKind;
    private string _currentDutId = "—";
    private string _currentOrder = "—";
    private string _runningVersion = "";
    private int _dutCount;
    private int _abortCount;
    private string _currentStepName = "";
    private string _currentStepPath = "";
    private string _hint = "Prüfung mit „Start“ beginnen.";
    private ResultRating _dutRating;
    private bool _dutWerror;
    private RunStatus? _lastStatus;
    private string? _waitingResource;
    private readonly StationCoordinator? _coordinator;

    /// <summary>Gemeinsamer Zustand aller Prüfplätze (Konfiguration, Geräte, Zugriffsprotokoll); null = eigene Simulation je Lauf.</summary>
    public StationCoordinator? Coordinator => _coordinator;
    private string? _error;

    public StationViewModel(
        string name,
        Func<TestProject> projectSource,
        IResourceManager resources,
        IUiDispatcher? ui = null,
        ProjectSerializer? serializer = null,
        IClock? clock = null,
        Func<string>? currentUser = null,
        Testumgebung.Domain.Code.ICodeRuntime? codeRuntime = null,
        ResultPipeline? results = null,
        ReleaseHub? releases = null,
        StationCoordinator? coordinator = null)
    {
        _coordinator = coordinator;
        _codeRuntime = codeRuntime;
        _results = results;
        _releases = releases;
        Name = name;
        _projectSource = projectSource;
        _resources = resources;
        _ui = ui ?? ImmediateDispatcher.Instance;
        if (releases is not null) releases.Changed += () => _ui.Post(() => OnPropertiesChanged(nameof(ReleasedVersionText), nameof(HasRelease), nameof(AvailablePrograms)));
        if (coordinator is not null) coordinator.SetupChanged += () => _ui.Post(RefreshAvailablePrograms);
        _serializer = serializer ?? new ProjectSerializer();
        _clock = clock ?? SystemClock.Instance;
        Debug = new DebugViewModel(this, _ui, currentUser ?? (() => ""));
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        AbortCommand = new RelayCommand(RequestAbort, () => CanAbort);
        Selection = new StationSelectionViewModel(this);
        _layout = projectSource().Definition.Layout.Clone();
        _showAllMeasurements = _layout.ShowAllMeasurements;
    }

    public string Name { get; }
    public DebugViewModel Debug { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand AbortCommand { get; }

    public ObservableCollection<OverviewItemViewModel> Overview { get; } = [];
    public ObservableCollection<ExitItemViewModel> ExitSequence { get; } = [];
    public ObservableCollection<ProtocolEntry> Protocol { get; } = [];
    public ObservableCollection<MeasurementRow> Measurements { get; } = [];

    /// <summary>Zuletzt aufgenommene Kurven (z. B. L1–L3) mit Cursor und Effektivwerten für die Werkeransicht.</summary>
    public WaveformPlotViewModel LiveWaveform { get; } = new();

    private string? _liveWaveformTitle;

    public string? LiveWaveformTitle
    {
        get => _liveWaveformTitle;
        private set { if (SetProperty(ref _liveWaveformTitle, value)) OnPropertyChanged(nameof(HasLiveWaveform)); }
    }

    public bool HasLiveWaveform => _liveWaveformTitle is not null;
    public ObservableCollection<OperatorContentViewModel> Contents { get; } = [];

    /// <summary>Große Warnungen „DB-Speicherung gescheitert“ – je betroffenem Run, jeweils <see cref="WarningDuration"/> sichtbar.</summary>
    public ObservableCollection<StorageWarning> StorageWarnings { get; } = [];

    public static TimeSpan WarningDuration { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Wartezeit bis zum Ausblenden einer Warnung (Echtzeit der Oberfläche; in Tests ersetzbar).</summary>
    public Func<TimeSpan, Task> WarningDelay { get; set; } = d => Task.Delay(d);

    /// <summary>Letzte gespeicherte Ergebnisdatei.</summary>
    public string? LastResultFile
    {
        get => _lastResultFile;
        private set => SetProperty(ref _lastResultFile, value);
    }

    /// <summary>Dateispeicherfehler – deutlich anzeigen, bis zum nächsten erfolgreichen Speichern.</summary>
    public string? StorageError
    {
        get => _storageError;
        private set => SetProperty(ref _storageError, value);
    }

    /// <summary>Letzte Speicherberichte (für Tests und Entwickleransicht).</summary>
    public ObservableCollection<StorageReport> StorageReports { get; } = [];

    public ResultPipeline? Results => _results;

    /// <summary>Entwicklereingriff im Lauf (für das Audit-Log).</summary>
    public event Action<DevelopmentEvent>? DevelopmentRecorded;

    /// <summary>Werkerbetrieb nur mit freigegebenen Versionen; ohne Release-Ablage (Tests, Altbetrieb) gilt der Editorstand.</summary>
    public bool UsesReleases => _releases is not null;

    /// <summary>Entwickler: aktuellen Entwurf aus dem Editor statt der freigegebenen Version starten (Entwicklungslauf).</summary>
    public bool UseDraft
    {
        get => _useDraft;
        set { if (SetProperty(ref _useDraft, value)) RefreshAvailablePrograms(); }
    }

    /// <summary>Freigegebene Version des laufenden bzw. zuletzt gestarteten Ablaufs (null = Entwurf/Altbetrieb).</summary>
    public ReleaseInfo? ActiveRelease
    {
        get => _activeRelease;
        private set
        {
            if (SetProperty(ref _activeRelease, value)) OnPropertyChanged(nameof(RunningVersionText));
        }
    }

    /// <summary>Freigegebene, aber noch nicht übernehmbare Version (Hinweis bis zum nächsten Start).</summary>
    public string? PendingVersionText
    {
        get => _pendingVersionText;
        private set => SetProperty(ref _pendingVersionText, value);
    }

    public bool HasRelease => _releases?.CurrentInfo(_projectSource().Definition.Name) is not null;

    /// <summary>Aktuell freigegebene Version des Ablaufs (Kopfzeile „Aktive Prüfsoftware“).</summary>
    public string ReleasedVersionText => _releases is null ? "" :
        _releases.CurrentInfo(_projectSource().Definition.Name) is { } r ? $"v{r.Version} · Freigegeben" : "keine freigegebene Version";

    public string RunningVersionText => RunningVersion.Length == 0 ? ""
        : _runIsDraft ? $"Entwurf v{RunningVersion} (nicht freigegeben)"
        : ActiveRelease is { } r ? $"v{r.Version} · Freigegeben" : $"v{RunningVersion}";

    /// <summary>
    /// <see cref="IVersionSource"/>: neue Freigabe erst beim nächsten Prüfling (V01), nur wenn verträglich;
    /// sonst gilt sie ab dem nächsten Start des Hauptablaufs.
    /// </summary>
    TestDefinition? IVersionSource.NextDefinition(TestDefinition current)
    {
        if (_releases is null || _runIsDraft || _runningProject is null) return null;
        var info = _releases.CurrentInfo(current.Name);
        if (info is null || info.ReleaseId == _runningReleaseId || info.ReleaseId == _rejectedReleaseId) return null;
        if (_releases.GetReleased(current.Name) is not { } next) return null;
        if (!DefinitionCompatibility.CanSwapAtDut(_runningProject, next.Project, out var reason))
        {
            _rejectedReleaseId = info.ReleaseId;
            _ui.Post(() => PendingVersionText = $"Version {info.Version} ist freigegeben und wird beim nächsten Start aktiv ({reason}).");
            return null;
        }
        if (_coordinator is not null)
        {
            if (_coordinator.CycleRiskWithRunning(Name, next.Project.Definition) is { } cycle)
            {
                _rejectedReleaseId = info.ReleaseId;
                _ui.Post(() => PendingVersionText = $"Version {info.Version} wird nicht übernommen: {cycle.Text}.");
                return null;
            }
            _coordinator.UpdateRun(Name, next.Project.Definition);
        }
        _runningReleaseId = info.ReleaseId;
        _runningProject = next.Project;
        _ui.Post(() => ActiveRelease = info);
        return next.Project.Definition;
    }

    /// <summary>Der aktuelle Lauf (für Tests und Debugger).</summary>
    public TestRun? Run => _run;

    public Task<RunStatus>? RunTask => _runTask;

    private TaskCompletionSource? _completion;
    private bool _simulationOnly;
    private bool _abortBeforeEngineStart;
    private readonly object _storageGate = new();
    private readonly HashSet<Task> _storageTasks = [];

    public bool IsRunning => _completion is { Task.IsCompleted: false };
    public bool IsSimulation => _simulationOnly;

    /// <summary>Wartet auf Exit-Folge, sicheren Zustand und alle lokalen Speicheraufträge.</summary>
    public async Task WaitForCompletionAsync()
    {
        await Selection.WaitForCompletionAsync().ConfigureAwait(false);
        if (_completion is { } completion) await completion.Task.ConfigureAwait(false);
        await DrainStorageAsync().ConfigureAwait(false);
    }

    private async Task DrainStorageAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_storageGate) tasks = _storageTasks.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private void QueueStorage(ResultRun recorded, StorageResolver resolver)
    {
        var task = StoreResultAsync(recorded, resolver);
        lock (_storageGate) _storageTasks.Add(task);
        _ = task.ContinueWith(t => { lock (_storageGate) _storageTasks.Remove(t); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public bool CanStart => !IsRunning && (!Selection.IsActive || Selection.CanStart || Selection.CanSubmitOrder);
    public bool CanAbort => IsRunning || Selection.IsActive;

    public EngineState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public StationStatusKind StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public string CurrentDutId
    {
        get => _currentDutId;
        private set => SetProperty(ref _currentDutId, value);
    }

    public string CurrentOrder
    {
        get => _currentOrder;
        private set => SetProperty(ref _currentOrder, value);
    }

    public string RunningVersion
    {
        get => _runningVersion;
        private set => SetProperty(ref _runningVersion, value);
    }

    /// <summary>Durchlaufzahl (statt irreführendem Gesamtprozentsatz bei Endlosschleifen).</summary>
    public int DutCount
    {
        get => _dutCount;
        private set
        {
            SetProperty(ref _dutCount, value);
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public int AbortCount
    {
        get => _abortCount;
        private set
        {
            SetProperty(ref _abortCount, value);
            OnPropertyChanged(nameof(IsAborting));
        }
    }

    public bool IsAborting => _abortCount > 0 && IsRunning;

    public string CurrentStepName
    {
        get => _currentStepName;
        private set => SetProperty(ref _currentStepName, value);
    }

    public string CurrentStepPath
    {
        get => _currentStepPath;
        private set => SetProperty(ref _currentStepPath, value);
    }

    public string Hint
    {
        get => Selection.OrderSession && !Selection.IsActive && !IsRunning ? "Fertigungsauftrag scannen und mit Enter bestätigen." : _hint;
        private set => SetProperty(ref _hint, value);
    }

    public ResultRating DutRating
    {
        get => _dutRating;
        private set => SetProperty(ref _dutRating, value);
    }

    public bool DutWerror
    {
        get => _dutWerror;
        private set => SetProperty(ref _dutWerror, value);
    }

    public RunStatus? LastStatus
    {
        get => _lastStatus;
        private set => SetProperty(ref _lastStatus, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public int CurrentStepIndex => Overview.FirstOrDefault(o => o.State == ItemState.Running)?.Number ?? Overview.Count(o => o.State == ItemState.Done);

    public string ProgressText => Overview.Count == 0
        ? ""
        : $"Durchlauf {Math.Max(DutCount, 1)} • Schritt {Math.Max(CurrentStepIndex, 1)} von {Overview.Count}";

    public double DutProgress => Overview.Count == 0 ? 0 : 100.0 * Overview.Count(o => o.State is ItemState.Done or ItemState.Skipped) / Overview.Count;

    public OperatorContentViewModel? ActiveContent => Contents.LastOrDefault(c => c.IsAwaiting) ?? Contents.LastOrDefault();

    // ------------------------------------------------------------------ Steuerung

    /// <summary>Editorstand ausschließlich mit simulierten Geräten ausführen, unabhängig von der Stationszuordnung.</summary>
    public Task StartSimulationAsync()
    {
        if (!CanStart || Selection.IsActive) return Task.CompletedTask;
        return RunProjectAsync(_serializer.Clone(_projectSource()), null, null, null, simulationOnly: true);
    }

    /// <summary>Startet den Hauptablauf mit einer unveränderlichen Kopie der aktuellen Projektversion.</summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;
        // Auswahl-/Auftragsablauf: „Start“ öffnet die Erfassung bzw. startet den zugeordneten Test
        if (Selection.IsActive)
        {
            await Selection.StartSelectedAsync().ConfigureAwait(true);
            return;
        }
        if (SelectionConfig is { Enabled: true })
        {
            Selection.Activate();
            if (Selection.CanSubmitOrder) await Selection.StartSelectedAsync().ConfigureAwait(true);
            return;
        }
        TestProject project;
        ReleaseInfo? release = null;
        try
        {
            if (_releases is null || UseDraft)
            {
                project = _serializer.Clone(_projectSource());
            }
            else
            {
                var name = _projectSource().Definition.Name;
                if (_releases.GetReleased(name) is not { } released)
                {
                    await _releases.RefreshAsync().ConfigureAwait(true);
                    if (_releases.GetReleased(name) is not { } again)
                    {
                        Error = $"Keine freigegebene Version von „{name}“. Im Werkerbetrieb laufen nur freigegebene Versionen" +
                                " – Entwickler: Releases → Entwurf erstellen, prüfen, freigeben (oder „Entwurf testen“)."
                                + (_releases.Error is { } e ? " " + e : "");
                        return;
                    }
                    released = again;
                }
                (release, project) = released;
            }
        }
        catch (Exception ex) when (ex is ProjectFormatException or ReleasePackageException)
        {
            Error = ex.Message;
            return;
        }
        await RunProjectAsync(project, release, null, null).ConfigureAwait(true);
    }

    /// <summary>
    /// Lauf eines Projekts (freigegebene Version oder Entwurf) bis zum Ende. Aus der Auswahl: mit Vorbelegung
    /// (Kennung, Auftrag, Prüfparameter) und genau einem Prüfling.
    /// </summary>
    internal async Task RunProjectAsync(TestProject project, ReleaseInfo? release, IReadOnlyDictionary<string, object?>? presets, int? dutLimit,
        string? selectionNote = null, bool simulationOnly = false, bool orderSession = false, string? pythonVenv = null)
    {
        if (IsRunning) return;
        var completion = _completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _run = null;
        _runTask = null;
        _abortBeforeEngineStart = false;
        _simulationOnly = simulationOnly;
        OnPropertyChanged(nameof(IsSimulation));
        RaiseRunState();
        try
        {
            await RunProjectCoreAsync(project, release, presets, dutLimit, selectionNote, orderSession, pythonVenv).ConfigureAwait(true);
        }
        finally
        {
            try { await DrainStorageAsync().ConfigureAwait(true); }
            finally
            {
                _simulationOnly = false;
                completion.TrySetResult();
                OnPropertyChanged(nameof(IsSimulation));
                RaiseRunState();
            }
        }
    }

    private HardwareRegistry? BuildRunRegistry(TestProject project)
    {
        HardwareRegistry registry;
        if (_coordinator is not null && !_simulationOnly)
        {
            // Parallelbetrieb: Zyklusrisiko mit den laufenden Abläufen der anderen Prüfplätze → Start verweigert.
            if (_coordinator.TryBeginRun(Name, project.Definition) is { } refusal)
            {
                Error = refusal;
                return null;
            }
            try
            {
                registry = _coordinator.BuildRegistry(Name, project, seed: Name.GetHashCode());
            }
            catch (StationSetupException ex)
            {
                _coordinator.EndRun(Name);
                Error = ex.Message;
                return null;
            }
        }
        else
        {
            registry = SampleProjects.CreateSimulation(project, seed: Name.GetHashCode()).Registry;
        }

        return registry;
    }

    private async Task<bool> ValidateRunProjectAsync(TestProject project, ReleaseInfo? release)
    {
        if (release is not null && _releases is not null)
        {
            var differences = await _releases.Service.VerifyEnvironmentAsync(release).ConfigureAwait(true);
            if (differences.Count > 0)
            {
                Error = "Freigabe passt nicht zur Ausführungsumgebung: " + string.Join(" ", differences);
                return false;
            }
        }
        var diagnostics = project.Definition.Validate();
        if (diagnostics.Count > 0)
        {
            Error = string.Join(Environment.NewLine, diagnostics);
            return false;
        }

        return true;
    }

    private TestRunOptions RunOptions(HardwareRegistry registry, IReadOnlyDictionary<string, object?>? presets, int? dutLimit, bool orderSession, string? pythonVenv)
        => new()
        {
            Clock = _clock,
            Resources = _resources,
            Hardware = registry,
            AccessJournal = _coordinator?.Journal,
            Operator = this,
            Debugger = Debug.Controller,
            StationName = Name,
            CodeRuntime = _codeRuntime,
            PythonVenv = pythonVenv,
            PairedStation = PairContext,
            // Beide Seiten behalten bis zum nächsten gemeinsamen Auftrag dieselbe Version.
            VersionSource = PairContext is null ? this : null,
            Presets = presets,
            SessionPresets = orderSession ? presets : null,
            ContinuousLoop = orderSession,
            DutLimit = dutLimit,
            EventHistoryLimit = 2000,
            RetainWaveformsInHistory = false,
            DevelopmentRun = _runIsDraft,
        };

    private async Task RunProjectCoreAsync(TestProject project, ReleaseInfo? release, IReadOnlyDictionary<string, object?>? presets, int? dutLimit,
        string? selectionNote, bool orderSession, string? pythonVenv)
    {
        _runIsDraft = _simulationOnly || (_releases is not null && UseDraft);
        _runningProject = project;
        _runningReleaseId = release?.ReleaseId;
        _rejectedReleaseId = null;
        PendingVersionText = null;
        ActiveRelease = release;
        if (!await ValidateRunProjectAsync(project, release).ConfigureAwait(true)) return;

        var registry = BuildRunRegistry(project);
        if (registry is null) return;

        Error = null;
        _storage = new StorageResolver(project.Definition);
        ApplyLayout(project.Definition.Layout);
        ResetDisplay(project.Definition);
        if (orderSession && presets?.GetValueOrDefault("Auftrag") is string order) CurrentOrder = order;
        if (selectionNote is not null) LogSelection("Auswahl · Kennung zugeordnet", selectionNote, ResultRating.NResult, "OK");
        _run = new TestRun(project.Definition, RunOptions(registry, presets, dutLimit, orderSession, pythonVenv), runId: $"{Name.Replace(' ', '-')}-{DateTime.Now:HHmmss}");
        if (_results is not null)
        {
            var resolver = _storage;
            var recorder = new ResultRecorder(_run, _results.Journal, resolver);
            recorder.RunRecorded += recorded => QueueStorage(recorded, resolver);
        }
        _run.EventRaised += e => _ui.Post(() => Handle(e));
        RunningVersion = project.Definition.Version;
        OnPropertyChanged(nameof(RunningVersionText));
        Hint = "Prüfung läuft.";
        _runTask = Task.Run(_run.RunAsync);
        if (_abortBeforeEngineStart) RequestAbort();
        RaiseRunState();

        RunStatus status;
        try
        {
            status = await _runTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _coordinator?.EndRun(Name);
            var problems = await SafeStateAsync().ConfigureAwait(false);
            _ui.Post(() =>
            {
                Error = ex.Message + (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
                RaiseRunState();
            });
            return;
        }
        _coordinator?.EndRun(Name);
        var safeProblems = await SafeStateAsync().ConfigureAwait(false);
        _ui.Post(() =>
        {
            foreach (var p in safeProblems)
                AddProtocol(new ProtocolEntry(DateTimeOffset.Now, ProtocolKind.Error, "Sicherer Zustand", "—", "", "", ResultRating.Error, p));
            if (safeProblems.Count > 0) Error = "Sicherer Zustand nicht für alle Geräte bestätigt – Geräte prüfen: " + string.Join(" ", safeProblems);
            LastStatus = status;
            State = EngineState.Completed;
            StatusText = "Bereit";
            StatusKind = StationStatusKind.Idle;
            Hint = $"Ablauf beendet: {status}. Neuer Start möglich.";
            foreach (var c in Contents) c.Cancel();
            Contents.Clear();
            _contentById.Clear();
            OnPropertyChanged(nameof(ActiveContent));
            RaiseRunState();
        });
    }

    /// <summary>Lauf für gezielte Ausführung im Debugkontext (eigene Projektkopie, Simulation dieses Prüfplatzes).</summary>
    public TestRun CreateTargetedRun()
    {
        var project = _serializer.Clone(_projectSource());
        var registry = SampleProjects.CreateSimulation(project, seed: Name.GetHashCode()).Registry;
        return new TestRun(project.Definition, new TestRunOptions
        {
            Clock = _clock,
            Resources = _resources,
            Hardware = registry,
            AccessJournal = _coordinator?.Journal,
            Operator = this,
            StationName = Name,
            CodeRuntime = _codeRuntime,
        }, runId: $"{Name.Replace(' ', '-')}-Debug");
    }

    /// <summary>Werkerabbruch. Erneuter Abbruch beendet nur die aktuelle Exit-Gruppe.</summary>
    public void RequestAbort()
    {
        // Werkerabbruch beendet auch den Auswahl-/Auftragsablauf (Abschnitt 5)
        if (Selection.IsActive) Selection.End(IsRunning ? "Werkerabbruch – Auswahl-/Auftragsablauf beendet." : "Auswahl-/Auftragsablauf beendet.");
        if (!IsRunning) return;
        if (_run is null) { _abortBeforeEngineStart = true; return; }
        if (AbortCount == 0) BuildExitSequence(_run);
        _run.RequestAbort();
    }

    private void RaiseRunState()
    {
        OnPropertiesChanged(nameof(IsRunning), nameof(CanStart), nameof(CanAbort), nameof(IsAborting), nameof(UsesSelection), nameof(Hint));
        Selection.RaiseRunning();
        StartCommand.RaiseCanExecuteChanged();
        AbortCommand.RaiseCanExecuteChanged();
        Debug.RaiseRunState();
    }

    /// <summary>Nach jedem Lauf (auch Abbruch/Fehler): Geräte dieses Prüfplatzes in den sicheren Zustand (zusätzlich zu den Exit-Gruppen).</summary>
    private async Task<IReadOnlyList<string>> SafeStateAsync()
    {
        if (_coordinator is null || _simulationOnly) return [];
        try
        {
            return await _coordinator.SafeStateAsync(Name).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or StationSetupException)
        {
            return [ex.Message];
        }
    }

    /// <summary>„ (belegt von Prüfplatz 02)“ – wer die angeforderten Geräte gerade hält.</summary>
    private string Holders(IReadOnlyList<string> names)
    {
        var registry = _run?.Options.Hardware;
        var keys = names.Select(n => registry?.LockKeyOf(n) ?? n).ToHashSet(StringComparer.Ordinal);
        var owners = _resources.Snapshot().Where(o => keys.Contains(o.Resource) && o.OwnerRunId is not null && o.OwnerRunId != _run?.RunId)
            .Select(o => o.OwnerName ?? o.OwnerRunId!).Distinct().ToList();
        return owners.Count == 0 ? "" : $" (belegt von {string.Join(", ", owners)})";
    }

    private void ResetDisplay(TestDefinition definition)
    {
        LiveWaveform.Clear();
        LiveWaveformTitle = null;
        Overview.Clear();
        _overviewByNode.Clear();
        ExitSequence.Clear();
        ClearMeasurements();
        Contents.Clear();
        _contentById.Clear();
        Protocol.Clear();
        AbortCount = 0;
        DutCount = 0;
        DutRating = ResultRating.NResult;
        DutWerror = false;
        LastStatus = null;
        CurrentDutId = "—";
        CurrentOrder = "—";
        CurrentStepName = "";
        CurrentStepPath = "";

        var dut = FindDutGroup(definition.Loop) ?? definition.Loop;
        var number = 0;
        foreach (var child in dut.Children)
        {
            // Vom Entwickler ausgeblendete Schritte/Gruppen erscheinen nicht in der Ablaufübersicht.
            if (_storage is not null && !_storage.IsEnabled(child.Id, StorageTarget.Display)) continue;
            var item = new OverviewItemViewModel(child.Id, ++number, child.Name);
            Overview.Add(item);
            _overviewByNode[child.Id] = item;
            if (child is GroupDefinition g)
                foreach (var d in g.Descendants()) _overviewByNode[d.Id] = item;
        }

        _dutIdWriters.Clear();
        _orderWriters.Clear();
        foreach (var s in definition.AllNodes().OfType<StepDefinition>())
        {
            if (!s.Parameters.TryGetValue("Ziel", out var target)) continue;
            if (target == dut.DutIdVariable) _dutIdWriters.Add(s.Id);
            if (target == "Auftrag") _orderWriters.Add(s.Id);
        }
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private static GroupDefinition? FindDutGroup(GroupDefinition group)
        => group.IsDutRun ? group : group.Children.OfType<GroupDefinition>().Select(FindDutGroup).FirstOrDefault(g => g is not null);

    private void BuildExitSequence(TestRun run)
    {
        ExitSequence.Clear();
        var number = 0;
        var frames = run.ExecutionStack.Where(f => f.IsGroup).Reverse();
        foreach (var f in frames)
        {
            if (run.Definition.Find(f.NodeId) is not GroupDefinition { ExitGroup: { } exit } g) continue;
            var description = string.Join(", ", exit.Children.Take(3).Select(c => c.Name));
            var name = g == run.Definition.Root ? "Hauptablauf Finalization" : $"{exit.Name} ({g.Name})";
            ExitSequence.Add(new ExitItemViewModel(exit.Id, ++number, name, description));
        }
    }

    // ------------------------------------------------------------------ Ereignisse der Engine

    private void HandleInteractionAndResources(EngineEvent e)
    {
        switch (e)
        {
            case OperatorContentShown os:
                HandleOperatorContentShown(os);
                break;

            case OperatorContentCleared cl:
                HandleOperatorContentCleared(cl);
                break;

            case OperatorAbortRequested a:
                HandleOperatorAbortRequested(a);
                break;

            case ResourceWaitStarted rw:
                HandleResourceWaitStarted(rw);
                break;

            case ResourceAcquired:
                HandleResourceAcquired();
                break;

            case ResourceNotAcquired rn:
                HandleResourceNotAcquired(rn);
                break;

            case Diagnostic dg:
                HandleDiagnostic(dg);
                break;

            case DevelopmentEvent dev:
                HandleDevelopmentEvent(dev);
                break;

            case VersionActivated va:
                HandleVersionActivated(va);
                break;

            case DutRunCompleted dc:
                HandleDutRunCompleted(dc);
                break;

        }
    }

    private void Handle(EngineEvent e)
    {
        switch (e)
        {
            case StateChanged s:
                HandleStateChanged(s);
                break;

            case DutRunStarted d:
                HandleDutRunStarted(d);
                break;

            case StepStarted st:
                HandleStepStarted(st);
                break;

            case GroupEntered ge:
                HandleGroupEntered(ge);
                break;

            case GroupNotEntered gn:
                HandleGroupNotEntered(gn);
                break;

            case GroupCompleted gc:
                HandleGroupCompleted(gc);
                break;

            case ExitGroupAborted ea:
                HandleExitGroupAborted(ea);
                break;

            case StepCompleted sc:
                HandleStepCompleted(sc);
                break;

            default:
                HandleInteractionAndResources(e);
                break;

        }
        Debug.Handle(e);
    }

    private void HandleStateChanged(StateChanged s)
    {
        State = s.State;
        UpdateStatus();
    }

    private void HandleDutRunStarted(DutRunStarted d)
    {
        DutCount++;
        DutRating = ResultRating.NResult;
        DutWerror = false;
        CurrentDutId = "—";
        ClearMeasurements();
        foreach (var o in Overview)
        {
            o.State = ItemState.Pending;
            o.Rating = ResultRating.NResult;
        }
        AddProtocol(new ProtocolEntry(d.Time, ProtocolKind.Run, $"Durchlauf {DutCount} – Prüfling gestartet", "—", "", "", null, "Läuft", IsHeader: true));
    }

    private void HandleStepStarted(StepStarted st)
    {
        CurrentStepName = Last(st.Path);
        CurrentStepPath = st.Path;
        MarkOverview(st.NodeId, ItemState.Running, null);
    }

    private void HandleGroupEntered(GroupEntered ge)
    {
        if (_overviewByNode.TryGetValue(ge.NodeId, out var gi) && gi.NodeId == ge.NodeId) gi.State = ItemState.Running;
        SetExitState(ge.NodeId, ItemState.Running, null);
    }

    private void HandleGroupNotEntered(GroupNotEntered gn)
    {
        if (_overviewByNode.TryGetValue(gn.NodeId, out var ni) && ni.NodeId == gn.NodeId) ni.State = ItemState.Skipped;
        SetExitState(gn.NodeId, ItemState.Skipped, null);
    }

    private void HandleGroupCompleted(GroupCompleted gc)
    {
        if (_overviewByNode.TryGetValue(gc.NodeId, out var ci) && ci.NodeId == gc.NodeId)
        {
            ci.State = ItemState.Done;
            ci.Rating = gc.Rating;
        }
        if (ExitSequence.FirstOrDefault(x => x.NodeId == gc.NodeId) is { State: not ItemState.Aborted } x)
        {
            x.State = ItemState.Done;
            x.Rating = gc.Rating;
        }
    }

    private void HandleExitGroupAborted(ExitGroupAborted ea)
    {
        SetExitState(ea.NodeId, ItemState.Aborted, null);
    }

    private void HandleStepCompleted(StepCompleted sc)
    {
        OnStepCompleted(sc);
    }

    private void HandleOperatorContentShown(OperatorContentShown os)
    {
        ShowContent(os.Prompt);
    }

    private void HandleOperatorContentCleared(OperatorContentCleared cl)
    {
        foreach (var c in Contents.Where(c => c.Prompt.GroupScopeId == cl.GroupScopeId).ToList())
        {
            Contents.Remove(c);
            _contentById.Remove(c.DisplayId);
        }
        OnPropertyChanged(nameof(ActiveContent));
    }

    private void HandleOperatorAbortRequested(OperatorAbortRequested a)
    {
        AbortCount = a.Number;
        DutWerror = true;
        Hint = a.Number == 1
            ? "Werkerabbruch: reguläre Schritte gestoppt, Abschlussgruppen laufen von innen nach außen."
            : $"Erneuter Abbruch ({a.Number}): {a.Effect}";
        AddProtocol(new ProtocolEntry(a.Time, ProtocolKind.Abort, $"Werkerabbruch angefordert ({a.Number})", "—", "", "", null, "WERROR"));
        UpdateStatus();
    }

    private void HandleResourceWaitStarted(ResourceWaitStarted rw)
    {
        _waitingResource = string.Join(", ", rw.Resources) + Holders(rw.Resources);
    }

    private void HandleResourceAcquired()
    {
        _waitingResource = null;
    }

    private void HandleResourceNotAcquired(ResourceNotAcquired rn)
    {
        _waitingResource = null;
        AddProtocol(new ProtocolEntry(rn.Time, ProtocolKind.Error, $"{Last(rn.Path)}: Ressourcen nicht erhalten", "—", "", "", rn.Reason.Contains("Zeitlimit") ? ResultRating.Error : null, rn.Reason));
    }

    private void HandleDiagnostic(Diagnostic dg)
    {
        AddProtocol(new ProtocolEntry(dg.Time, ProtocolKind.Info, dg.Message, "—", "", "", null, "Hinweis"));
    }

    private void HandleDevelopmentEvent(DevelopmentEvent dev)
    {
        DevelopmentRecorded?.Invoke(dev);
    }

    private void HandleVersionActivated(VersionActivated va)
    {
        RunningVersion = va.ToVersion;
        OnPropertyChanged(nameof(RunningVersionText));
        AddProtocol(new ProtocolEntry(va.Time, ProtocolKind.Info, $"Freigegebene Version {va.ToVersion} aktiv ab diesem Prüfling (vorher {va.FromVersion})", "—", "", "", null, "Version"));
        Hint = $"Neue freigegebene Version {va.ToVersion} ist ab diesem Prüfling aktiv.";
    }

    private void HandleDutRunCompleted(DutRunCompleted dc)
    {
        Selection.RecordDut(dc);
        DutRating = dc.Status.Rating;
        DutWerror = dc.Status.OperatorAbort;
        if (!string.IsNullOrEmpty(dc.DutId)) CurrentDutId = dc.DutId;
        AddProtocol(new ProtocolEntry(dc.Time, ProtocolKind.Run, $"Prüfling {(dc.DutId == "" ? "ohne Kennung" : dc.DutId)} abgeschlossen", "—", "", "",
            dc.Status.Rating, dc.Status.OperatorAbort ? "WERROR" : "Abgeschlossen", IsHeader: true));
    }

    private void OnStepCompleted(StepCompleted sc)
    {
        if (sc.Value is WaveformSet { Channels.Count: > 0 } waves)
        {
            LiveWaveform.SetData(waves);
            LiveWaveformTitle = $"{Last(sc.Path)} · {sc.Time.ToLocalTime():HH:mm:ss} · {waves}";
        }
        if (_overviewByNode.TryGetValue(sc.NodeId, out var item))
        {
            item.Rating = item.Rating.Merge(sc.Rating);
            if (item.NodeId == sc.NodeId) item.State = sc.Cancelled ? ItemState.Aborted : ItemState.Done;
        }
        DutRating = DutRating.Merge(sc.Rating);

        var value = FormatValue(sc.Value);
        if (_dutIdWriters.Contains(sc.NodeId) && sc.Value is string id && id.Length > 0) CurrentDutId = id;
        if (_orderWriters.Contains(sc.NodeId) && sc.Value is string order && order.Length > 0) CurrentOrder = order;

        if (_storage is not null && !_storage.IsEnabled(sc.NodeId, StorageTarget.Display))
        {
            // Anzeige ausgeschlossen: Ablauf und Bewertung bleiben unverändert, nur keine Zeilen in Protokoll/Messwerten.
            OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
            return;
        }

        DisplayStepResult(sc, value);
    }

    private void DisplayStepResult(StepCompleted sc, string value)
    {
        var limits = sc.LowLimit is null && sc.HighLimit is null ? "—" : $"{Format.Number(sc.LowLimit)} – {Format.Number(sc.HighLimit)}";
        var isMeasurement = sc.LowLimit is not null || sc.HighLimit is not null || (sc.Unit is not null && sc.Value is double);
        if (isMeasurement && !sc.Cancelled)
        {
            var name = Last(sc.Path);
            SetMeasurement(new MeasurementRow(name, value, sc.Unit ?? "", limits, sc.Rating));
        }

        var status = sc.Cancelled ? "Abgebrochen" : sc.Rating == ResultRating.Error ? "Fehler" : "OK";
        AddProtocol(new ProtocolEntry(sc.Time, isMeasurement ? ProtocolKind.Measure : ProtocolKind.Step, Last(sc.Path), value, sc.Unit ?? "—", limits,
            sc.Rating, sc.Message is null ? status : $"{status}: {sc.Message}", Iteration: sc.Iteration > 1 ? sc.Iteration.ToString(CultureInfo.InvariantCulture) : ""));
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private void MarkOverview(string nodeId, ItemState state, ResultRating? rating)
    {
        if (!_overviewByNode.TryGetValue(nodeId, out var item)) return;
        item.State = state;
        if (rating is { } r) item.Rating = r;
        OnPropertiesChanged(nameof(ProgressText), nameof(DutProgress));
    }

    private void SetExitState(string nodeId, ItemState state, ResultRating? rating)
    {
        if (ExitSequence.FirstOrDefault(x => x.NodeId == nodeId) is not { } x) return;
        x.State = state;
        if (rating is not null) x.Rating = rating;
    }

    private void UpdateStatus()
    {
        if (!IsRunning && State is EngineState.Completed or EngineState.Idle)
        {
            StatusText = "Bereit";
            StatusKind = StationStatusKind.Idle;
            return;
        }
        (StatusText, StatusKind) = State switch
        {
            EngineState.WaitingResource => ($"Warten auf {_waitingResource ?? "Ressource"}", StationStatusKind.Waiting),
            EngineState.AwaitingOperator => ("Wartet auf Werker", StationStatusKind.Waiting),
            EngineState.PausedBeforeResultReturn => ("Pausiert", StationStatusKind.Paused),
            _ when AbortCount > 0 => ("Abschließen", StationStatusKind.Aborting),
            EngineState.Initializing => ("Initialisierung", StationStatusKind.Running),
            EngineState.Finalizing => ("Abschließen", StationStatusKind.Aborting),
            _ => ("Prüfung läuft", StationStatusKind.Running),
        };
        OnPropertyChanged(nameof(IsAborting));
    }

    private void AddProtocol(ProtocolEntry entry)
    {
        Protocol.Add(entry);
        while (Protocol.Count > MaxProtocol) Protocol.RemoveAt(0);
    }

    private static string Last(string path)
    {
        var i = path.LastIndexOf(" > ", StringComparison.Ordinal);
        return i < 0 ? path : path[(i + 3)..];
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "—",
        double d => Format.Number(d),
        bool b => b ? "true" : "false",
        string s => s,
        DateTime dt => dt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    // ------------------------------------------------------------------ Ergebnisdatei und Datenbank

    private async Task StoreResultAsync(ResultRun recorded, StorageResolver resolver)
    {
        StorageReport report;
        try
        {
            report = await _results!.ProcessAsync(recorded, resolver).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report = new StorageReport(recorded.RunId, recorded.DutId, new FileSaveResult(null, ex.Message, false), null);
        }
        _ui.Post(() => ApplyStorageReport(report));
    }

    private void ApplyStorageReport(StorageReport report)
    {
        StorageReports.Add(report);
        while (StorageReports.Count > 50) StorageReports.RemoveAt(0);
        var dut = report.DutId == "" ? "ohne Kennung" : report.DutId;
        var now = _clock.Now;
        if (report.File is { } file)
        {
            if (file.Success)
            {
                LastResultFile = file.Path;
                StorageError = null;
                AddProtocol(new ProtocolEntry(now, ProtocolKind.Info, $"Ergebnisdatei gespeichert ({dut})", "—", "", "", null, Path.GetFileName(file.Path!)));
            }
            else
            {
                StorageError = file.Recovered
                    ? $"{file.Error} Lokal gesichert: {file.Path}"
                    : file.Error;
                AddProtocol(new ProtocolEntry(now, ProtocolKind.Error, $"Ergebnisdatei für {dut} nicht gespeichert", "—", "", "", ResultRating.Error, StorageError ?? ""));
            }
        }
        if (report.Database is { Stored: false } db)
        {
            var warning = new StorageWarning(report.RunId, dut,
                $"Datenbank-Speicherung für Prüfling {dut} gescheitert. Die Daten liegen sicher in der lokalen Outbox und werden automatisch nachgereicht.",
                db.Error ?? "");
            StorageWarnings.Add(warning);
            AddProtocol(new ProtocolEntry(now, ProtocolKind.Error, $"DB-Speicherung für {dut} gescheitert – Outbox", "—", "", "", null, db.Error ?? ""));
            _ = HideWarningLater(warning);
        }
    }

    private async Task HideWarningLater(StorageWarning warning)
    {
        try { await WarningDelay(WarningDuration).ConfigureAwait(false); }
        catch (Exception) { /* Anzeige-Timer */ }
        _ui.Post(() => StorageWarnings.Remove(warning));
    }

    // ------------------------------------------------------------------ Werkerinteraktion

    private void ShowContent(OperatorPrompt prompt)
    {
        if (prompt.DisplayMode == OperatorDisplayMode.ReplacePrevious)
        {
            foreach (var old in Contents.Where(c => c.Prompt.DisplayMode == OperatorDisplayMode.ReplacePrevious && !c.IsAwaiting).ToList())
            {
                Contents.Remove(old);
                _contentById.Remove(old.DisplayId);
            }
        }
        var vm = new OperatorContentViewModel(prompt);
        Contents.Add(vm);
        _contentById[prompt.DisplayId] = vm;
        Hint = prompt.Text;
        OnPropertyChanged(nameof(ActiveContent));
    }

    private Task<object?> AskCoreAsync(string runId, OperatorPrompt prompt, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        _ui.Post(() =>
        {
            if (!_contentById.TryGetValue(prompt.DisplayId, out var vm))
            {
                ShowContent(prompt);
                vm = _contentById[prompt.DisplayId];
            }
            vm.Await(tcs);
            tcs.Task.ContinueWith(_ => _ui.Post(() => OnPropertyChanged(nameof(ActiveContent))), TaskScheduler.Default);
            OnPropertyChanged(nameof(ActiveContent));
        });
        return tcs.Task;
    }
}
