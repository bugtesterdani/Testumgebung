using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>
/// Konfigurierbare Werkeransicht (Abschnitt 4): Position von „Aktueller Schritt und Fortschritt“, Ablaufübersicht, Protokoll,
/// Messwerte (alle oder nur auffällige). Das Layout kommt aus dem laufenden Ablauf, im Ruhezustand aus dem geöffneten Projekt.
/// </summary>
public sealed partial class StationViewModel
{
    private OperatorLayout _layout = new();
    private readonly List<MeasurementRow> _allMeasurements = [];
    private bool _showAllMeasurements = true;

    public OperatorLayout Layout => _layout;

    /// <summary>Layout übernehmen (Laufstart bzw. Änderung im Editor, solange der Prüfplatz ruht).</summary>
    public void ApplyLayout(OperatorLayout layout)
    {
        _layout = layout.Clone();
        _showAllMeasurements = _layout.ShowAllMeasurements;
        RebuildMeasurements();
        OnPropertiesChanged(nameof(Layout), nameof(ProgressTop), nameof(ProgressLeft), nameof(ProgressBottom), nameof(ShowOverview),
            nameof(ShowProtocol), nameof(ContentColumn), nameof(ContentSpan), nameof(MainRowSpan), nameof(ShowAllMeasurements),
            nameof(CanToggleMeasurements), nameof(HiddenMeasurementsText));
    }

    /// <summary>Layout des geöffneten Projekts übernehmen, falls der Prüfplatz nicht prüft.</summary>
    public void RefreshIdleLayout()
    {
        if (!IsRunning) ApplyLayout(_projectSource().Definition.Layout);
    }

    public bool ShowOverview => _layout.ShowOverview;
    public bool ShowProtocol => _layout.ShowProtocol;

    /// <summary>„Links“ ohne Ablaufübersicht fällt auf „oben“ zurück – der Fortschritt bleibt immer sichtbar.</summary>
    public bool ProgressLeft => _layout.ProgressPosition == ProgressPosition.Left && _layout.ShowOverview;
    public bool ProgressTop => _layout.ProgressPosition == ProgressPosition.Top || (_layout.ProgressPosition == ProgressPosition.Left && !_layout.ShowOverview);
    public bool ProgressBottom => _layout.ProgressPosition == ProgressPosition.Bottom;

    /// <summary>Spalte/Spannweite des aktiven Inhalts: ohne Ablaufübersicht nutzt er deren Platz.</summary>
    public int ContentColumn => _layout.ShowOverview ? 2 : 0;
    public int ContentSpan => _layout.ShowOverview ? 1 : 3;

    /// <summary>Ohne Protokoll reichen die oberen Bereiche bis unten.</summary>
    public int MainRowSpan => _layout.ShowProtocol ? 1 : 3;

    /// <summary>Werker: alle Messwerte einblenden (Standard laut Layout).</summary>
    public bool ShowAllMeasurements
    {
        get => _showAllMeasurements;
        set
        {
            if (_showAllMeasurements == value) return;
            _showAllMeasurements = value;
            RebuildMeasurements();
            OnPropertiesChanged(nameof(ShowAllMeasurements), nameof(HiddenMeasurementsText));
        }
    }

    /// <summary>Umschalter nur, wenn der Ablauf nicht standardmäßig alle Messwerte zeigt.</summary>
    public bool CanToggleMeasurements => !_layout.ShowAllMeasurements;

    public string HiddenMeasurementsText
    {
        get
        {
            var hidden = _allMeasurements.Count - Measurements.Count;
            return hidden > 0 ? $"{hidden} unauffällige Messwert(e) ausgeblendet" : "";
        }
    }

    private bool IsShown(MeasurementRow row) => _showAllMeasurements || row.Rating is ResultRating.Fail or ResultRating.Error;

    private void ClearMeasurements()
    {
        _allMeasurements.Clear();
        Measurements.Clear();
        OnPropertyChanged(nameof(HiddenMeasurementsText));
    }

    /// <summary>Messwert setzen (gleiche Bezeichnung = aktualisieren, Reihenfolge bleibt).</summary>
    private void SetMeasurement(MeasurementRow row)
    {
        var i = _allMeasurements.FindIndex(m => m.Name == row.Name);
        if (i < 0) _allMeasurements.Add(row);
        else _allMeasurements[i] = row;
        var shown = Measurements.FirstOrDefault(m => m.Name == row.Name);
        if (shown is not null && IsShown(row)) Measurements[Measurements.IndexOf(shown)] = row;
        else RebuildMeasurements();
        OnPropertyChanged(nameof(HiddenMeasurementsText));
    }

    private void RebuildMeasurements()
    {
        var wanted = _allMeasurements.Where(IsShown).ToList();
        if (wanted.SequenceEqual(Measurements)) return;
        Measurements.Clear();
        foreach (var m in wanted) Measurements.Add(m);
    }
}
