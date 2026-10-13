using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed record PlotPoint(double X, double Y);

/// <summary>Kurve für die Anzeige, bereits auf die Zeichenfläche (<see cref="ResultDocumentViewModel.PlotWidth"/> × <see cref="ResultDocumentViewModel.PlotHeight"/>) skaliert.</summary>
public sealed record PlotSeries(string Channel, int ColorIndex, IReadOnlyList<PlotPoint> Points, string Info);

public sealed record ResultHeaderItem(string Label, string Value);

public sealed record WaveformGroup(long EventSequence, string Title, IReadOnlyList<ResultWaveform> Waveforms);

/// <summary>Ereigniszeile im Reader.</summary>
public sealed record ResultEventRow(
    string Time,
    string Kind,
    string Path,
    string Value,
    string Unit,
    string Limits,
    ResultRating? Rating,
    string Message,
    string Iteration)
{
    public string RatingText => Rating?.ToDisplay() ?? "—";
}

/// <summary>Inhalt einer geöffneten Ergebnisdatei – gelesen ohne die zugehörige Testversion.</summary>
public sealed class ResultDocumentViewModel : ObservableObject
{
    public const double PlotWidth = 1000;
    public const double PlotHeight = 260;
    private const int MaxPlotPoints = 2000;

    private WaveformGroup? _selectedGroup;

    /// <summary>Prüflauf aus der Datenbank (mit Kurven) als Reader-Dokument.</summary>
    public ResultDocumentViewModel(ResultRun run)
        : this("(Datenbank) " + run.RunId, new ResultFileContent(run, [], ResultFile.FormatVersion, 0))
    {
    }

