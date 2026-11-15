using Testumgebung.Persistence.Reports;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class ReportsViewModel
{
    private IndividualValueChart? _selectedControlChart;
    private int _failWarningCount;
    private int _trendWarningCount;

    public int FailWarningCount
    {
        get => _failWarningCount;
        set { if (SetProperty(ref _failWarningCount, Math.Max(0, value))) OnPropertyChanged(nameof(FailStreakWarning)); }
    }

    public int TrendWarningCount
    {
        get => _trendWarningCount;
        set { if (SetProperty(ref _trendWarningCount, Math.Max(0, value))) OnPropertyChanged(nameof(TrendWarning)); }
    }

    public string? FailStreakWarning
        => FailWarningCount > 0 && _stats is { } stats && stats.LongestFailStreak >= FailWarningCount
            ? $"Im ausgewerteten Zeitraum: bis zu {stats.LongestFailStreak} FAIL in Folge am selben Prüfplatz und Prüfprogrammstand."
            : null;

    public string? TrendWarning => TrendMessage();

    public WaveformPlotViewModel ControlPlot { get; } = new() { ShowRms = false };
    public IReadOnlyList<IndividualValueChart> ControlCharts => SelectedMeasurement?.Charts ?? [];
    public bool HasControlCharts => ControlCharts.Count > 0;

    public IndividualValueChart? SelectedControlChart
    {
        get => _selectedControlChart;
        set
        {
            if (!SetProperty(ref _selectedControlChart, value)) return;
            UpdateControlPlot(value);
            OnPropertiesChanged(nameof(TrendWarning), nameof(ControlLimitsText));
        }
    }

    public string ControlLimitsText => SelectedControlChart is { ControlRadius: { } radius } chart
        ? $"Untere Eingriffsgrenze {(chart.Mean - radius).ToString("0.####", De)} · "
            + $"Mittelwert {chart.Mean.ToString("0.####", De)} · "
            + $"Obere Eingriffsgrenze {(chart.Mean + radius).ToString("0.####", De)} {chart.Series.Unit}"
        : "Eingriffsgrenzen benötigen mindestens zwei Messwerte.";

    private void UpdateControlCharts()
    {
        var previous = SelectedControlChart?.Series;
        OnPropertiesChanged(nameof(ControlCharts), nameof(HasControlCharts));
        SelectedControlChart = ControlCharts.FirstOrDefault(chart => chart.Series == previous) ?? ControlCharts.FirstOrDefault();
    }

    private string? TrendMessage()
    {
        if (TrendWarningCount < 2 || SelectedControlChart is not { } chart) return null;
        var (rising, falling) = chart.LongestTrends();
        if (Math.Max(rising, falling) < TrendWarningCount) return null;
        return $"Trend im ausgewerteten Zeitraum: {Math.Max(rising, falling)} Messwerte in Folge {(rising >= falling ? "steigend" : "fallend")}.";
    }

    private void UpdateControlPlot(IndividualValueChart? chart)
    {
        if (chart is null || chart.Points.Count == 0)
        {
            ControlPlot.TimeOrigin = null;
            ControlPlot.Clear();
            return;
        }
        var origin = chart.Points[0].Time;
        var times = PlotTimes(chart.Points, origin);
        var values = chart.Points.Select(point => point.Value).ToArray();
        const string measurements = "Einzelwerte";
        var channels = new List<PlotSource> { new(measurements, chart.Series.Unit, values) };
        var center = chart.Mean;
        var violations = new Dictionary<string, double[]>();
        if (chart.ControlRadius is { } radius && double.IsFinite(radius))
        {
            var low = center - radius;
            var high = center + radius;
            AddLine("Untere Eingriffsgrenze", low);
            AddLine("Mittelwert", center);
            AddLine("Obere Eingriffsgrenze", high);
            violations[measurements] = times.Where((_, index) => values[index] < low || values[index] > high).ToArray();
        }
        ControlPlot.TimeOrigin = origin;
        ControlPlot.SetData(channels, 1, times, pointTrace: true, violations: violations,
            sampleTimes: new Dictionary<string, double[]> { [measurements] = times });

        void AddLine(string name, double value)
            => channels.Add(new(name, chart.Series.Unit, Enumerable.Repeat(value, times.Length).ToArray()));
    }

    private static double[] PlotTimes(IReadOnlyList<MeasurementPoint> points, DateTimeOffset origin)
    {
        var times = new double[points.Count];
        for (var index = 1; index < times.Length; index++)
        {
            var seconds = (points[index].Time - origin).TotalSeconds;
            // Preserve distinct samples that share a timestamp in the renderer.
            times[index] = Math.Max(seconds, Math.BitIncrement(times[index - 1]));
        }
        return times;
    }
}
