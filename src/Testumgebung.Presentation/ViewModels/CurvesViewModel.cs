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

/// <summary>Arbeitsbereiche der Kurvenseite: drei Wege zur Kurve plus Bibliothek.</summary>
public enum CurveTab
{
    /// <summary>Gespeicherte Kurvenversionen des Projekts.</summary>
    Library,
    /// <summary>Editor: Sinus, Rampe, Sprung, Formel.</summary>
    Generator,
    /// <summary>Dateiimport: CSV/Text, COMTRADE, Excel.</summary>
    File,
    /// <summary>Aufgenommene Messkurve aus einer Ergebnisdatei.</summary>
    Measurement,
}

public sealed record Choice<T>(T Value, string Text);

/// <summary>Eintrag der Kurvenbibliothek.</summary>
public sealed record CurveListItem(CurveDefinition Curve, string Usage)
{
    public string Title => $"{Curve.Name}  v{Curve.Version}";
    public string Info => string.Create(CultureInfo.InvariantCulture,
        $"{Curve.Channels.Count} Kanal/Kanäle · {Curve.Count} Samples · {(Curve.HasExplicitTimes ? "explizite Zeit" : $"{Curve.SampleRate:0.##} Hz")} · {CurvesViewModel.OriginText(Curve.Origin)}");
}

/// <summary>Spaltenzuordnung im Import: Quelle → Zielkanal mit Einheit.</summary>
public sealed class MappingRow : ObservableObject
{
    private bool _include;
    private string _target;
    private string _unit;

    public MappingRow(ImportColumn column, bool include, string target, string unit)
    {
        Column = column;
        _include = include;
        _target = target;
        _unit = unit;
    }

    public ImportColumn Column { get; }
    public string Source => Column.Name;
    public string Range => Column.IsNumeric ? string.Create(CultureInfo.InvariantCulture, $"{Column.Min:0.###} … {Column.Max:0.###}") : "nicht numerisch";

    public bool Include { get => _include; set => SetProperty(ref _include, value); }
    public string Target { get => _target; set => SetProperty(ref _target, value); }
    public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
}

/// <summary>
/// Kurven (Abschnitt 8): drei Wege – Dateiimport mit Spalten-, Einheiten- und Zeitbasiszuordnung, Messkurve als neue Ausgabekurve,
/// Editor mit Sinus/Rampe/Sprung/Formel. Bearbeitung (Zuschnitt, Skalierung, Zeitverschiebung, Abtastrate) nur ausdrücklich
/// und mit Historie; gespeicherte Versionen sind unveränderlich und Teil des Release-Pakets. Vor der Übertragung
/// wird die Geräteeignung geprüft; Testausgabe Laden → Übertragen → Starten → Stoppen gegen die Simulation.
/// </summary>
public sealed class CurvesViewModel : ObservableObject
{
    private const string NoColumn = "(keine)";
    private const string AllChannels = "(alle Kanäle)";

    private readonly EditorViewModel _editor;
    private readonly IFileDialogService? _dialogs;
    private readonly Func<string> _user;
    private readonly Func<ResultFileStore?> _resultFiles;
    private readonly IUiDispatcher _ui;
    private readonly Stack<CurveDefinition> _undo = new();
    private CurveTab _tab = CurveTab.Library;
    private CurveListItem? _selected;
    private CurveDefinition? _working;
    private string? _error;
    private string? _status;

    // Eigenschaften der neuen Version
    private string _curveId = "Neue_Kurve";
    private string _curveName = "Neue Kurve";
    private string _curveVersion = "0.1";
    private string _description = "";

    // Generator
    private GeneratorKind _genKind = GeneratorKind.Sine;
    private string _genChannels = "L1;L2;L3";
    private string _genUnit = "V";
    private string _genRate = "20000";
    private string _genDurationMs = "40";
    private string _genAmplitude = "325";
    private string _genFrequency = "50";
    private string _genOffset = "0";
    private string _genPhases = "0;120;240";
    private string _genRampStart = "0";
    private string _genRampEnd = "230";
    private string _genSteps = "0:0.01;230:0.03;0:0.01";
    private string _genFormulas = "325*sin(2*pi*50*t)";

    // Import (Datei und Messkurve)
    private ImportTable? _table;
    private string? _importPath;
    private string? _selectedSheet;
    private TimeBaseMode _timeMode = TimeBaseMode.FixedRate;
    private string _timeColumn = NoColumn;
    private string _timeFactor = "1";
    private string _importRate = "";
    private string _qualityColumn = NoColumn;
    private DataView? _preview;
    private ResultFileInfo? _selectedResultFile;
    private ResultFileContent? _resultContent;
    private ResultFileSource.WaveformSource? _selectedSource;

    // Bearbeitung
    private string _trimFromMs = "0";
    private string _trimToMs = "0";
    private string _scaleFactor = "1";
    private string _scaleChannel = AllChannels;
    private string _shiftMs = "0";
    private string _resampleHz = "";

    // Ausgabe
    private string? _selectedOutput;
    private SimulationAdapter? _testSim;
    private SimulatedOutput? _testOutput;
    private string _outputStateText = "Keine Testausgabe";
    private bool _outputBusy;

    public CurvesViewModel(EditorViewModel editor, IFileDialogService? dialogs, Func<string> user, Func<ResultFileStore?> resultFiles, IUiDispatcher ui)
    {
        _editor = editor;
        _dialogs = dialogs;
        _user = user;
        _resultFiles = resultFiles;
        _ui = ui;

        GeneratorKinds =
        [
            new(GeneratorKind.Sine, "Sinus"), new(GeneratorKind.Ramp, "Rampe"), new(GeneratorKind.Step, "Sprung / Stufen"), new(GeneratorKind.Formula, "Formel f(t)"),
        ];
        TimeModes = [new(TimeBaseMode.FixedRate, "Feste Abtastrate"), new(TimeBaseMode.ExplicitTimes, "Explizite Zeitpunkte (Zeitspalte)")];

        GenerateCommand = new RelayCommand(Generate);
        OpenFileCommand = new RelayCommand(OpenFile);
        RefreshResultFilesCommand = new RelayCommand(RefreshResultFiles);
        OpenResultFileCommand = new RelayCommand(OpenResultFileDialog);
        ApplyImportCommand = new RelayCommand(ApplyImport, () => _table is not null);
        TrimCommand = new RelayCommand(() => Edit(c => CurveOps.Trim(c, Ms(_trimFromMs, "Zuschnitt von") / 1000, Ms(_trimToMs, "Zuschnitt bis") / 1000)), HasWorking);
        TrimToViewCommand = new RelayCommand(() => Edit(c => CurveOps.Trim(c, Plot.ViewStart, Plot.ViewEnd)), HasWorking);
        ScaleCommand = new RelayCommand(() => Edit(c => CurveOps.Scale(c, Num(_scaleFactor, "Faktor"), _scaleChannel == AllChannels ? null : _scaleChannel)), HasWorking);
        ShiftCommand = new RelayCommand(() => Edit(c => CurveOps.Shift(c, Num(_shiftMs, "Verschiebung") / 1000)), HasWorking);
        ResampleCommand = new RelayCommand(() => Edit(c => CurveOps.Resample(c, Num(_resampleHz, "Abtastrate"))), HasWorking);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        SaveVersionCommand = new RelayCommand(SaveVersion, HasWorking);
        NewFromSelectedCommand = new RelayCommand(() => { if (_selected is not null) OpenForEditing(_selected.Curve); }, () => _selected is not null);
        LoadCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Load), () => CanOutput);
        TransferCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Transfer), () => CanOutput);
        StartCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Start), () => CanOutput);
        StopCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Stop), () => _testSim is not null && !_outputBusy);

        Plot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaveformPlotViewModel.ViewText)) OnPropertyChanged(nameof(TrimToViewText));
        };
        _editor.ProjectReplaced += Reload;
        Reload();
    }

    private bool HasWorking() => _working is not null;

    // ------------------------------------------------------------------ Allgemein

    public static string OriginText(CurveOrigin origin) => origin switch
    {
        CurveOrigin.Generator => "Editor",
        CurveOrigin.FileImport => "Dateiimport",
        CurveOrigin.Measurement => "Messkurve",
        _ => origin.ToString(),
    };

    private CurveLibrary Library => _editor.Project.Definition.Curves;

    public CurveTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsImportTab));
            if (value == CurveTab.Measurement && ResultFiles.Count == 0) RefreshResultFiles();
        }
    }

    /// <summary>Datei- oder Messkurvenimport: Vorschau und Zuordnung anzeigen.</summary>
    public bool IsImportTab => _tab is CurveTab.File or CurveTab.Measurement;

    public WaveformPlotViewModel Plot { get; } = new();

    public string? Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string? Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ------------------------------------------------------------------ Bibliothek

    public ObservableCollection<CurveListItem> Curves { get; } = [];

    public CurveListItem? SelectedCurve
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            NewFromSelectedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedDetails));
            if (value is not null) Show(value.Curve, editable: false);
        }
    }

    /// <summary>Herkunft, Historie und Hash der gewählten Version.</summary>
    public IReadOnlyList<string> SelectedDetails => _selected is null ? [] : Details(_selected.Curve, _selected.Usage);

    private static List<string> Details(CurveDefinition c, string? usage)
    {
        var list = new List<string>
        {
            $"Kennung: {c.Id}  ·  Version {c.Version}",
            $"Herkunft: {OriginText(c.Origin)} – {c.SourceText}",
            $"Erstellt: {c.CreatedBy}, {c.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}",
            string.Create(CultureInfo.InvariantCulture, $"Zeitbasis: {(c.HasExplicitTimes ? $"explizite Zeitpunkte ({c.Count})" : $"{c.SampleRate:0.##} Hz")} · Dauer {WaveformPlotViewModel.FormatTime(c.Duration)}"),
            "Kanäle: " + string.Join(", ", c.Channels.Select(ch => string.Create(CultureInfo.InvariantCulture, $"{ch.Id} [{ch.Unit}] {ch.Min:0.##}…{ch.Max:0.##}"))),
            "SHA-256: " + c.ContentHash()[..16] + "…",
        };
        if (c.Quality is { Length: > 0 } q) list.Add("Qualität: " + q);
        if (!string.IsNullOrEmpty(c.Description)) list.Add("Beschreibung: " + c.Description);
        if (usage is { Length: > 0 }) list.Add("Verwendet in: " + usage);
        list.AddRange(c.History.Select(h => "• " + h));
        return list;
    }

    public RelayCommand NewFromSelectedCommand { get; }

    private void Reload()
    {
        var keep = _selected?.Curve.Key;
        Curves.Clear();
        var usage = Usage();
        foreach (var c in Library.Versions.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Version, VersionComparer.Instance))
            Curves.Add(new CurveListItem(c, string.Join(", ", usage.Where(u => u.Key == c.Key).Select(u => u.Step))));
        _selected = Curves.FirstOrDefault(c => c.Curve.Key == keep);
        OnPropertiesChanged(nameof(SelectedCurve), nameof(SelectedDetails), nameof(HasCurves));
        RefreshOutputs();
    }

    public bool HasCurves => Curves.Count > 0;

    /// <summary>Schritte „Kurve laden“ und ihre Kurvenversion (leere Version = neueste).</summary>
    private List<(string Key, string Step)> Usage()
    {
        var list = new List<(string, string)>();
        foreach (var s in _editor.Project.Definition.AllNodes().OfType<StepDefinition>().Where(s => s.BlockType == BlockCatalog.CurveLoad))
        {
            var id = s.Parameters.GetValueOrDefault("Kurve") ?? "";
            var version = s.Parameters.GetValueOrDefault("Version");
            if (Library.Find(id, version) is { } c) list.Add((c.Key, s.Name));
        }
        return list;
    }

    // ------------------------------------------------------------------ Arbeitskopie

    /// <summary>Kurve in Bearbeitung (noch nicht gespeichert, bis „Version speichern“).</summary>
    public CurveDefinition? Working => _working;

    public bool IsEditing => _working is not null;

    public IReadOnlyList<string> WorkingDetails => _working is null ? [] : Details(_working, null);

    public string WorkingTitle => _working is null ? "Keine Kurve in Bearbeitung"
        : $"In Bearbeitung: {_working.Name} (Basis v{_working.Version}, {OriginText(_working.Origin)}) – noch nicht gespeichert";

    public ObservableCollection<string> ChannelChoices { get; } = [];

    private void Show(CurveDefinition curve, bool editable)
    {
        if (editable)
        {
            _working = curve;
        }
        else
        {
            _working = null;
            _undo.Clear();
        }
        Plot.SetData(curve, keepView: editable);
        UpdateWorking();
    }

    private void OpenForEditing(CurveDefinition curve)
    {
        _undo.Clear();
        CurveId = curve.Id;
        CurveName = curve.Name;
        CurveVersion = Library.NextVersion(curve.Id);
        Description = curve.Description ?? "";
        Show(curve, editable: true);
        Status = $"Version {curve.Version} als Ausgangspunkt geöffnet. Gespeicherte Versionen bleiben unverändert.";
    }

    private void SetWorking(CurveDefinition curve, bool newCurve)
    {
        if (newCurve) _undo.Clear();
        _working = curve;
        Plot.SetData(curve, keepView: !newCurve);
        UpdateWorking();
    }

    private void UpdateWorking()
    {
        var current = _working ?? _selected?.Curve;
        ChannelChoices.Clear();
        ChannelChoices.Add(AllChannels);
        if (current is not null)
        {
            foreach (var c in current.Channels) ChannelChoices.Add(c.Id);
            _trimToMs = (current.Duration * 1000).ToString("0.###", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(_resampleHz)) _resampleHz = current.SampleRate.ToString("0.##", CultureInfo.InvariantCulture);
        }
        if (!ChannelChoices.Contains(_scaleChannel)) _scaleChannel = AllChannels;
        foreach (var c in new[] { TrimCommand, TrimToViewCommand, ScaleCommand, ShiftCommand, ResampleCommand, SaveVersionCommand })
            c.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        OnPropertiesChanged(nameof(Working), nameof(IsEditing), nameof(WorkingDetails), nameof(WorkingTitle), nameof(TrimToMs),
            nameof(ScaleChannel), nameof(ResampleHz));
        CheckSuitability();
    }

    // ------------------------------------------------------------------ Weg 3: Editor

    public IReadOnlyList<Choice<GeneratorKind>> GeneratorKinds { get; }

    public GeneratorKind GenKind
    {
        get => _genKind;
        set { if (SetProperty(ref _genKind, value)) OnPropertiesChanged(nameof(IsSine), nameof(IsRamp), nameof(IsStep), nameof(IsFormula)); }
    }

    public bool IsSine => _genKind == GeneratorKind.Sine;
    public bool IsRamp => _genKind == GeneratorKind.Ramp;
    public bool IsStep => _genKind == GeneratorKind.Step;
    public bool IsFormula => _genKind == GeneratorKind.Formula;

    public string GenChannels { get => _genChannels; set => SetProperty(ref _genChannels, value); }
    public string GenUnit { get => _genUnit; set => SetProperty(ref _genUnit, value); }
    public string GenRate { get => _genRate; set => SetProperty(ref _genRate, value); }
    public string GenDurationMs { get => _genDurationMs; set => SetProperty(ref _genDurationMs, value); }
    public string GenAmplitude { get => _genAmplitude; set => SetProperty(ref _genAmplitude, value); }
    public string GenFrequency { get => _genFrequency; set => SetProperty(ref _genFrequency, value); }
    public string GenOffset { get => _genOffset; set => SetProperty(ref _genOffset, value); }
    public string GenPhases { get => _genPhases; set => SetProperty(ref _genPhases, value); }
    public string GenRampStart { get => _genRampStart; set => SetProperty(ref _genRampStart, value); }
    public string GenRampEnd { get => _genRampEnd; set => SetProperty(ref _genRampEnd, value); }
    public string GenSteps { get => _genSteps; set => SetProperty(ref _genSteps, value); }

    /// <summary>Eine Formel je Zeile (je Kanal; eine Zeile gilt für alle).</summary>
    public string GenFormulas { get => _genFormulas; set => SetProperty(ref _genFormulas, value); }

    public RelayCommand GenerateCommand { get; }

    /// <summary>Generatoreinstellungen aus den Eingaben (Zahlen mit Punkt oder Komma).</summary>
    public GeneratorSettings BuildSettings() => new()
    {
        Kind = _genKind,
        ChannelIds = List(_genChannels),
        Unit = _genUnit.Trim(),
        SampleRate = Num(_genRate, "Abtastrate"),
        Duration = Num(_genDurationMs, "Dauer") / 1000,
        Amplitude = Num(_genAmplitude, "Amplitude"),
        Frequency = Num(_genFrequency, "Frequenz"),
        Offset = Num(_genOffset, "Offset"),
        Phases = List(_genPhases).Select(p => Num(p, "Phase")).ToList(),
        RampStart = Num(_genRampStart, "Rampe Start"),
        RampEnd = Num(_genRampEnd, "Rampe Ende"),
        Steps = _genSteps,
        Formulas = _genFormulas.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
    };

    private void Generate() => Try(() =>
    {
        var curve = CurveGenerator.Generate(BuildSettings(), Id(), CurveName.Trim(), CurveVersion.Trim(), _user(), DateTimeOffset.Now);
        SetWorking(curve, newCurve: true);
        Status = "Kurve erzeugt: " + curve.SourceText;
    });

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
            foreach (var s in Xlsx.SheetNames(File.ReadAllBytes(path))) Sheets.Add(s);
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
            foreach (var f in store.List()) ResultFiles.Add(f);
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
        foreach (var s in ResultFileSource.List(_resultContent)) ResultSources.Add(s);
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
        $"{_table.Format} · {_table.SourceName} · {_table.Rows} Zeilen · {_table.Columns.Count} Spalten{(_table.SampleRate is { } r ? $" · Abtastrate laut Datei {r:0.##} Hz" : "")} · SHA-256 {_table.Sha256[..12]}…");

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

    private void SetTable(ImportTable table)
    {
        _table = table;
        var suggested = CurveImport.SuggestSettings(table);
        Mapping.Clear();
        foreach (var col in table.Columns)
        {
            var m = suggested.Channels.FirstOrDefault(c => c.SourceColumn == col.Name);
            var isMeta = col.Name == table.TimeColumn || col.Name == table.QualityColumn;
            if (isMeta) continue;
            Mapping.Add(new MappingRow(col, m is not null, m?.TargetChannel ?? col.Name, m?.Unit ?? col.Unit ?? ""));
        }
        ColumnChoices.Clear();
        ColumnChoices.Add(NoColumn);
        foreach (var c in table.Columns) ColumnChoices.Add(c.Name);
        _timeMode = suggested.Mode;
        _timeColumn = suggested.TimeColumn ?? NoColumn;
        _timeFactor = suggested.TimeFactor.ToString("0.#########", CultureInfo.InvariantCulture);
        _importRate = suggested.SampleRate?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        _qualityColumn = suggested.QualityColumn ?? NoColumn;
        ImportWarnings.Clear();
        foreach (var w in table.Warnings) ImportWarnings.Add(w);
        _preview = BuildPreview(table);
        ImportSummary.Clear();
        try
        {
            foreach (var s in CurveImport.Summarize(table, CurrentImportSettings())) ImportSummary.Add(s);
        }
        catch (CurveImportException) { /* Zuordnung noch unvollständig */ }
        ApplyImportCommand.RaiseCanExecuteChanged();
        OnPropertiesChanged(nameof(Table), nameof(HasTable), nameof(Preview), nameof(TableInfo), nameof(TimeMode), nameof(IsExplicitTime),
            nameof(TimeColumn), nameof(TimeFactor), nameof(ImportRate), nameof(QualityColumn));
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
        foreach (var c in table.Columns)
        {
            var name = c.Unit is { Length: > 0 } u ? $"{c.Name} [{u}]" : c.Name;
            while (dt.Columns.Contains(name)) name += "'";
            dt.Columns.Add(name, typeof(string));
        }
        foreach (var row in table.PreviewRows.Take(200))
        {
            var r = dt.NewRow();
            for (var i = 0; i < Math.Min(row.Length, dt.Columns.Count); i++) r[i] = row[i];
            dt.Rows.Add(r);
        }
        return dt.DefaultView;
    }

    /// <summary>Importeinstellungen aus der aktuellen Zuordnung.</summary>
    public ImportSettings CurrentImportSettings() => new(
        _timeMode,
        _timeColumn == NoColumn ? null : _timeColumn,
        _timeMode == TimeBaseMode.ExplicitTimes ? Num(_timeFactor, "Zeitfaktor") : 1,
        _timeMode == TimeBaseMode.FixedRate && _importRate.Trim().Length > 0 ? Num(_importRate, "Abtastrate") : null,
        Mapping.Where(m => m.Include).Select(m => new ChannelMapping(m.Source, m.Target.Trim(), m.Unit.Trim())).ToList(),
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
        foreach (var s in CurveImport.Summarize(_table, settings)) ImportSummary.Add(s);
        SetWorking(curve, newCurve: true);
        Status = $"Übernommen: {curve.SourceText}" + (curve.Origin == CurveOrigin.Measurement ? " – die Ergebnisdatei bleibt unverändert." : "");
    });

    // ------------------------------------------------------------------ Bearbeitung

    public string TrimFromMs { get => _trimFromMs; set => SetProperty(ref _trimFromMs, value); }
    public string TrimToMs { get => _trimToMs; set => SetProperty(ref _trimToMs, value); }
    public string TrimToViewText => "Auf Ansicht zuschneiden (" + Plot.ViewText + ")";
    public string ScaleFactor { get => _scaleFactor; set => SetProperty(ref _scaleFactor, value); }
    public string ScaleChannel { get => _scaleChannel; set => SetProperty(ref _scaleChannel, value); }
    public string ShiftMs { get => _shiftMs; set => SetProperty(ref _shiftMs, value); }
    public string ResampleHz { get => _resampleHz; set => SetProperty(ref _resampleHz, value); }

    public RelayCommand TrimCommand { get; }
    public RelayCommand TrimToViewCommand { get; }
    public RelayCommand ScaleCommand { get; }
    public RelayCommand ShiftCommand { get; }
    public RelayCommand ResampleCommand { get; }
    public RelayCommand UndoCommand { get; }

    private void Edit(Func<CurveDefinition, CurveDefinition> op) => Try(() =>
    {
        if (_working is null) return;
        var next = op(_working);
        _undo.Push(_working);
        SetWorking(next, newCurve: false);
        Plot.SetView(0, next.Duration);
        Status = "Bearbeitet: " + next.History[^1];
    });

    private void Undo()
    {
        if (_undo.Count == 0) return;
        SetWorking(_undo.Pop(), newCurve: false);
        Status = "Letzter Bearbeitungsschritt rückgängig gemacht.";
    }

    // ------------------------------------------------------------------ Versionierung

    public string CurveId { get => _curveId; set => SetProperty(ref _curveId, value); }
    public string CurveName { get => _curveName; set => SetProperty(ref _curveName, value); }
    public string CurveVersion { get => _curveVersion; set => SetProperty(ref _curveVersion, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    public RelayCommand SaveVersionCommand { get; }

    private string Id()
    {
        var id = _curveId.Trim();
        if (id.Length == 0 || id.Any(ch => char.IsWhiteSpace(ch) || ch == '@'))
            throw new CurveImportException("Kennung ohne Leerzeichen und ohne „@“ angeben (z. B. AC_Profil).");
        return id;
    }

    /// <summary>Arbeitskopie als neue, unveränderliche Version im Projekt ablegen (Teil des nächsten Release-Pakets).</summary>
    public void SaveVersion() => Try(() =>
    {
        if (_working is null) return;
        var id = Id();
        var version = _curveVersion.Trim();
        if (version.Length == 0) throw new CurveImportException("Version angeben.");
        if (Library.Find(id, version) is { } existing && existing.Version == version)
            throw new CurveImportException($"Version {version} von „{id}“ existiert bereits und ist unveränderlich – neue Version wählen (Vorschlag {Library.NextVersion(id)}).");
        var problems = CurveOps.Validate(_working);
        if (problems.Count > 0) throw new CurveImportException(string.Join(" ", problems));
        var saved = _working.WithVersion(version, _curveName.Trim().Length > 0 ? _curveName.Trim() : id, id, _user(), DateTimeOffset.Now,
            _description.Trim().Length > 0 ? _description.Trim() : null);
        Library.Add(saved);
        _editor.MarkChanged();
        _working = null;
        _undo.Clear();
        Reload();
        SelectedCurve = Curves.FirstOrDefault(c => c.Curve.Key == saved.Key);
        Tab = CurveTab.Library;
        CurveVersion = Library.NextVersion(id);
        Status = $"Version {saved.Key} gespeichert. Sie wird mit dem nächsten Release-Paket freigegeben; „Kurve laden“-Schritte wählen sie über Kurve und Version.";
    });

    // ------------------------------------------------------------------ Geräteeignung und Testausgabe

    public ObservableCollection<string> Outputs { get; } = [];

    public string? SelectedOutput
    {
        get => _selectedOutput;
        set
        {
            if (!SetProperty(ref _selectedOutput, value)) return;
            ResetTestOutput();
            CheckSuitability();
        }
    }

    public ObservableCollection<SuitabilityIssue> Suitability { get; } = [];
    public bool IsSuitable => Suitability.Count > 0 && Suitability.All(i => !i.IsError);
    public string CapabilitiesText => _testOutput is { } o
        ? string.Create(CultureInfo.InvariantCulture, $"{o.Name}: {o.Unit} {o.Min:0.##}…{o.Max:0.##}, Kanäle {o.Channels}, Abtastraten {o.SampleRates} Hz{(o.ExplicitTimes ? ", explizite Zeit" : "")}, max. {o.MaxSamples} Samples")
        : "";

    public ObservableCollection<string> OutputLog { get; } = [];

    public string OutputStateText
    {
        get => _outputStateText;
        private set => SetProperty(ref _outputStateText, value);
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand TransferCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }

    private CurveDefinition? Current => _working ?? _selected?.Curve;
    private bool CanOutput => Current is not null && _testOutput is not null && !_outputBusy;

    private void RefreshOutputs()
    {
        var keep = _selectedOutput;
        Outputs.Clear();
        foreach (var o in _editor.Project.SimulationOutputs) Outputs.Add(o.Name);
        _selectedOutput = Outputs.Contains(keep ?? "") ? keep : Outputs.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedOutput));
        ResetTestOutput();
        CheckSuitability();
    }

    private void ResetTestOutput()
    {
        _testSim = null;
        _testOutput = _editor.Project.SimulationOutputs.FirstOrDefault(o => o.Name == _selectedOutput);
        OutputLog.Clear();
        OutputStateText = _testOutput is null ? "Kein Ausgabegerät im Projekt" : "Leerlauf";
        OnPropertyChanged(nameof(CapabilitiesText));
        RaiseOutput();
    }

    private void RaiseOutput()
    {
        foreach (var c in new[] { LoadCommand, TransferCommand, StartCommand, StopCommand }) c.RaiseCanExecuteChanged();
    }

    private void CheckSuitability()
    {
        Suitability.Clear();
        if (Current is { } c && _testOutput is not null)
        {
            var sim = EnsureSim();
            foreach (var i in OutputSuitability.Check(sim.GetOutputCapabilities(_testOutput.Channel), c.ToOutput())) Suitability.Add(i);
        }
        OnPropertyChanged(nameof(IsSuitable));
        RaiseOutput();
    }

    private SimulationAdapter EnsureSim()
        => _testSim ??= SampleProjects.CreateSimulation(_editor.Project, seed: 7).Adapter;

    private async Task OutputAsync(CurveCommand command)
    {
        if (_testOutput is null) return;
        var sim = EnsureSim();
        var channel = _testOutput.Channel;
        _outputBusy = true;
        RaiseOutput();
        try
        {
            switch (command)
            {
                case CurveCommand.Load:
                    await sim.LoadAsync(channel, Current!.ToOutput(), CancellationToken.None).ConfigureAwait(true);
                    Log($"Geladen: {Current!.Key}");
                    break;
                case CurveCommand.Transfer:
                    await sim.TransferAsync(channel, CancellationToken.None).ConfigureAwait(true);
                    Log($"Übertragen: {sim.LoadedCurve(channel)}");
                    break;
                case CurveCommand.Start:
                    await sim.StartAsync(channel, CancellationToken.None).ConfigureAwait(true);
                    Log($"Gestartet: {sim.LoadedCurve(channel)}");
                    await FeedbackAsync(sim).ConfigureAwait(true);
                    break;
                case CurveCommand.Stop:
                    await sim.StopAsync(channel, CancellationToken.None).ConfigureAwait(true);
                    Log("Gestoppt");
                    break;
            }
            Error = null;
        }
        catch (Exception ex) when (ex is OutputSequenceException or InvalidOperationException or ArgumentException)
        {
            Log("Abgelehnt: " + ex.Message);
            Error = ex.Message;
        }
        finally
        {
            _outputBusy = false;
            OutputStateText = StateText(sim.GetOutputState(channel), sim.LoadedCurve(channel));
            RaiseOutput();
        }
    }

    /// <summary>Rückmessung über die zugeordneten Messkanäle (Simulation): Effektivwert je Phase.</summary>
    private async Task FeedbackAsync(SimulationAdapter sim)
    {
        if (_testOutput is null || Current is not { SampleRate: > 0 } curve) return;
        var pairs = _testOutput.Feedback.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2, StringSplitOptions.TrimEntries)).Where(x => x.Length == 2).ToList();
        if (pairs.Count == 0) return;
        var set = await sim.CaptureAsync(pairs.Select(p => p[1]).ToList(), curve.SampleRate, Math.Max(1, curve.Count), CancellationToken.None).ConfigureAwait(true);
        Log("Rückmessung: " + string.Join(", ", set.Channels.Select((w, i) =>
            string.Create(CultureInfo.InvariantCulture, $"{pairs[i][0]}→{w.Channel} {w.Rms():0.##} {_testOutput.Unit} eff."))));
    }

    private static string StateText(OutputState state, string? key) => state switch
    {
        OutputState.Idle => "Leerlauf",
        OutputState.Loaded => $"Geladen ({key}) – nächster Schritt: Übertragen",
        OutputState.Transferred => $"Im Gerät ({key}) – bereit zum Starten",
        OutputState.Running => $"Ausgabe läuft ({key})",
        OutputState.Stopped => $"Gestoppt ({key}) – erneut starten möglich",
        _ => state.ToString(),
    };

    private void Log(string text) => OutputLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");

    // ------------------------------------------------------------------ Hilfen

    private void Try(Action action)
    {
        try
        {
            action();
            Error = null;
        }
        catch (Exception ex) when (ex is CurveImportException or CurveGeneratorException or CurveFormulaException or FormatException
                                       or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException
                                       or ResultFileException)
        {
            Error = ex.Message;
        }
    }

    private static List<string> List(string text)
        => text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>Zahl mit Punkt oder Komma.</summary>
    public static double Num(string text, string what)
    {
        var t = text.Trim().Replace(',', '.');
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
        throw new FormatException($"{what}: „{text}“ ist keine Zahl.");
    }

    private static double Ms(string text, string what) => Num(text, what);
}