    public ResultDocumentViewModel(string path, ResultFileContent content)
    {
        Path = path;
        Content = content;
        var r = content.Run;
        Header =
        [
            new("Prüfling", r.DutId == "" ? "ohne Kennung" : r.DutId),
            new("Fertigungsauftrag", r.Order == "" ? "—" : r.Order),
            new("Bewertung", r.Status.ToString()),
            new("Test", $"{r.TestName} v{r.TestVersion}"),
            new("Prüfplatz", r.Station),
            new("Start", r.Started.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
            new("Ende", r.Ended.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
            new("Dauer", $"{(r.Ended - r.Started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"),
            new("Run-ID", r.RunId),
            new("Gefiltert für", r.FilteredFor == "" ? "—" : r.FilteredFor),
            new("Entwicklungslauf", r.IsDevelopmentRun ? "ja" : "nein"),
            new("Format", $"v{content.FormatVersion}, {content.FileSize / 1024.0:0.0} KiB"),
        ];
        Problems = content.Problems;
        foreach (var e in r.Events.Where(e => e.Kind != ResultEventKind.Group || e.Rating is not null)) Events.Add(ToRow(e));
        foreach (var g in r.Waveforms.GroupBy(w => w.EventSequence))
        {
            var ev = r.Events.FirstOrDefault(e => e.Sequence == g.Key);
            var title = ev is null ? $"Kurven #{g.Key}" : LastSegment(ev.Path);
            var list = g.ToList();
            WaveformGroups.Add(new WaveformGroup(g.Key, $"{title} – {list.Count} Kanal/Kanäle, {list[0].Data.Count} Samples", list));
        }
        SelectedGroup = WaveformGroups.FirstOrDefault();
    }

    public string Path { get; }
    public ResultFileContent Content { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public IReadOnlyList<ResultHeaderItem> Header { get; }
    public IReadOnlyList<string> Problems { get; }
    public bool IsIntact => Problems.Count == 0;
    public string IntegrityText => IsIntact ? "Integrität geprüft (CRC aller Chunks)" : $"{Problems.Count} Problem(e) – beschädigte Teile übersprungen";
    public ResultRating Rating => Content.Run.Rating;
    public ObservableCollection<ResultEventRow> Events { get; } = [];
    public ObservableCollection<WaveformGroup> WaveformGroups { get; } = [];
    public ObservableCollection<PlotSeries> Plot { get; } = [];
    public bool HasWaveforms => WaveformGroups.Count > 0;

    public string PlotRange { get; private set; } = "";

    public WaveformGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            BuildPlot();
        }
    }

    private void BuildPlot()
    {
        Plot.Clear();
        PlotRange = "";
        if (_selectedGroup is null || _selectedGroup.Waveforms.Count == 0)
        {
            OnPropertyChanged(nameof(PlotRange));
            return;
        }
        var waves = _selectedGroup.Waveforms.Select(w => w.Data).ToList();
        var min = waves.Min(w => w.Count == 0 ? 0 : w.Values.Min());
        var max = waves.Max(w => w.Count == 0 ? 0 : w.Values.Max());
        if (max - min < 1e-12) { max += 1; min -= 1; }
        var duration = waves.Max(w => w.Count == 0 ? 0 : w.TimeOf(w.Count - 1));
        if (duration <= 0) duration = 1;
        var i = 0;
        foreach (var w in waves)
        {
            var step = Math.Max(1, w.Count / MaxPlotPoints);
            var points = new List<PlotPoint>();
            for (var k = 0; k < w.Count; k += step)
                points.Add(new PlotPoint(w.TimeOf(k) / duration * PlotWidth, PlotHeight - (w.Values[k] - min) / (max - min) * PlotHeight));
            Plot.Add(new PlotSeries(w.Channel, i++, points,
                string.Create(CultureInfo.InvariantCulture, $"{w.Channel}: eff. {w.Rms():0.###} {w.Unit}")));
        }
        PlotRange = string.Create(CultureInfo.InvariantCulture,
            $"{min:0.###} … {max:0.###} {waves[0].Unit} · {duration * 1000:0.#} ms · {waves[0].SampleRate:0.##} Hz");
        OnPropertyChanged(nameof(PlotRange));
    }

    private static ResultEventRow ToRow(ResultEvent e)
    {
        var kind = EventKindText(e);
        var value = e.WaveformIds is { Count: > 0 } ids ? $"{ids.Count} Kurve(n)" : FormatValue(e.Value);
        var limits = e.LowLimit is null && e.HighLimit is null ? "—" : $"{Format.Number(e.LowLimit)} – {Format.Number(e.HighLimit)}";
        var message = e.Cancelled ? "Abgebrochen" + (e.Message is null ? "" : ": " + e.Message) : e.Message ?? "";
        if (e.AttachmentId is not null) message = (message + " [Bild eingebettet]").Trim();
        return new ResultEventRow(e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), kind, e.Path == "" ? "—" : e.Path, value,
            e.Unit ?? "", limits, e.Rating, message, e.Iteration > 1 ? e.Iteration.ToString(CultureInfo.InvariantCulture) : "");
    }

    private static string EventKindText(ResultEvent e)
    {
        return e.Kind switch
        {
            ResultEventKind.Step => e.LowLimit is not null || e.HighLimit is not null ? "Messung" : "Schritt",
            ResultEventKind.Group => "Gruppe",
            ResultEventKind.GroupNotEntered => "Nicht betreten",
            ResultEventKind.OperatorShown => "Werkeranzeige",
            ResultEventKind.OperatorAnswer => "Werkereingabe",
            ResultEventKind.Diagnostic => "Hinweis",
            ResultEventKind.Development => "Entwicklung",
            ResultEventKind.Abort => "Werkerabbruch",
            _ => e.Kind.ToString(),
        };
    }

    private static string FormatValue(object? v) => v switch
    {
        null => "—",
        double d => Format.Number(d),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf(" > ", StringComparison.Ordinal);
        return i < 0 ? path : path[(i + 3)..];
    }
}

/// <summary>Entwicklerseite „Ergebnisse“: Ergebnisdateien, Reader, Outbox-Status und Meldungen.</summary>
public sealed class ResultsViewModel : ObservableObject
{
    private const int MaxNotifications = 200;

