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

    private static List<string> Details(CurveDefinition curve, string? usage)
    {
        var list = new List<string>
        {
            $"Kennung: {curve.Id}  ·  Version {curve.Version}",
            $"Herkunft: {OriginText(curve.Origin)} – {curve.SourceText}",
            $"Erstellt: {curve.CreatedBy}, {curve.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}",
            string.Create(CultureInfo.InvariantCulture, $"Zeitbasis: {(curve.HasExplicitTimes ? $"explizite Zeitpunkte ({curve.Count})" : $"{curve.SampleRate:0.##} Hz")} · Dauer {WaveformPlotViewModel.FormatTime(curve.Duration)}"),
            "Kanäle: " + string.Join(", ", curve.Channels.Select(ch => string.Create(CultureInfo.InvariantCulture, $"{ch.Id} [{ch.Unit}] {ch.Min:0.##}…{ch.Max:0.##}"))),
            "SHA-256: " + curve.ContentHash()[..16] + "…",
        };
        if (curve.Quality is { Length: > 0 } text) list.Add("Qualität: " + text);
        if (!string.IsNullOrEmpty(curve.Description)) list.Add("Beschreibung: " + curve.Description);
        if (usage is { Length: > 0 }) list.Add("Verwendet in: " + usage);
        list.AddRange(curve.History.Select(history => "• " + history));
        return list;
    }

    public RelayCommand NewFromSelectedCommand { get; }

    private void OnProjectReplaced()
    {
        Reload();
        if (_selected is { } selected)
        {
            Show(selected.Curve, editable: false);
            return;
        }

        _working = null;
        _undo.Clear();
        PointEditor.UseCurve(null, reset: true);
        Plot.Clear();
        UpdateWorking();
    }

    private void Reload()
    {
        var keep = _selected?.Curve.Key;
        Curves.Clear();
        var usage = Usage();
        foreach (var curve in Library.Versions.OrderBy(curve2 => curve2.Id, StringComparer.OrdinalIgnoreCase).ThenBy(curve2 => curve2.Version, VersionComparer.Instance))
            Curves.Add(new CurveListItem(curve, string.Join(", ", usage.Where(entry => entry.Key == curve.Key).Select(entry => entry.Step))));
        _selected = Curves.FirstOrDefault(curveListItem => curveListItem.Curve.Key == keep);
        OnPropertiesChanged(nameof(SelectedCurve), nameof(SelectedDetails), nameof(HasCurves));
        RefreshOutputs();
    }

    public bool HasCurves => Curves.Count > 0;

    /// <summary>Schritte „Kurve laden“ und ihre Kurvenversion (leere Version = neueste).</summary>
    private List<(string Key, string Step)> Usage()
    {
        var list = new List<(string, string)>();
        foreach (var step in _editor.Project.Definition.AllNodes().OfType<StepDefinition>().Where(step2 => step2.BlockType == BlockCatalog.CurveLoad ||
                     step2.BlockType == BlockCatalog.CurveOutput &&
                     step2.Parameters.GetValueOrDefault(CurveOutputBlocks.ActionParameter, CurveOutputBlocks.Load) == CurveOutputBlocks.Load))
        {
            var id = step.Parameters.GetValueOrDefault("Kurve") ?? "";
            var version = step.Parameters.GetValueOrDefault("Version");
            if (Library.Find(id, version) is { } curve) list.Add((curve.Key, step.Name));
        }
        AddTraceUsage(list);
        return list;
    }

    private void AddTraceUsage(List<(string Key, string Step)> list)
    {
        foreach (var step in _editor.Project.Definition.AllNodes().OfType<StepDefinition>().Where(step => step.BlockType is TraceBlocks.Start or TraceBlocks.Record))
            foreach (var key in TraceCurveKeys(step))
            {
                var separator = key.LastIndexOf('@');
                if (Library.Find(separator < 0 ? key : key[..separator], separator < 0 ? null : key[(separator + 1)..]) is { } limit)
                    list.Add((limit.Key, step.Name));
            }
    }

    private static IEnumerable<string> TraceCurveKeys(StepDefinition step)
        => step.Parameters.GetValueOrDefault(TraceBlocks.EvaluationParameter, TraceBlocks.ValueBand) != TraceBlocks.ValueBand
            ? []
            : new[] { "Grenzkurven", TraceBlocks.AbortCurvesParameter }
            .SelectMany(parameter => (step.Parameters.GetValueOrDefault(parameter) ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal);

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
        PointEditor.UseCurve(null, reset: true);
        _working = null;
        _undo.Clear();
        Reload();
        SelectedCurve = Curves.FirstOrDefault(curveListItem => curveListItem.Curve.Key == saved.Key);
        Tab = CurveTab.Library;
        CurveVersion = Library.NextVersion(id);
        Status = $"Version {saved.Key} gespeichert. Sie wird mit dem nächsten Release-Paket freigegeben; „Kurve laden“-Schritte wählen sie über Kurve und Version.";
    });
}
