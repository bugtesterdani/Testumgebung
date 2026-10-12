using Testumgebung.Contracts;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class MeasurementStatisticsBoundaryTests
{
    public static IEnumerable<object?[]> Cases()
    {
        foreach (var low in new double?[] { null, 0 }) foreach (var high in new double?[] { null, 5 })
            foreach (var unit in new string?[] { null, "", "V" }) foreach (var values in new[] { new double[] { 2 }, new double[] { 2, 2 }, new double[] { -1, 2, 6 } })
                foreach (var node in new string?[] { null, "measurement" }) yield return [low, high, unit, values, node];
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void Measurement_statistics_handle_missing_limits_units_and_constant_values(double? low, double? high, string? unit, double[] values, string? node)
    {
        var events = values.Select((v, i) => new ResultEvent(i + 1, ResultEventKind.Step, node, "Group > Measurement", 1, DateTimeOffset.UnixEpoch, Value: i == 0 ? (object)(long)v : v, LowLimit: low, HighLimit: high, Unit: unit)).ToList();
        events.Add(new ResultEvent(99, ResultEventKind.Step, node, "Group > Measurement", 1, DateTimeOffset.UnixEpoch, Value: 1000.0, LowLimit: 0, Cancelled: true));
        var run = new ResultRun { RunId = "run", Events = events, Structure = node is null ? [] : [new ResultNode(node, null, "Measurement", "step", null, null)] };
        var statistics = ResultAnalysis.Compute([run], new StatisticsFilter());
        if (low is null && high is null && string.IsNullOrEmpty(unit)) { Assert.Empty(statistics.Measurements); return; }
        var measurement = Assert.Single(statistics.Measurements);
        Assert.Equal(node ?? "Group > Measurement", measurement.NodeId); Assert.Equal("Measurement", measurement.Name); Assert.Equal(unit ?? "", measurement.Unit);
        Assert.Equal(values.Length, measurement.Count); Assert.Equal(values, measurement.Values); Assert.Equal(values.Average(), measurement.Mean, 9);
        Assert.Equal(values.Count(v => low is { } l && v < l || high is { } h && v > h), measurement.OutOfLimits);
        var variable = values.Distinct().Count() > 1;
        Assert.Equal(variable && low is not null && high is not null, measurement.Cp is not null);
        Assert.Equal(variable && (low is not null || high is not null), measurement.Cpk is not null);
        var page = new ReportsViewModel(() => null, null, new QueueDispatcher()); Assert.Equal("", page.MeasurementText);
        page.SelectedMeasurement = measurement;
        Assert.Contains("n = " + values.Length, page.MeasurementText); Assert.Contains(measurement.Capability, page.MeasurementText);
        Assert.Equal(12, page.Histogram.Count); Assert.Equal(values.Length, measurement.Histogram().Sum());
        page.SelectedMeasurement = measurement; Assert.Equal(12, page.Histogram.Count);
        page.SelectedMeasurement = null; Assert.Empty(page.Histogram); Assert.Equal("", page.MeasurementText);
    }

    [Fact]
    public void Later_event_fills_missing_unit_and_preserves_previous_limits()
    {
        var run = new ResultRun { RunId = "run", Events = [new ResultEvent(1, ResultEventKind.Step, null, "Measurement", 1, DateTimeOffset.UnixEpoch, Value: 1L, LowLimit: 0), new ResultEvent(2, ResultEventKind.Step, null, "Measurement", 1, DateTimeOffset.UnixEpoch, Value: 2.0, HighLimit: 3, Unit: "V")] };
        var measurement = Assert.Single(ResultAnalysis.Compute([run], new StatisticsFilter()).Measurements);
        Assert.Equal("V", measurement.Unit); Assert.Equal(0, measurement.Low); Assert.Equal(3, measurement.High); Assert.NotNull(measurement.Cp); Assert.NotNull(measurement.Cpk);
    }
}
