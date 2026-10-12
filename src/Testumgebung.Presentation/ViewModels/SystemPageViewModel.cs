using System.Collections.ObjectModel;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Pilot;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Datei einer geprüften Sicherung mit Auswahl zum Wiederherstellen.</summary>
public sealed class BackupFileRow(BackupFileEntry entry, string? target) : ObservableObject
{
    private bool _selected = target is not null;

    public BackupFileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string? Target { get; } = target;
    public string TargetText => Target ?? "kein Ziel auf diesem Rechner";
    public bool CanRestore => Target is not null;
    public string SecretsText => Entry.ContainsProtectedSecrets ? "enthält DPAPI-geschützte Geheimnisse" : "";

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value && CanRestore);
    }
}

/// <summary>
/// System &amp; Sicherung (Pilot): Systemprüfung des Prüfrechners, Konfiguration sichern und wiederherstellen.
/// </summary>
public sealed class SystemPageViewModel : ObservableObject
{
    private const string ZipFilter = "Konfigurationssicherung (*.zip)|*.zip|Alle Dateien (*.*)|*.*";

    private readonly Func<SystemCheckContext> _context;
    private readonly IReadOnlyList<BackupSource> _sources;
    private readonly IFileDialogService? _dialogs;
    private readonly Func<bool> _anyRunning;
    private readonly Action? _afterRestore;
    private readonly IUiDispatcher _ui;
    private bool _busy;
    private bool _probeDevices;
    private string? _checkSummary;
    private string? _backupStatus;
    private string? _restorePath;
    private BackupInspection? _inspection;

    public SystemPageViewModel(Func<SystemCheckContext> context, IReadOnlyList<BackupSource> sources, IFileDialogService? dialogs,
        Func<bool> anyRunning, IUiDispatcher ui, Action? afterRestore = null, Testumgebung.Workers.CodeRuntime? pythonRuntime = null)
    {
        _context = context;
        _sources = sources;
        _dialogs = dialogs;
        _anyRunning = anyRunning;
        _ui = ui;
        _afterRestore = afterRestore;
        Python = new PythonEnvironmentViewModel(pythonRuntime, anyRunning);
        RunCheckCommand = new RelayCommand(() => _ = RunCheckAsync(), () => !_busy);
        BackupCommand = new RelayCommand(() =>
        {
            var path = _dialogs?.SaveFile("Konfiguration sichern", ZipFilter, $"Testumgebung-Konfiguration-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            if (path is not null) Backup(path);
        });
        OpenBackupCommand = new RelayCommand(() =>
        {
            var path = _dialogs?.OpenFile("Sicherung öffnen", ZipFilter);
            if (path is not null) InspectBackup(path);
        });
        RestoreCommand = new RelayCommand(Restore, () => _inspection is { IsIntact: true } && BackupFiles.Any(f => f.Selected));
    }

    public IReadOnlyList<BackupSource> Sources => _sources;
    public PythonEnvironmentViewModel Python { get; }

    // ------------------------------------------------------------------ Systemprüfung

    public RelayCommand RunCheckCommand { get; }
    public ObservableCollection<SystemCheckItem> Checks { get; } = [];

    public bool ProbeDevices
    {
        get => _probeDevices;
        set => SetProperty(ref _probeDevices, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) RunCheckCommand.RaiseCanExecuteChanged();
        }
    }

    public string? CheckSummary
    {
        get => _checkSummary;
        private set => SetProperty(ref _checkSummary, value);
    }

    public bool CheckPassed => Checks.Count > 0 && Checks.All(c => c.Level != CheckLevel.Error);

