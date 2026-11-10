/**
 * @file
 * @brief Lädt gespeicherte Prüfläufe für Detailansicht, Kurvenanzeige und Wiederherstellung unvollständiger Ergebnisse.
 */

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
        var run = content.Run;
        Header =
        [
            new("Prüfling", run.DutId == "" ? "ohne Kennung" : run.DutId),
            new("Fertigungsauftrag", run.Order == "" ? "—" : run.Order),
            new("Bewertung", run.Status.ToString()),
            new("Test", $"{run.TestName} v{run.TestVersion}"),
            new("Prüfplatz", run.Station),
            new("Start", run.Started.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
            new("Ende", run.Ended.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)),
            new("Dauer", $"{(run.Ended - run.Started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s"),
            new("Run-ID", run.RunId),
            new("Gefiltert für", run.FilteredFor == "" ? "—" : run.FilteredFor),
            new("Entwicklungslauf", run.IsDevelopmentRun ? "ja" : "nein"),
            new("Format", $"v{content.FormatVersion}, {content.FileSize / 1024.0:0.0} KiB"),
        ];
        Problems = content.Problems;
        foreach (var resultEvent in run.Events.Where(resultEvent2 => resultEvent2.Kind != ResultEventKind.Group || resultEvent2.Rating is not null)) Events.Add(ToRow(resultEvent));
        foreach (var group in run.Waveforms.GroupBy(waveform => waveform.EventSequence))
        {
            var ev = run.Events.FirstOrDefault(resultEvent2 => resultEvent2.Sequence == group.Key);
            var title = ev is null ? $"Kurven #{group.Key}" : LastSegment(ev.Path);
            var list = group.ToList();
            WaveformGroups.Add(new WaveformGroup(group.Key, $"{title} – {list.Count} Kanal/Kanäle, {list[0].Data.Count} Samples", list));
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
    public string Outcome => Content.Run.Status.ToString();
    public ResultRating OutcomeRating => Content.Run.OperatorAbort ? ResultRating.Error : Rating;
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
        var waves = _selectedGroup.Waveforms.Select(waveform => waveform.Data).ToList();
        var min = waves.Min(waveform => waveform.Count == 0 ? 0 : waveform.Values.Min());
        var max = waves.Max(waveform => waveform.Count == 0 ? 0 : waveform.Values.Max());
        if (max - min < 1e-12) { max += 1; min -= 1; }
        var duration = waves.Max(waveform => waveform.Count == 0 ? 0 : waveform.TimeOf(waveform.Count - 1));
        if (duration <= 0) duration = 1;
        var index = 0;
        foreach (var waveform in waves)
        {
            var step = Math.Max(1, waveform.Count / MaxPlotPoints);
            var points = new List<PlotPoint>();
            for (var index2 = 0; index2 < waveform.Count; index2 += step)
                points.Add(new PlotPoint(waveform.TimeOf(index2) / duration * PlotWidth, PlotHeight - (waveform.Values[index2] - min) / (max - min) * PlotHeight));
            Plot.Add(new PlotSeries(waveform.Channel, index++, points,
                string.Create(CultureInfo.InvariantCulture, $"{waveform.Channel}: eff. {waveform.Rms():0.###} {waveform.Unit}")));
        }
        PlotRange = string.Create(CultureInfo.InvariantCulture,
            $"{min:0.###} … {max:0.###} {waves[0].Unit} · {duration * 1000:0.#} ms · {waves[0].SampleRate:0.##} Hz");
        OnPropertyChanged(nameof(PlotRange));
    }

    private static ResultEventRow ToRow(ResultEvent resultEvent)
    {
        var kind = EventKindText(resultEvent);
        var value = resultEvent.WaveformIds is { Count: > 0 } ids ? $"{ids.Count} Kurve(n)" : FormatValue(resultEvent.Value);
        var limits = resultEvent.LowLimit is null && resultEvent.HighLimit is null ? "—" : $"{Format.Number(resultEvent.LowLimit)} – {Format.Number(resultEvent.HighLimit)}";
        var message = resultEvent.Cancelled ? "Abgebrochen" + (resultEvent.Message is null ? "" : ": " + resultEvent.Message) : resultEvent.Message ?? "";
        if (resultEvent.AttachmentId is not null) message = (message + " [Bild eingebettet]").Trim();
        return new ResultEventRow(resultEvent.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), kind, resultEvent.Path == "" ? "—" : resultEvent.Path, value,
            resultEvent.Unit ?? "", limits, resultEvent.Rating, message, resultEvent.Iteration > 1 ? resultEvent.Iteration.ToString(CultureInfo.InvariantCulture) : "");
    }

    private static string EventKindText(ResultEvent resultEvent)
    {
        return resultEvent.Kind switch
        {
            ResultEventKind.Step => resultEvent.LowLimit is not null || resultEvent.HighLimit is not null ? "Messung" : "Schritt",
            ResultEventKind.Group => "Gruppe",
            ResultEventKind.GroupNotEntered => "Nicht betreten",
            ResultEventKind.OperatorShown => "Werkeranzeige",
            ResultEventKind.OperatorAnswer => "Werkereingabe",
            ResultEventKind.Diagnostic => "Hinweis",
            ResultEventKind.Development => "Entwicklung",
            ResultEventKind.Abort => "Werkerabbruch",
            _ => resultEvent.Kind.ToString(),
        };
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "—",
        double number => Format.Number(number),
        bool flag => flag ? "true" : "false",
        DateTime dt => dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string LastSegment(string path)
    {
        var index = path.LastIndexOf(" > ", StringComparison.Ordinal);
        return index < 0 ? path : path[(index + 3)..];
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
            if (_document is { } resultDocumentViewModel && _dialogs?.SaveFile("Prüfbericht als PDF", "PDF (*.pdf)|*.pdf", Path.ChangeExtension(resultDocumentViewModel.FileName, ".pdf")) is { } path) ExportReport(path);
        }, () => _document is not null);
        ReportCsvCommand = new RelayCommand(() =>
        {
            if (_document is { } resultDocumentViewModel && _dialogs?.SaveFile("Prüfdaten als CSV", "CSV für Excel (*.csv)|*.csv", Path.ChangeExtension(resultDocumentViewModel.FileName, ".csv")) is { } path) ExportCsv(path);
        }, () => _document is not null);
        RecoverCommand = new RelayCommand(() => _ = RecoverSelectedAsync(), () => _selectedIncomplete is not null && !_recovering);
        DiscardIncompleteCommand = new RelayCommand(DiscardSelected, () => _selectedIncomplete is not null && !_recovering);
        RefreshCommand = new RelayCommand(Refresh);
        RestoreRecoveryCommand = new RelayCommand(RestoreRecovery, () => PendingRecoveryCount > 0);
        RetryNowCommand = new RelayCommand(() => _ = Outbox?.RetryNowAsync(), () => Outbox is not null && Outbox.Pending.Count > 0);
        OpenFolderCommand = new RelayCommand(() =>
        {
            if (Files?.Folder is { } fileName)
            {
                Directory.CreateDirectory(fileName);
                _dialogs?.OpenExternal(fileName);
            }
        }, () => Files is not null);
        if (Outbox is not null)
        {
            Outbox.Changed += () => _ui.Post(RefreshOutbox);
            Outbox.Notified += runNotification => _ui.Post(() =>
            {
                Notifications.Insert(0, runNotification);
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
        if (_document is not { } resultDocumentViewModel) return;
        try
        {
            Testumgebung.Persistence.Reports.Reports.TestReport(resultDocumentViewModel.Content.Run, resultDocumentViewModel.Path).Save(path);
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
        if (_document is not { } resultDocumentViewModel) return;
        try
        {
            Testumgebung.Persistence.Reports.ResultAnalysis.SaveCsv(path, Testumgebung.Persistence.Reports.ResultAnalysis.RunCsv(resultDocumentViewModel.Content.Run));
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
        foreach (var incompleteRun in Pipeline?.Journal?.FindIncomplete() ?? []) Incomplete.Add(incompleteRun);
        _selectedIncomplete = Incomplete.FirstOrDefault(incompleteRun2 => incompleteRun2.Path == selected) ?? Incomplete.FirstOrDefault();
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
                : $"{run.Title}: als abgebrochen (ERROR) gespeichert{(report.File?.Path is { } path ? " – " + Path.GetFileName(path) : "")}.";
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
        var count => $"{count} Ergebnisdatei(en) liegen im Wiederherstellungsordner {Files?.RecoveryFolder} – der Ergebnisordner war beim Speichern nicht erreichbar.",
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
        SwitchableRunDatabase switchableRunDatabase => switchableRunDatabase.CanSimulateOutage,
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
            foreach (var resultFileInfo in Files?.List() ?? []) ResultFiles.Add(resultFileInfo);
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        _selectedFile = ResultFiles.FirstOrDefault(resultFileInfo2 => resultFileInfo2.Path == selected);
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
        foreach (var outboxEntry in Outbox?.Pending ?? []) Pending.Add(outboxEntry);
        OnPropertiesChanged(nameof(OutboxSummary), nameof(DatabaseOffline), nameof(DatabaseName), nameof(CanSimulateOutage), nameof(HasDatabase));
        RetryNowCommand.RaiseCanExecuteChanged();
    }
}
