using System.Text;
using Testumgebung.Persistence.Reports;

namespace Testumgebung.Engine.Tests;

public sealed class StatisticsReportContractTests
{
    public static IEnumerable<object[]> Reports()
    {
        foreach (var count in new[] { 0, 1, 100 })
            foreach (var pass in new[] { false, true })
                foreach (var capability in new double?[] { null, .5, 1, 1.33 })
                    yield return [count, pass, capability!];
    }

    [Theory, MemberData(nameof(Reports))]
    public void Statistics_report_handles_missing_metrics_and_all_capability_ratings(int count, bool pass, double? capability)
    {
        var measurements = new[]
        {
            new MeasurementStatistics("U", "Spannung", "V", count, 1, 3, 2, .1, 0, 4, pass ? 0 : 1, capability, capability, count == 0 ? [] : [1, 2, 3]),
            new MeasurementStatistics("I", "Strom", "", count, 2, 2, 2, 0, null, null, 0, null, null, count == 0 ? [] : [2, 2]),
            new MeasurementStatistics("R", "Widerstand", "Ohm", count, 0, 1, .5, .1, null, 2, 0, null, capability, [0, 1]),
        };
        var stats = new ResultStatistics
        {
            Filter = new StatisticsFilter(),
            Runs = count,
            Pass = pass ? count : 0,
            Fail = pass ? 0 : count,
            First = count == 0 ? null : DateTimeOffset.UnixEpoch,
            Last = count == 0 ? null : DateTimeOffset.UnixEpoch.AddSeconds(1),
            MeanDuration = count == 0 ? null : TimeSpan.FromSeconds(1),
            FirstPassYield = count == 0 ? null : .9,
            FinalYield = count == 0 ? null : 1,
            Measurements = measurements,
            Failures = [new("U", "Spannung", pass ? 0 : count, pass ? count : 0, count, pass ? null : "Grenzwert")],
            Daily = [new(DateOnly.MinValue, pass ? 1 : 0, pass ? 0 : 1, pass ? 0 : 1, 0)],
            Stations = [new("P1", count, pass ? count : 0, pass ? 1 : 0), new("P2", count, pass ? count : 0, pass ? 1 : 0)]
        };
        foreach (var company in new string?[] { null, "Firma" })
        {
            var report = Testumgebung.Persistence.Reports.Reports.StatisticsReport(stats, company);
            Assert.NotEmpty(report.Pages);
            var pdf = Encoding.Latin1.GetString(report.ToBytes());
            Assert.StartsWith("%PDF-", pdf);
            Assert.Contains("Messwerte und Prozessf", pdf);
            Assert.Contains("Pr", pdf);
            Assert.EndsWith("%%EOF\n", pdf);
        }
        foreach (var measurement in measurements)
            Assert.Equal(measurement.Values.Count, measurement.Histogram().Sum());
    }
}