    public async Task RunCheckAsync()
    {
        if (_busy) return;
        IsBusy = true;
        CheckSummary = "Systemprüfung läuft …";
        try
        {
            var context = _context();
            var probe = _probeDevices;
            var items = await Task.Run(() => SystemCheck.RunAsync(new SystemCheckContext
            {
                Services = context.Services,
                Coordinator = context.Coordinator,
                StationStore = context.StationStore,
                AccessStore = context.AccessStore,
                PythonExecutable = context.PythonExecutable,
                ProbeDevices = probe,
                MinimumFreeBytes = context.MinimumFreeBytes,
                RunProcess = context.RunProcess,
                VisaAvailable = context.VisaAvailable,
                DaqmxAvailable = context.DaqmxAvailable,
            })).ConfigureAwait(true);
            Checks.Clear();
            foreach (var i in items.OrderByDescending(i => i.Level)) Checks.Add(i);
            var errors = items.Count(i => i.Level == CheckLevel.Error);
            var warnings = items.Count(i => i.Level == CheckLevel.Warning);
            CheckSummary = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss}: {items.Count} Prüfpunkte – {errors} Fehler, {warnings} Warnung(en)."
                           + (errors == 0 ? " Bereit für den Pilotbetrieb, sofern die Warnungen bewusst akzeptiert sind." : " Fehler vor dem Pilot beheben.");
            OnPropertyChanged(nameof(CheckPassed));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ Sicherung

    public RelayCommand BackupCommand { get; }
    public RelayCommand OpenBackupCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public ObservableCollection<BackupFileRow> BackupFiles { get; } = [];
    public ObservableCollection<string> BackupMessages { get; } = [];

    public string? BackupStatus
    {
        get => _backupStatus;
        private set => SetProperty(ref _backupStatus, value);
    }

    public string? RestorePath
    {
        get => _restorePath;
        private set => SetProperty(ref _restorePath, value);
    }

    public string InspectionText => _inspection is null ? ""
        : $"Sicherung vom {_inspection.Manifest.Created.ToLocalTime():dd.MM.yyyy HH:mm} · {_inspection.Manifest.User}@{_inspection.Manifest.Machine}"
          + (_inspection.IsIntact ? " · Prüfsummen in Ordnung" : " · BESCHÄDIGT – Wiederherstellen gesperrt");

    public void Backup(string path)
    {
        try
        {
            var manifest = ConfigBackup.Create(path, _sources, DateTimeOffset.Now);
            var missing = _sources.Where(s => manifest.Files.All(f => f.Name != s.Name)).Select(s => s.Name).ToList();
            BackupStatus = $"Gesichert: {string.Join(", ", manifest.Files.Select(f => f.Name))} → {path}"
                           + (missing.Count > 0 ? $" (nicht vorhanden: {string.Join(", ", missing)})" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupStatus = "Sicherung fehlgeschlagen: " + ex.Message;
        }
    }

    public void InspectBackup(string path)
    {
        RestorePath = path;
        _inspection = ConfigBackup.Inspect(path);
        BackupFiles.Clear();
        BackupMessages.Clear();
        foreach (var f in _inspection.Manifest.Files)
            BackupFiles.Add(new BackupFileRow(f, _sources.FirstOrDefault(s => s.Name == f.Name)?.Path));
        foreach (var p in _inspection.Problems) BackupMessages.Add("Fehler: " + p);
        foreach (var w in _inspection.Warnings) BackupMessages.Add("Hinweis: " + w);
        foreach (var row in BackupFiles) row.PropertyChanged += (_, _) => RestoreCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(InspectionText));
        RestoreCommand.RaiseCanExecuteChanged();
    }

    public void Restore()
    {
        if (_inspection is not { IsIntact: true } || _restorePath is null) return;
        if (_anyRunning())
        {
            BackupStatus = "Nicht wiederhergestellt: Während einer Prüfung kann die Konfiguration nicht ersetzt werden.";
            return;
        }
        var targets = BackupFiles.Where(f => f.Selected && f.Target is not null).ToDictionary(f => f.Name, f => f.Target!);
        try
        {
            var result = ConfigBackup.Restore(_restorePath, targets, DateTimeOffset.Now);
            BackupStatus = $"Wiederhergestellt: {string.Join(", ", result.Restored.Select(Path.GetFileName))}. "
                           + (result.PreviousCopies.Count > 0 ? "Vorherige Stände daneben gesichert. " : "")
                           + "Anwendung neu starten, damit Einstellungen und Zugang vollständig wirksam werden.";
            _ui.Post(() => _afterRestore?.Invoke());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BackupStatus = "Wiederherstellung fehlgeschlagen: " + ex.Message;
        }
    }
}
