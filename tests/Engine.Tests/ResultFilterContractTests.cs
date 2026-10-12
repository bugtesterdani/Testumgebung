using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class ResultFilterContractTests
{
    public static IEnumerable<object[]> Filters()
    {
        foreach (var field in new[] { "From", "To", "Test", "Station", "Dut", "Version" })
            foreach (var variant in new[] { "none", "empty", "spaces", "match", "mismatch" })
                foreach (var development in new[] { false, true })
                    foreach (var include in new[] { false, true })
                        yield return [field, variant, development, include];
    }

    [Theory, MemberData(nameof(Filters))]
    public void Query_and_report_filters_respect_boundaries_and_development_runs(string field, string variant, bool development, bool include)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var run = new ResultRun { RunId = "R1", Started = now, TestName = "Test", TestVersion = "1", Station = "P1", DutId = "SN123", IsDevelopmentRun = development };
        string? Text(string match) => variant switch { "none" => null, "empty" => "", "spaces" => "  ", "match" => match, _ => "other" };
        var from = field == "From" && variant is "match" or "mismatch" ? now.AddSeconds(variant == "match" ? 0 : 1) : (DateTimeOffset?)null;
        var to = field == "To" && variant is "match" or "mismatch" ? now.AddSeconds(variant == "match" ? 0 : -1) : (DateTimeOffset?)null;
        var query = new RunQuery(from, to, field == "Test" ? Text("Test") : null, field == "Station" ? Text("P1") : null,
            field == "Dut" ? Text("SN123") : null, include);
        Assert.Equal((include || !development) && (variant != "mismatch" || field == "Version"), query.Matches(run));
        var filter = new StatisticsFilter
        {
            From = from,
            To = to,
            Test = query.Test,
            Station = query.Station,
            DutPattern = field == "Dut" ? Text("SN?*") : null,
            Version = field == "Version" ? Text("1") : null,
            IncludeDevelopment = include
        };
        Assert.Equal((include || !development) && variant != "mismatch", filter.Matches(run));
        Assert.Contains(include ? "inkl. Entwicklungsläufe" : "ohne Entwicklungsläufe", filter.Describe());
    }
}