    private readonly IUiDispatcher _ui;
    private readonly IFileDialogService? _dialogs;
    private ResultFileInfo? _selectedFile;
    private ResultDocumentViewModel? _document;
    private string? _error;

    public ResultsViewModel(ResultPipeline? pipeline, IUiDispatcher ui, IFileDialogService? dialogs, Func<string>? currentUser = null)
    {
        Pipeline = pipeline;
        _ui = ui;
        _dialogs = dialogs;
        _currentUser = currentUser ?? (() => Environment.UserName);
        ReportPdfCommand = new RelayCommand(() =>
        {
            if (_document is { } d && _dialogs?.SaveFile("Prüfbericht als PDF", "PDF (*.pdf)|*.pdf", Path.ChangeExtension(d.FileName, ".pdf")) is { } p) ExportReport(p);
        }, () => _document is not null);
        ReportCsvCommand = new RelayCommand(() =>
        {
            if (_document is { } d && _dialogs?.SaveFile("Prüfdaten als CSV", "CSV für Excel (*.csv)|*.csv", Path.ChangeExtension(d.FileName, ".csv")) is { } p) ExportCsv(p);
        }, () => _document is not null);
        RecoverCommand = new RelayCommand(() => _ = RecoverSelectedAsync(), () => _selectedIncomplete is not null && !_recovering);
        DiscardIncompleteCommand = new RelayCommand(DiscardSelected, () => _selectedIncomplete is not null && !_recovering);
        RefreshCommand = new RelayCommand(Refresh);
        RestoreRecoveryCommand = new RelayCommand(RestoreRecovery, () => PendingRecoveryCount > 0);
        RetryNowCommand = new RelayCommand(() => _ = Outbox?.RetryNowAsync(), () => Outbox is not null && Outbox.Pending.Count > 0);
        OpenFolderCommand = new RelayCommand(() =>
        {
            if (Files?.Folder is { } f)
            {
                Directory.CreateDirectory(f);
                _dialogs?.OpenExternal(f);
            }
        }, () => Files is not null);
        if (Outbox is not null)
        {
            Outbox.Changed += () => _ui.Post(RefreshOutbox);
            Outbox.Notified += n => _ui.Post(() =>
            {
                Notifications.Insert(0, n);
                while (Notifications.Count > MaxNotifications) Notifications.RemoveAt(Notifications.Count - 1);
            });
        }
        Refresh();
    }

    private readonly Func<string> _currentUser;
    private IncompleteRun? _selectedIncomplete;
    private bool _recovering;
    private string? _recoveryStatus;

    public ResultPipeline? Pipeline { get; }

    public RelayCommand ReportPdfCommand { get; }
    public RelayCommand ReportCsvCommand { get; }

    /// <summary>Prüfbericht (PDF) der geöffneten Ergebnisdatei.</summary>
    public void ExportReport(string path)
    {
        if (_document is not { } d) return;
        try
        {
            Testumgebung.Persistence.Reports.Reports.TestReport(d.Content.Run, d.Path).Save(path);
            Error = null;
            _dialogs?.OpenExternal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "Prüfbericht nicht gespeichert: " + ex.Message;
        }
    }

