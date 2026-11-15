using Testumgebung.Contracts;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class IndividualValueChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 13, 0, 0, TimeSpan.Zero);

    private static ResultRun Run(int index, double value, string station = "A", ResultRating rating = ResultRating.Fail,
        bool abort = false, ResultRunKind kind = ResultRunKind.Production, int? measurementMinute = null)
    {
        return new()
        {
            RunId = $"{station}-{index}",
            Station = station,
            TestName = "Test",
            TestVersion = "1",
            DutId = $"DUT-{index}",
            Rating = rating,
            OperatorAbort = abort,
            Kind = kind,
            Started = Start.AddMinutes(index),
            Ended = Start.AddMinutes(index).AddSeconds(5),
            Events = [new(1, ResultEventKind.Step, "voltage", "Spannung", 1,
                Start.AddMinutes(measurementMinute ?? index), Value: value, Unit: "V")],
        };
    }

    [Fact]
    public void Charts_use_measurement_timestamps_and_do_not_mix_stations()
    {
        var stats = ResultAnalysis.Compute([
            Run(1, 2, measurementMinute: 6), Run(3, 1), Run(6, 3, measurementMinute: 10),
            Run(2, 42, "B"), Run(4, 999, kind: ResultRunKind.BadReference), Run(5, double.NaN),
        ], new StatisticsFilter());
        var charts = Assert.Single(stats.Measurements).Charts;
        Assert.Equal(2, charts.Count);
        var chart = charts.Single(item => item.Series.Station == "A");
        Assert.Equal(new[] { Start.AddMinutes(3), Start.AddMinutes(6), Start.AddMinutes(10) }, chart.Points.Select(point => point.Time));
        Assert.Equal(new[] { 1d, 2d, 3d }, chart.Points.Select(point => point.Value));
        Assert.Equal(2, chart.Mean);
        Assert.Equal(2.66, chart.ControlRadius);
        Assert.Equal((3, 1), chart.LongestTrends());
        Assert.Null(charts.Single(item => item.Series.Station == "B").ControlRadius);
    }

    [Fact]
    public void Streaks_reset_on_aborts_and_equal_values_end_monotonic_trends()
    {
        var stats = ResultAnalysis.Compute([
            Run(0, 1), Run(1, 3, "B"), Run(2, 1), Run(3, 2, abort: true), Run(4, 3), Run(5, 2), Run(6, 1),
        ], new StatisticsFilter());
        Assert.Equal(3, stats.LongestFailStreak);
        var chart = Assert.Single(stats.Measurements).Charts.Single(item => item.Series.Station == "A");
        Assert.Equal((3, 3), chart.LongestTrends());
    }

    [Fact]
    public async Task Reports_page_displays_timestamped_points_and_optional_warnings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-control-chart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (var index = 1; index <= 3; index++)
            {
                using var file = File.Create(Path.Combine(folder, index + ResultFile.Extension));
                ResultFile.Write(file, Run(index, index));
            }
            var page = new ReportsViewModel(() => folder, null, new QueueDispatcher());
            await page.LoadAsync();
            Assert.True(page.HasControlCharts);
            Assert.Equal(4, page.ControlPlot.Channels.Count);
            Assert.Equal(Start.AddMinutes(1), page.ControlPlot.TimeOrigin);
            Assert.Equal(120, page.ControlPlot.Duration);
            Assert.Null(page.FailStreakWarning);
            Assert.Null(page.TrendWarning);
            page.FailWarningCount = 3;
            page.TrendWarningCount = 3;
            Assert.Contains("3 FAIL", page.FailStreakWarning);
            Assert.Contains("3 Messwerte in Folge steigend", page.TrendWarning);
            page.ControlPlot.CursorTime = 60;
            Assert.Contains(Start.AddMinutes(2).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"), page.ControlPlot.CursorText);
            page.SelectedMeasurement = null;
            Assert.False(page.ControlPlot.HasData);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
