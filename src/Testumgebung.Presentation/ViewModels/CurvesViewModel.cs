/**
 * @file
 * @brief Verwaltet Kurvenimport, Generator und bearbeitbare Arbeitskopien neben gespeicherten Versionen.
 */

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
public sealed partial class CurvesViewModel : ObservableObject
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
        PointEditor = new CurvePointEditor(Plot, movePoint => Edit(movePoint, zoomToFit: false));
        Plot.PointEditor = PointEditor;

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
        TrimCommand = new RelayCommand(() => Edit(curve => CurveOps.Trim(curve, Ms(_trimFromMs, "Zuschnitt von") / 1000, Ms(_trimToMs, "Zuschnitt bis") / 1000)), HasWorking);
        TrimToViewCommand = new RelayCommand(() => Edit(curve => CurveOps.Trim(curve, Plot.ViewStart, Plot.ViewEnd)), HasWorking);
        ScaleCommand = new RelayCommand(() => Edit(curve => CurveOps.Scale(curve, Num(_scaleFactor, "Faktor"), _scaleChannel == AllChannels ? null : _scaleChannel)), HasWorking);
        ShiftCommand = new RelayCommand(() => Edit(curve => CurveOps.Shift(curve, Num(_shiftMs, "Verschiebung") / 1000)), HasWorking);
        ResampleCommand = new RelayCommand(() => Edit(curve => CurveOps.Resample(curve, Num(_resampleHz, "Abtastrate"))), HasWorking);
        UndoCommand = new RelayCommand(Undo, () => _undo.Count > 0);
        SaveVersionCommand = new RelayCommand(SaveVersion, HasWorking);
        NewFromSelectedCommand = new RelayCommand(() => { if (_selected is not null) OpenForEditing(_selected.Curve); }, () => _selected is not null);
        LoadCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Load), () => CanOutput);
        TransferCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Transfer), () => CanOutput);
        StartCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Start), () => CanOutput);
        StopCommand = new RelayCommand(() => _ = OutputAsync(CurveCommand.Stop), () => _testSim is not null && !_outputBusy);

        Plot.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(WaveformPlotViewModel.ViewText)) OnPropertyChanged(nameof(TrimToViewText));
        };
        _editor.ProjectReplaced += OnProjectReplaced;
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

    public CurvePointEditor PointEditor { get; }
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
        var text2 = text.Trim().Replace(',', '.');
        if (double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
        throw new FormatException($"{what}: „{text}“ ist keine Zahl.");
    }

    private static double Ms(string text, string what) => Num(text, what);
}
