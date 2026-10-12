using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class IndividualReportBoundaryTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var rating in Enum.GetValues<ResultRating>()) foreach (var abort in new[] { false, true })
            foreach (var metadata in new[] { false, true }) yield return [rating, abort, metadata];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Individual_pdf_preserves_measurements_operator_answers_failures_and_run_metadata(ResultRating rating, bool abort, bool metadata)
    {
        var values = new object?[] { null, true, false, 2.5, 42L, "text", new DateTime(2026, 1, 2) };
        var events = values.Select((v, i) => new ResultEvent(i + 1, ResultEventKind.Step, metadata ? "node" : null, "Root > Measurement", i % 2 + 1, DateTimeOffset.UnixEpoch, i % 2 == 0 ? rating : null, v, i % 3 == 0 ? null : i % 3 == 1 ? "" : "V", i % 2 == 0 ? 0 : null, i % 3 == 0 ? 5 : null, Message: i % 2 == 0 ? "message" : null, Cancelled: i == 0)).ToList();
        events.Add(new ResultEvent(10, ResultEventKind.Diagnostic, null, "Root", 1, DateTimeOffset.UnixEpoch, ResultRating.Error, Value: false));
        events.Add(new ResultEvent(11, ResultEventKind.GroupNotEntered, null, "Root", 1, DateTimeOffset.UnixEpoch));
        events.Add(new ResultEvent(12, ResultEventKind.Group, "group", "Root", 1, DateTimeOffset.UnixEpoch, metadata ? rating : null, 2L, Message: metadata ? "group message" : null));
        events.AddRange(values.Select((v, i) => new ResultEvent(20 + i, ResultEventKind.OperatorAnswer, null, "Operator", 1, DateTimeOffset.UnixEpoch, Value: v)));
        var run = new ResultRun { RunId = "report-run", DutId = metadata ? "SN-1" : " ", TestName = "Report fixture", TestVersion = "1", Started = DateTimeOffset.UnixEpoch, Ended = DateTimeOffset.UnixEpoch.AddSeconds(5), Rating = rating, OperatorAbort = abort, IsDevelopmentRun = metadata, FilteredFor = metadata ? "file" : "", Events = events, Structure = metadata ? [new ResultNode("node", null, "Measurement", "step", null, null)] : [] };
        foreach (var source in new string?[] { null, "C:/results/run.tres" })
        {
            var pdf = Encoding.Latin1.GetString(Reports.TestReport(run, source, metadata ? "Company" : null).ToBytes());
            Assert.StartsWith("%PDF-", pdf); Assert.EndsWith("%%EOF\n", pdf);
            Assert.Contains("report-run", pdf); Assert.Contains("Report fixture", pdf); Assert.Contains("Werkereingaben", pdf);
            Assert.Contains("Abweichungen", pdf); Assert.Contains("Alle Schritte", pdf);
            if (abort) Assert.Contains("WERROR", pdf);
            if (source is not null) Assert.Contains("run.tres", pdf);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(2500)]
    public void Waveform_report_handles_empty_and_downsampled_curves_with_or_without_event_reference(int count)
    {
        var samples = Enumerable.Range(0, count).Select(i => (double)(i % 5)).ToArray();
        foreach (var referenced in new[] { false, true })
        {
            var run = new ResultRun { RunId = "wave-report", Events = referenced ? [new ResultEvent(1, ResultEventKind.Step, null, "Waveform", 1, DateTimeOffset.UnixEpoch)] : [], Waveforms = [new ResultWaveform("curve", 1, null, new Waveform("CH1", "V", 1000, DateTimeOffset.UnixEpoch, samples))] };
            var pdf = Encoding.Latin1.GetString(Reports.TestReport(run).ToBytes()); Assert.Contains("CH1", pdf); Assert.Contains("Effektivwert", pdf);
        }
    }
}
