/**
 * @file
 * @brief Vermittelt zwischen Engine-Ereignissen, Werkerinteraktion und UI-Zustand eines Prüfplatzes.
 */

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
        if (releases is not null) releases.Changed += () => _ui.Post(() => { OnPropertiesChanged(nameof(ReleasedVersionText), nameof(HasRelease), nameof(AvailablePrograms)); UpdateProgramVersionWarning(); });
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
    public Func<TimeSpan, Task> WarningDelay { get; set; } = duration => Task.Delay(duration);

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
        _releases.CurrentInfo(_projectSource().Definition.Name) is { } release ? $"v{release.Version} · Freigegeben" : "keine freigegebene Version";

    public string RunningVersionText => RunningVersion.Length == 0 ? ""
        : _runIsDraft ? $"Entwurf v{RunningVersion} (nicht freigegeben)"
        : ActiveRelease is { } release ? $"v{release.Version} · Freigegeben" : $"v{RunningVersion}";

    /// <summary>
    /// <see cref="IVersionSource"/>: neue Freigabe erst beim nächsten Prüfling (V01), nur wenn verträglich;
    /// sonst gilt sie ab dem nächsten Start des Hauptablaufs.
    /// </summary>
    TestDefinition? IVersionSource.NextDefinition(TestDefinition current)
    {
        if (_releases is null || _runIsDraft || _runningProject is null) return null;
        // Central source releases are bound to the whole order session, never swapped between serial numbers.
        if (_releases.Catalog is not null) return null;
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
        if (ActiveRelease is { } release)
            recorded.Application += $" · Release-ID={release.ReleaseId} · Paket-SHA256={release.PackageSha256}" +
                (release.Source is { } source ? $" · {source.Source.Provider}={source.Revision} · Repository={source.RepositoryIdentity} · Linie={source.Source.Line}" : "");
        var task = StoreResultAsync(recorded, resolver);
        lock (_storageGate) _storageTasks.Add(task);
        _ = task.ContinueWith(task2 => { lock (_storageGate) _storageTasks.Remove(task2); },
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

    public string DutResultText => new RunStatus(DutRating, DutWerror).ToString();

    public ResultRating DutRating
    {
        get => _dutRating;
        private set { if (SetProperty(ref _dutRating, value)) OnPropertyChanged(nameof(DutResultText)); }
    }

    public bool DutWerror
    {
        get => _dutWerror;
        private set { if (SetProperty(ref _dutWerror, value)) OnPropertyChanged(nameof(DutResultText)); }
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

    public int CurrentStepIndex => Overview.FirstOrDefault(overviewItemViewModel => overviewItemViewModel.State == ItemState.Running)?.Number ?? Overview.Count(overviewItemViewModel => overviewItemViewModel.State == ItemState.Done);

    public LiveOutputCollection LiveOutputs { get; } = new();

    public string ProgressText => Overview.Count == 0
        ? ""
        : $"Durchlauf {Math.Max(DutCount, 1)} • Schritt {Math.Max(CurrentStepIndex, 1)} von {Overview.Count}";

    public double DutProgress => Overview.Count == 0 ? 0 : 100.0 * Overview.Count(overviewItemViewModel => overviewItemViewModel.State is ItemState.Done or ItemState.Skipped) / Overview.Count;

    public OperatorContentViewModel? ActiveContent => Contents.LastOrDefault(operatorContentViewModel => operatorContentViewModel.IsAwaiting) ?? Contents.LastOrDefault();
}