    public void ExportCsv(string path)
    {
        if (_document is not { } d) return;
        try
        {
            Testumgebung.Persistence.Reports.ResultAnalysis.SaveCsv(path, Testumgebung.Persistence.Reports.ResultAnalysis.RunCsv(d.Content.Run));
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "CSV nicht gespeichert: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ Unvollständige Prüfläufe (Absturz, Stromausfall)

    public ObservableCollection<IncompleteRun> Incomplete { get; } = [];
    public bool HasIncomplete => Incomplete.Count > 0;
    public string IncompleteSummary => Incomplete.Count == 0 ? "Keine unvollständigen Prüfläufe."
        : $"{Incomplete.Count} unvollständige(r) Prüflauf/-läufe nach Programmende, Absturz oder Stromausfall – bitte als abgebrochen speichern oder verwerfen. Es wird nicht automatisch weitergeprüft.";
    public RelayCommand RecoverCommand { get; }
    public RelayCommand DiscardIncompleteCommand { get; }

    public IncompleteRun? SelectedIncomplete
    {
        get => _selectedIncomplete;
        set
        {
            if (!SetProperty(ref _selectedIncomplete, value)) return;
            RecoverCommand.RaiseCanExecuteChanged();
            DiscardIncompleteCommand.RaiseCanExecuteChanged();
        }
    }

    public string? RecoveryStatus
    {
        get => _recoveryStatus;
        private set => SetProperty(ref _recoveryStatus, value);
    }

    public void RefreshIncomplete()
    {
        var selected = _selectedIncomplete?.Path;
        Incomplete.Clear();
        foreach (var r in Pipeline?.Journal?.FindIncomplete() ?? []) Incomplete.Add(r);
        _selectedIncomplete = Incomplete.FirstOrDefault(r => r.Path == selected) ?? Incomplete.FirstOrDefault();
        OnPropertiesChanged(nameof(SelectedIncomplete), nameof(HasIncomplete), nameof(IncompleteSummary));
        RecoverCommand.RaiseCanExecuteChanged();
        DiscardIncompleteCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Als abgebrochen (ERROR) speichern: Ergebnisdatei und Datenbank wie ein regulärer Prüflingstest, mit Hinweis.</summary>
    public async Task RecoverSelectedAsync()
    {
        if (_selectedIncomplete is not { } run || Pipeline is null || _recovering) return;
        _recovering = true;
        RecoverCommand.RaiseCanExecuteChanged();
        try
        {
            var report = await Pipeline.RecoverAsync(run, DateTimeOffset.Now, _currentUser()).ConfigureAwait(true);
            RecoveryStatus = report.FileFailed && report.File?.Path is null && report.Database is not { Stored: true }
                ? $"{run.Title}: nicht gespeichert – {report.File?.Error ?? report.Database?.Error}"
                : $"{run.Title}: als abgebrochen (ERROR) gespeichert{(report.File?.Path is { } p ? " – " + Path.GetFileName(p) : "")}.";
        }
        finally
        {
            _recovering = false;
            RefreshIncomplete();
            Refresh();
        }
    }

    // ------------------------------------------------------------------ Wiederherstellungsordner

    public RelayCommand RestoreRecoveryCommand { get; }

    /// <summary>Ergebnisdateien, die beim Speichern im Wiederherstellungsordner gelandet sind (Ergebnisordner war nicht erreichbar).</summary>
    public int PendingRecoveryCount => Files?.PendingRecovery().Count ?? 0;

    public string RecoveryFolderSummary => PendingRecoveryCount switch
    {
        0 => "",
        var n => $"{n} Ergebnisdatei(en) liegen im Wiederherstellungsordner {Files?.RecoveryFolder} – der Ergebnisordner war beim Speichern nicht erreichbar.",
    };

    /// <summary>Dateien in den Ergebnisordner zurückkopieren.</summary>
    public void RestoreRecovery()
    {
        if (Files is not { } files) return;
        var (restored, problems) = files.RestoreRecovered();
        RecoveryStatus = problems.Count == 0
            ? $"{restored} Ergebnisdatei(en) in den Ergebnisordner zurückkopiert."
            : $"{restored} zurückkopiert, {problems.Count} Problem(e): {string.Join(" ", problems.Take(3))}";
        Refresh();
    }

    public void DiscardSelected()
    {
        if (_selectedIncomplete is not { } run || Pipeline?.Journal is not { } journal) return;
        try
        {
            journal.Discard(run);
            RecoveryStatus = $"{run.Title}: verworfen (Journal unter „{RunJournal.DiscardedFolderName}“ aufbewahrt).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecoveryStatus = "Verwerfen fehlgeschlagen: " + ex.Message;
        }
        RefreshIncomplete();
    }
    public ResultFileStore? Files => Pipeline?.Files;
    public OutboxService? Outbox => Pipeline?.Outbox;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RetryNowCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public ObservableCollection<ResultFileInfo> ResultFiles { get; } = [];
    public ObservableCollection<OutboxEntry> Pending { get; } = [];
    public ObservableCollection<RunNotification> Notifications { get; } = [];

    public string ResultFolder => Files?.Folder ?? "Dateispeicherung nicht konfiguriert";
    public string OutboxFolder => Outbox?.Folder ?? "—";
    public string DatabaseName => HasDatabase ? Outbox!.Database.Name : "Keine Datenbank konfiguriert";

    /// <summary>Nach Einstellungsänderung Anzeige auffrischen.</summary>
    public void SettingsChanged() => RefreshOutbox();
    public bool HasDatabase => Outbox is not null && Pipeline!.DatabaseEnabled;
    public bool CanSimulateOutage => Outbox?.Database switch
    {
        SwitchableRunDatabase s => s.CanSimulateOutage,
        ISimulatedDatabase => true,
        _ => false,
    };

    /// <summary>Simulierter DB-Ausfall (nur simulierte Datenbank).</summary>
    public bool DatabaseOffline
    {
        get => Outbox?.Database is ISimulatedDatabase { Offline: true };
        set
        {
            if (Outbox?.Database is not ISimulatedDatabase db || db.Offline == value) return;
            db.Offline = value;
            OnPropertyChanged();
            if (!value) _ = Outbox.RetryNowAsync();
        }
    }

    public string OutboxSummary => Outbox is null ? "Keine Datenbank konfiguriert – Outbox inaktiv."
        : !HasDatabase ? (Pending.Count == 0 ? "Keine Datenbank konfiguriert." : $"Keine Datenbank konfiguriert – {Pending.Count} Run(s) bleiben in der Outbox.")
        : Pending.Count == 0 ? "Outbox leer – alle Runs übertragen." : $"{Pending.Count} Run(s) warten auf Übertragung.";

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public ResultFileInfo? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!SetProperty(ref _selectedFile, value)) return;
            Open(value?.Path);
        }
    }

