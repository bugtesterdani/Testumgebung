using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Curves;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class CurvesViewModel
{
    // ------------------------------------------------------------------ Weg 1: Datei, Weg 2: Messkurve

    public const string FileFilter =
        "Kurvendateien (*.csv;*.txt;*.tsv;*.cfg;*.xlsx;*.tres)|*.csv;*.txt;*.tsv;*.cfg;*.xlsx;*.tres|CSV/Text (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|" +
        "COMTRADE (*.cfg)|*.cfg|Excel (*.xlsx)|*.xlsx|Ergebnisdatei (*.tres)|*.tres|Alle Dateien (*.*)|*.*";

    public RelayCommand OpenFileCommand { get; }

    public string? ImportPath { get => _importPath; private set => SetProperty(ref _importPath, value); }

    public ObservableCollection<string> Sheets { get; } = [];

    public bool HasSheets => Sheets.Count > 1;

    public string? SelectedSheet
    {
        get => _selectedSheet;
        set
        {
            if (!SetProperty(ref _selectedSheet, value) || value is null || _importPath is null) return;
            Try(() => SetTable(Xlsx.Read(_importPath, value)));
        }
    }

    private void OpenFile()
    {
        var path = _dialogs?.OpenFile("Kurve importieren", FileFilter);
        if (path is not null) ImportFile(path);
    }

    /// <summary>Datei lesen (Format nach Endung) und Zuordnung vorschlagen.</summary>
    public void ImportFile(string path) => Try(() =>
    {
        var table = CurveImport.Read(path);
        ImportPath = path;
        Sheets.Clear();
        if (Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            foreach (var text in Xlsx.SheetNames(File.ReadAllBytes(path))) Sheets.Add(text);
        _selectedSheet = Sheets.FirstOrDefault();
        OnPropertiesChanged(nameof(SelectedSheet), nameof(HasSheets));
        SetTable(table);
        if (Tab is not (CurveTab.File or CurveTab.Measurement)) Tab = CurveTab.File;
    });

    public ObservableCollection<ResultFileInfo> ResultFiles { get; } = [];
    public ObservableCollection<ResultFileSource.WaveformSource> ResultSources { get; } = [];
    public RelayCommand RefreshResultFilesCommand { get; }
    public RelayCommand OpenResultFileCommand { get; }

    private void RefreshResultFiles() => Try(() =>
    {
        ResultFiles.Clear();
        if (_resultFiles() is { } store)
            foreach (var resultFileInfo in store.List()) ResultFiles.Add(resultFileInfo);
        Status = ResultFiles.Count == 0 ? "Keine Ergebnisdateien im Ergebnisordner." : $"{ResultFiles.Count} Ergebnisdatei(en).";
    });

    private void OpenResultFileDialog()
    {
        var path = _dialogs?.OpenFile("Ergebnisdatei öffnen", "Ergebnisdatei (*.tres)|*.tres|Alle Dateien (*.*)|*.*");
        if (path is not null) LoadResultFile(path);
    }

    public ResultFileInfo? SelectedResultFile
    {
        get => _selectedResultFile;
        set
        {
            if (SetProperty(ref _selectedResultFile, value) && value is not null) LoadResultFile(value.Path);
        }
    }

    /// <summary>Ergebnisdatei öffnen (nur lesend) und ihre Kurvenaufnahmen auflisten.</summary>
    public void LoadResultFile(string path) => Try(() =>
    {
        _resultContent = ResultFile.Read(path);
        ImportPath = path;
        ResultSources.Clear();
        foreach (var waveformSource in ResultFileSource.List(_resultContent)) ResultSources.Add(waveformSource);
        if (ResultSources.Count == 0) Status = "Die Ergebnisdatei enthält keine Kurven.";
        SelectedSource = ResultSources.FirstOrDefault();
    });

    public ResultFileSource.WaveformSource? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value) || value is null || _resultContent is null || _importPath is null) return;
            Try(() => SetTable(ResultFileSource.ToTable(_resultContent, value, Path.GetFileName(_importPath))));
        }
    }

    public ImportTable? Table => _table;
    public bool HasTable => _table is not null;
    public DataView? Preview => _preview;
    public ObservableCollection<MappingRow> Mapping { get; } = [];
    public ObservableCollection<string> ColumnChoices { get; } = [];
    public ObservableCollection<string> ImportWarnings { get; } = [];
    public ObservableCollection<ChannelSummary> ImportSummary { get; } = [];
    public IReadOnlyList<Choice<TimeBaseMode>> TimeModes { get; }

    public string TableInfo => _table is null ? "" : string.Create(CultureInfo.InvariantCulture,
        $"{_table.Format} · {_table.SourceName} · {_table.Rows} Zeilen · {_table.Columns.Count} Spalten{(_table.SampleRate is { } ratio ? $" · Abtastrate laut Datei {ratio:0.##} Hz" : "")} · SHA-256 {_table.Sha256[..12]}…");

    public TimeBaseMode TimeMode
    {
        get => _timeMode;
        set { if (SetProperty(ref _timeMode, value)) OnPropertyChanged(nameof(IsExplicitTime)); }
    }

    public bool IsExplicitTime => _timeMode == TimeBaseMode.ExplicitTimes;
    public string TimeColumn { get => _timeColumn; set => SetProperty(ref _timeColumn, value); }

    /// <summary>Faktor Zeitspalte → Sekunden (z. B. 0,001 für ms).</summary>
    public string TimeFactor { get => _timeFactor; set => SetProperty(ref _timeFactor, value); }

    public string ImportRate { get => _importRate; set => SetProperty(ref _importRate, value); }
    public string QualityColumn { get => _qualityColumn; set => SetProperty(ref _qualityColumn, value); }

    public RelayCommand ApplyImportCommand { get; }

    private void BuildMappingRows(ImportTable table, ImportSettings suggested)
    {
        Mapping.Clear();
        foreach (var col in table.Columns)
        {
            var channelMapping = suggested.Channels.FirstOrDefault(channelMapping2 => channelMapping2.SourceColumn == col.Name);
            var isMeta = col.Name == table.TimeColumn || col.Name == table.QualityColumn;
            if (isMeta) continue;
            Mapping.Add(new MappingRow(col, channelMapping is not null, channelMapping?.TargetChannel ?? col.Name, channelMapping?.Unit ?? col.Unit ?? ""));
        }
    }

    private void SetTable(ImportTable table)
    {
        _table = table;
        var suggested = CurveImport.SuggestSettings(table);
        BuildMappingRows(table, suggested);
        ColumnChoices.Clear();
        ColumnChoices.Add(NoColumn);
        foreach (var importColumn in table.Columns) ColumnChoices.Add(importColumn.Name);
        _timeMode = suggested.Mode;
        _timeColumn = suggested.TimeColumn ?? NoColumn;
        _timeFactor = suggested.TimeFactor.ToString("0.#########", CultureInfo.InvariantCulture);
        _importRate = suggested.SampleRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        _qualityColumn = suggested.QualityColumn ?? NoColumn;
        ImportWarnings.Clear();
        foreach (var warning in table.Warnings) ImportWarnings.Add(warning);
        _preview = BuildPreview(table);
        ImportSummary.Clear();
        try
        {
            foreach (var channelSummary in CurveImport.Summarize(table, CurrentImportSettings())) ImportSummary.Add(channelSummary);
        }
        catch (CurveImportException) { /* Zuordnung noch unvollständig */ }
        ApplyImportCommand.RaiseCanExecuteChanged();
        OnPropertiesChanged(nameof(Table), nameof(HasTable), nameof(Preview), nameof(TableInfo), nameof(TimeMode), nameof(IsExplicitTime),
            nameof(TimeColumn), nameof(TimeFactor), nameof(ImportRate), nameof(QualityColumn));
        PointEditor.UseCurve(null, reset: true);
        // Vorschau ohne Übernahme: Rohdaten der vorgeschlagenen Zuordnung
        try
        {
            var candidate = CurveImport.ToCurve(table, CurrentImportSettings(), Id(), CurveName.Trim(), CurveVersion.Trim(), _user(), DateTimeOffset.Now);
            Plot.SetData(candidate);
            Status = "Vorschau der Datei – Zuordnung prüfen und „Als Kurve übernehmen“.";
        }
        catch (CurveImportException ex)
        {
            Plot.Clear();
            Status = "Vorschau nicht möglich: " + ex.Message;
        }
    }

    private static DataView BuildPreview(ImportTable table)
    {
        var dt = new DataTable();
        foreach (var importColumn in table.Columns)
        {
            var name = importColumn.Unit is { Length: > 0 } unit ? $"{importColumn.Name} [{unit}]" : importColumn.Name;
            while (dt.Columns.Contains(name)) name += "'";
            dt.Columns.Add(name, typeof(string));
        }
        foreach (var row in table.PreviewRows.Take(200))
        {
            var dataRow = dt.NewRow();
            for (var index = 0; index < Math.Min(row.Length, dt.Columns.Count); index++) dataRow[index] = row[index];
            dt.Rows.Add(dataRow);
        }
        return dt.DefaultView;
    }

    /// <summary>Importeinstellungen aus der aktuellen Zuordnung.</summary>
    public ImportSettings CurrentImportSettings() => new(
        _timeMode,
        _timeColumn == NoColumn ? null : _timeColumn,
        _timeMode == TimeBaseMode.ExplicitTimes ? Num(_timeFactor, "Zeitfaktor") : 1,
        _timeMode == TimeBaseMode.FixedRate && _importRate.Trim().Length > 0 ? Num(_importRate, "Abtastrate") : null,
        Mapping.Where(mappingRow => mappingRow.Include).Select(mappingRow => new ChannelMapping(mappingRow.Source, mappingRow.Target.Trim(), mappingRow.Unit.Trim())).ToList(),
        _qualityColumn == NoColumn ? null : _qualityColumn);

    private void ApplyImport() => Try(() =>
    {
        if (_table is null) return;
        var settings = CurrentImportSettings();
        var name = CurveName.Trim();
        if (_tab == CurveTab.Measurement && _curveId == "Neue_Kurve")
        {
            CurveId = "Messkurve_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
            CurveName = name = "Messkurve " + (_selectedSource?.DutId ?? "");
            CurveVersion = Library.NextVersion(CurveId);
        }
        var curve = CurveImport.ToCurve(_table, settings, Id(), name, CurveVersion.Trim(), _user(), DateTimeOffset.Now);
        ImportSummary.Clear();
        foreach (var channelSummary in CurveImport.Summarize(_table, settings)) ImportSummary.Add(channelSummary);
        SetWorking(curve, newCurve: true);
        Status = $"Übernommen: {curve.SourceText}" + (curve.Origin == CurveOrigin.Measurement ? " – die Ergebnisdatei bleibt unverändert." : "");
    });
}
