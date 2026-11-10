/**
 * @file
 * @brief Wählt Prüfläufe für statistische Auswertung und PDF-Berichte in der Oberfläche aus.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Balken eines Histogramms bzw. einer Tagesübersicht (Höhe 0…1).</summary>
public sealed record BarItem(string Label, double Height, string Tooltip, bool Highlight = false);

/// <summary>
/// Auswertung &amp; Berichte: Ergebnisdateien laden, filtern (Zeitraum, Test, Version, Prüfplatz, Kennung), Kennzahlen (Ausbeute,
/// FPY, Endausbeute), Fehler-Pareto, Messwertstatistik mit Cpk und Histogramm, Tagesverlauf; Export als PDF und CSV.
/// </summary>
public sealed class ReportsViewModel : ObservableObject
{
    private const string PdfFilter = "PDF (*.pdf)|*.pdf";
    private const string CsvFilter = "CSV für Excel (*.csv)|*.csv";
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly Func<string?> _defaultFolder;
    private readonly IFileDialogService? _dialogs;
    private readonly IUiDispatcher _ui;
    private LoadedResults? _loaded;
    private ResultStatistics? _stats;
    private string _folder = "";
    private bool _busy;
    private string? _status;
    private DateTime? _from;
    private DateTime? _to;
    private string _test = "";
    private string _version = "";
    private string _station = "";
    private string _dutPattern = "";
    private bool _includeDevelopment;
    private MeasurementStatistics? _selectedMeasurement;

    public const string FolderSource = "Ergebnisordner";
    public const string DatabaseSource = "Datenbank";

    private readonly Func<IRunQuerySource?> _database;
    private string _source = FolderSource;

    public ReportsViewModel(Func<string?> defaultFolder, IFileDialogService? dialogs, IUiDispatcher ui, Func<IRunQuerySource?>? database = null)
    {
        _database = database ?? (() => null);
        _defaultFolder = defaultFolder;
        _dialogs = dialogs;
        _ui = ui;
        LoadCommand = new RelayCommand(() => _ = LoadAsync(), () => !_busy);
        ApplyCommand = new RelayCommand(Apply, () => _loaded is not null);
        ResetFilterCommand = new RelayCommand(ResetFilter);
        PdfCommand = new RelayCommand(() =>
        {
            if (_dialogs?.SaveFile("Auswertung als PDF", PdfFilter, $"Auswertung-{DateTime.Now:yyyyMMdd-HHmm}.pdf") is { } path) ExportPdf(path);
        }, () => _stats is not null);
        CsvCommand = new RelayCommand(() =>
        {
            if (_dialogs?.SaveFile("Auswertung als CSV", CsvFilter, $"Auswertung-{DateTime.Now:yyyyMMdd-HHmm}.csv") is { } path) ExportCsv(path);
        }, () => _stats is not null);
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand ResetFilterCommand { get; }
    public RelayCommand PdfCommand { get; }
    public RelayCommand CsvCommand { get; }

    public string Folder
    {
        get => _folder.Length > 0 ? _folder : _defaultFolder() ?? "";
        set => SetProperty(ref _folder, value?.Trim() ?? "");
    }

    public IReadOnlyList<string> Sources { get; } = [FolderSource, DatabaseSource];

    /// <summary>Quelle der Auswertung: Ergebnisdateien eines Ordners oder die konfigurierte Datenbank (Zeitraum, Test und Prüfplatz filtert der Server).</summary>
    public string Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value == DatabaseSource ? DatabaseSource : FolderSource)) OnPropertyChanged(nameof(IsFolderSource));
        }
    }

    public bool IsFolderSource => _source == FolderSource;

    public string DatabaseHint => _database() is { } db ? $"Datenbank: {db.Name}" : "Keine lesbare Datenbank konfiguriert (Einstellungen → Datenbank).";

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) LoadCommand.RaiseCanExecuteChanged();
        }
    }

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ObservableCollection<string> Problems { get; } = [];

    // ------------------------------------------------------------------ Filter

    public DateTime? From { get => _from; set => SetProperty(ref _from, value); }
    public DateTime? To { get => _to; set => SetProperty(ref _to, value); }
    public string Test { get => _test; set => SetProperty(ref _test, value ?? ""); }
    public string Version { get => _version; set => SetProperty(ref _version, value ?? ""); }
    public string Station { get => _station; set => SetProperty(ref _station, value ?? ""); }
    public string DutPattern { get => _dutPattern; set => SetProperty(ref _dutPattern, value ?? ""); }
    public bool IncludeDevelopment { get => _includeDevelopment; set => SetProperty(ref _includeDevelopment, value); }

    public ObservableCollection<string> Tests { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public ObservableCollection<string> Stations { get; } = [];

    public StatisticsFilter Filter => new()
    {
        From = _from is { } dateTime ? new DateTimeOffset(dateTime.Date) : null,
        To = _to is { } dateTime2 ? new DateTimeOffset(dateTime2.Date.AddDays(1).AddTicks(-1)) : null,
        Test = _test,
        Version = _version,
        Station = _station,
        DutPattern = _dutPattern,
        IncludeDevelopment = _includeDevelopment,
    };

    private void ResetFilter()
    {
        From = To = null;
        Test = Version = Station = DutPattern = "";
        IncludeDevelopment = false;
        Apply();
    }

    // ------------------------------------------------------------------ Laden und Auswerten

    public async Task LoadAsync()
    {
        if (_busy) return;
        if (_source == DatabaseSource)
        {
            await LoadFromDatabaseAsync().ConfigureAwait(true);
            return;
        }
        var folder = Folder;
        IsBusy = true;
        Status = $"Lade Ergebnisdateien aus {folder} …";
        try
        {
            // Close the progress scope before publishing the final result. Queued UI
            // callbacks from this load must also stay silent during subsequent loads.
            using (var progress = new FolderLoadProgress(_ui, entry => Status = $"Lade Ergebnisdateien … {entry.Done}/{entry.Total}"))
                _loaded = await Task.Run(() => ResultAnalysis.Load(folder, progress)).ConfigureAwait(true);
            Problems.Clear();
            foreach (var take in _loaded.Problems.Take(50)) Problems.Add(take);
            Fill(Tests, _loaded.Runs.Select(run => run.TestName));
            Fill(Versions, _loaded.Runs.Select(run => run.TestVersion));
            Fill(Stations, _loaded.Runs.Select(run => run.Station));
            ApplyCommand.RaiseCanExecuteChanged();
            Apply();
            Status = $"{_loaded.Files} Datei(en) gelesen{(_loaded.Problems.Count > 0 ? $", {_loaded.Problems.Count} mit Problemen" : "")}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Dispatches progress only while its own folder load is active.</summary>
    private sealed class FolderLoadProgress(IUiDispatcher ui, Action<(int Done, int Total)> update)
        : IProgress<(int Done, int Total)>, IDisposable
    {
        private int _completed;

        public void Report((int Done, int Total) value) => ui.Post(() =>
        {
            if (Volatile.Read(ref _completed) == 0) update(value);
        });

        public void Dispose() => Interlocked.Exchange(ref _completed, 1);
    }

    private async Task LoadFromDatabaseAsync()
    {
        OnPropertyChanged(nameof(DatabaseHint));
        if (_database() is not { } db)
        {
            Status = "Keine lesbare Datenbank konfiguriert (Einstellungen → Datenbank).";
            return;
        }
        IsBusy = true;
        var filter = Filter;
        Status = $"Lade Läufe aus {db.Name} …";
        try
        {
            var query = new RunQuery(filter.From, filter.To, NullIfEmpty(filter.Test), NullIfEmpty(filter.Station), null, filter.IncludeDevelopment);
            var runs = await Task.Run(() => db.QueryRunsAsync(query, CancellationToken.None)).ConfigureAwait(true);
            Problems.Clear();
            if (runs.Count >= query.Limit) Problems.Add($"Mehr als {query.Limit} Läufe – Zeitraum einschränken.");
            Use(runs);
            Status = $"{runs.Count} Lauf/Läufe aus der Datenbank gelesen ({filter.Describe()}).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException or TimeoutException)
        {
            Status = "Datenbank nicht lesbar: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        target.Add("");
        foreach (var value in values.Where(value2 => !string.IsNullOrWhiteSpace(value2)).Distinct().OrderBy(value2 => value2, StringComparer.OrdinalIgnoreCase)) target.Add(value);
    }

    /// <summary>Auswertung mit vorgegebenen Läufen (Tests, Datenbankquelle später).</summary>
    public void Use(IReadOnlyList<ResultRun> runs)
    {
        _loaded = new LoadedResults(runs, [], runs.Count);
        Fill(Tests, runs.Select(run => run.TestName));
        Fill(Versions, runs.Select(run => run.TestVersion));
        Fill(Stations, runs.Select(run => run.Station));
        ApplyCommand.RaiseCanExecuteChanged();
        Apply();
    }

    public void Apply()
    {
        if (_loaded is null) return;
        _stats = ResultAnalysis.Compute(_loaded.Runs, Filter);
        Measurements.Clear();
        foreach (var measurementStatistics in _stats.Measurements) Measurements.Add(measurementStatistics);
        Failures.Clear();
        foreach (var failureItem in _stats.Failures) Failures.Add(failureItem);
        Daily.Clear();
        var max = _stats.Daily.Count == 0 ? 1 : _stats.Daily.Max(dailyCount => dailyCount.Total);
        foreach (var dailyCount in _stats.Daily.TakeLast(31))
            Daily.Add(new BarItem(dailyCount.Day.ToString("dd.MM", De), (double)dailyCount.Total / max,
                $"{dailyCount.Day:dd.MM.yyyy}: {dailyCount.Total} Läufe, PASS {dailyCount.Pass}, FAIL {dailyCount.Fail}, ERROR {dailyCount.Error}, ABORT {dailyCount.Abort}", dailyCount.Fail + dailyCount.Error > 0));
        SelectedMeasurement = Measurements.FirstOrDefault(measurementStatistics2 => measurementStatistics2.Cpk is not null) ?? Measurements.FirstOrDefault();
        OnPropertiesChanged(nameof(Statistics), nameof(HasStatistics), nameof(RunsText), nameof(YieldText), nameof(FpyText), nameof(FinalYieldText),
            nameof(DutsText), nameof(DurationText), nameof(FilterText), nameof(ProblemCount));
        PdfCommand.RaiseCanExecuteChanged();
        CsvCommand.RaiseCanExecuteChanged();
    }

    public ResultStatistics? Statistics => _stats;
    public bool HasStatistics => _stats is { Runs: > 0 };
    public int ProblemCount => _loaded?.Problems.Count ?? 0;

    private static string Percent(double? ratio) => ratio is { } number ? number.ToString("0.0 %", De) : "–";

    public string RunsText => _stats is null ? "–" : $"{_stats.Runs} · PASS {_stats.Pass} · FAIL {_stats.Fail} · ERROR {_stats.Error} · ABORT {_stats.OperatorAborts}";
    public string YieldText => Percent(_stats?.Yield);
    public string FpyText => Percent(_stats?.FirstPassYield);
    public string FinalYieldText => Percent(_stats?.FinalYield);
    public string DutsText => _stats is null ? "–" : $"{_stats.Duts} Prüflinge · {_stats.Retests} Wiederholungen · {_stats.OperatorAborts} Werkerabbrüche";
    public string DurationText => _stats?.MeanDuration is { } duration ? $"{duration.TotalSeconds.ToString("0.0", De)} s" : "–";
    public string FilterText => _stats?.Filter.Describe() ?? "";

    public ObservableCollection<MeasurementStatistics> Measurements { get; } = [];
    public ObservableCollection<FailureItem> Failures { get; } = [];
    public ObservableCollection<BarItem> Daily { get; } = [];
    public ObservableCollection<BarItem> Histogram { get; } = [];

    public MeasurementStatistics? SelectedMeasurement
    {
        get => _selectedMeasurement;
        set
        {
            if (!SetProperty(ref _selectedMeasurement, value)) return;
            Histogram.Clear();
            if (value is not null)
            {
                var counts = value.Histogram();
                var max = Math.Max(1, counts.Max());
                var width = (value.Max - value.Min) / counts.Count;
                for (var index = 0; index < counts.Count; index++)
                {
                    var lo = value.Min + index * width;
                    var outside = (value.Low is { } lowerValue && lo + width <= lowerValue) || (value.High is { } upperValue && lo >= upperValue);
                    Histogram.Add(new BarItem(lo.ToString("0.##", De), (double)counts[index] / max,
                        $"{lo.ToString("0.###", De)} … {(lo + width).ToString("0.###", De)}: {counts[index]}", outside));
                }
            }
            OnPropertyChanged(nameof(MeasurementText));
        }
    }

    public string MeasurementText => _selectedMeasurement is not { } measurementStatistics ? ""
        : $"{measurementStatistics.Name}: n = {measurementStatistics.Count}, μ = {measurementStatistics.Mean.ToString("0.####", De)} {measurementStatistics.Unit}, σ = {measurementStatistics.StdDev.ToString("0.####", De)}, " +
          $"Grenzen {(measurementStatistics.Low?.ToString("0.###", De) ?? "–")} … {(measurementStatistics.High?.ToString("0.###", De) ?? "–")}, Cpk {(measurementStatistics.Cpk?.ToString("0.00", De) ?? "–")} ({measurementStatistics.Capability})";

    // ------------------------------------------------------------------ Export

    public void ExportPdf(string path)
    {
        if (_stats is null) return;
        try
        {
            Reports.StatisticsReport(_stats).Save(path);
            Status = "PDF gespeichert: " + path;
            _dialogs?.OpenExternal(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "PDF nicht gespeichert: " + ex.Message;
        }
    }

    public void ExportCsv(string path)
    {
        if (_stats is null) return;
        try
        {
            ResultAnalysis.SaveCsv(path, ResultAnalysis.StatisticsCsv(_stats));
            Status = "CSV gespeichert: " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "CSV nicht gespeichert: " + ex.Message;
        }
    }

    internal IUiDispatcher Ui => _ui;
}