    public ResultDocumentViewModel? Document
    {
        get => _document;
        private set
        {
            if (!SetProperty(ref _document, value)) return;
            ReportPdfCommand.RaiseCanExecuteChanged();
            ReportCsvCommand.RaiseCanExecuteChanged();
        }
    }

    public void Refresh()
    {
        var selected = _selectedFile?.Path;
        ResultFiles.Clear();
        try
        {
            foreach (var f in Files?.List() ?? []) ResultFiles.Add(f);
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        _selectedFile = ResultFiles.FirstOrDefault(f => f.Path == selected);
        OnPropertiesChanged(nameof(SelectedFile), nameof(PendingRecoveryCount), nameof(RecoveryFolderSummary));
        RestoreRecoveryCommand?.RaiseCanExecuteChanged();
        RefreshOutbox();
        if (!_recovering) RefreshIncomplete();
    }

    /// <summary>Datei öffnen (auch außerhalb des Ergebnisordners).</summary>
    public void Open(string? path)
    {
        if (path is null)
        {
            Document = null;
            return;
        }
        try
        {
            Document = new ResultDocumentViewModel(path, ResultFile.Read(path));
            Error = null;
        }
        catch (ResultFileException ex)
        {
            Document = null;
            Error = ex.Message;
        }
    }

    private void RefreshOutbox()
    {
        Pending.Clear();
        foreach (var e in Outbox?.Pending ?? []) Pending.Add(e);
        OnPropertiesChanged(nameof(OutboxSummary), nameof(DatabaseOffline), nameof(DatabaseName), nameof(CanSimulateOutage), nameof(HasDatabase));
        RetryNowCommand.RaiseCanExecuteChanged();
    }
}
