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
        PointEditor.UseCurve(_working, reset: true);
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
        PointEditor.UseCurve(curve, reset: newCurve);
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
            foreach (var curveChannel in current.Channels) ChannelChoices.Add(curveChannel.Id);
            _trimToMs = (current.Duration * 1000).ToString("0.###", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(_resampleHz)) _resampleHz = current.SampleRate.ToString("0.##", CultureInfo.InvariantCulture);
        }
        if (!ChannelChoices.Contains(_scaleChannel)) _scaleChannel = AllChannels;
        foreach (var relayCommand in new[] { TrimCommand, TrimToViewCommand, ScaleCommand, ShiftCommand, ResampleCommand, SaveVersionCommand })
            relayCommand.RaiseCanExecuteChanged();
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
        Phases = List(_genPhases).Select(list => Num(list, "Phase")).ToList(),
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

    private void Edit(Func<CurveDefinition, CurveDefinition> operation, bool zoomToFit = true) => Try(() =>
    {
        if (_working is null) return;
        var next = operation(_working);
        _undo.Push(_working);
        SetWorking(next, newCurve: false);
        if (zoomToFit) Plot.SetView(0, next.Duration);
        Status = "Bearbeitet: " + next.History[^1];
    });

    public void ApplyPointTable(CurveDefinition source, CurveDefinition edited)
    {
        if (ReferenceEquals(_working, source) && !ReferenceEquals(source, edited))
            Edit(_ => edited);
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        SetWorking(_undo.Pop(), newCurve: false);
        Status = "Letzter Bearbeitungsschritt rückgängig gemacht.";
    }
}
