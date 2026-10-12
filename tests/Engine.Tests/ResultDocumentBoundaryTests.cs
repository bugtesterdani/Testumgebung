using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ResultDocumentBoundaryTests
{
    public static IEnumerable<object?[]> Values()
    {
        yield return [null, "—"];
        yield return [true, "true"];
        yield return [false, "false"];
        yield return [42L, "42"];
        yield return ["text", "text"];
        yield return [new NullText(), ""];
        yield return [new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local), "02.01.2026 03:04:05"];
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void Reader_formats_typed_values_without_losing_content(object? value, string expected)
    {
        var document = new ResultDocumentViewModel(new ResultRun { RunId = "r", Events = [Event(value: value)] });
        var row = Assert.Single(document.Events);
        Assert.Equal(expected, row.Value);
        Assert.Equal("—", row.Path);
        Assert.Equal("—", row.Limits);
        Assert.Equal("—", row.RatingText);
        Assert.Equal("", row.Iteration);
        Assert.False(document.HasWaveforms);
        Assert.True(document.IsIntact);
        Assert.Contains("CRC", document.IntegrityText);
    }

    [Theory]
    [InlineData(ResultEventKind.Step, "Schritt")]
    [InlineData(ResultEventKind.Group, "Gruppe")]
    [InlineData(ResultEventKind.GroupNotEntered, "Nicht betreten")]
    [InlineData(ResultEventKind.OperatorShown, "Werkeranzeige")]
    [InlineData(ResultEventKind.OperatorAnswer, "Werkereingabe")]
    [InlineData(ResultEventKind.Diagnostic, "Hinweis")]
    [InlineData(ResultEventKind.Development, "Entwicklung")]
    [InlineData(ResultEventKind.Abort, "Werkerabbruch")]
    [InlineData((ResultEventKind)99, "99")]
    public void Reader_preserves_each_event_category(ResultEventKind kind, string expected)
    {
        var document = new ResultDocumentViewModel(new ResultRun { RunId = "r", Events = [Event(kind) with { Rating = ResultRating.Pass }] });
        Assert.Equal(expected, Assert.Single(document.Events).Kind);
        Assert.NotEqual("—", document.Events[0].RatingText);
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(false, "message", "image")]
    [InlineData(true, null, "image")]
    [InlineData(true, "message", null)]
    public void Reader_preserves_abort_attachment_and_measurement_metadata(bool cancelled, string? message, string? attachment)
    {
        foreach (var low in new double?[] { null, 0 }) foreach (var high in new double?[] { null, 5 })
        {
            var ev = Event(value: 2.5) with { Path = "Group > Measurement", Iteration = 2, Unit = "V", LowLimit = low, HighLimit = high, Cancelled = cancelled, Message = message, AttachmentId = attachment, WaveformIds = ["curve"] };
            var document = new ResultDocumentViewModel(new ResultRun { RunId = "r", Events = [ev, Event(ResultEventKind.Group)] });
            var row = Assert.Single(document.Events);
            Assert.Equal(low is null && high is null ? "Schritt" : "Messung", row.Kind);
            Assert.Equal("1 Kurve(n)", row.Value);
            Assert.Equal("2", row.Iteration);
            Assert.Equal("V", row.Unit);
            Assert.Equal(ev.Path, row.Path);
            var expected = cancelled ? "Abgebrochen" + (message is null ? "" : ": " + message) : message ?? "";
            if (attachment is not null) expected = (expected + " [Bild eingebettet]").Trim();
            Assert.Equal(expected, row.Message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5000)]
    public void Plot_handles_empty_constant_and_downsampled_curves(int count)
    {
        var samples = Enumerable.Range(0, count).Select(i => count < 4 ? 2.0 : i % 3 - 1.0).ToArray();
        var wave = new ResultWaveform("wave", 1, null, new Waveform("channel", "V", 100, DateTimeOffset.UnixEpoch, samples));
        var document = new ResultDocumentViewModel(new ResultRun { RunId = "r", Waveforms = [wave] });
        Assert.True(document.HasWaveforms);
        Assert.StartsWith("Kurven #1", Assert.Single(document.WaveformGroups).Title);
        var series = Assert.Single(document.Plot);
        Assert.Equal((count + Math.Max(1, count / 2000) - 1) / Math.Max(1, count / 2000), series.Points.Count);
        Assert.All(series.Points, p => { Assert.InRange(p.X, 0, 1000); Assert.InRange(p.Y, 0, 260); });
        Assert.Contains("channel", series.Info);
        document.SelectedGroup = document.SelectedGroup;
        Assert.Single(document.Plot);
        document.SelectedGroup = new WaveformGroup(2, "empty", []);
        Assert.Empty(document.Plot);
        Assert.Equal("", document.PlotRange);
        document.SelectedGroup = null;
        Assert.Empty(document.Plot);
    }

    [Fact]
    public void Reader_exposes_integrity_problems_header_and_matching_waveform_titles()
    {
        var run = new ResultRun { RunId = "r", DutId = "serial", Order = "A123", FilteredFor = "database", IsDevelopmentRun = true, Events = [Event() with { Path = "Group > Measurement" }], Waveforms = [new ResultWaveform("w", 1, null, new Waveform("C", "V", 1, DateTimeOffset.UnixEpoch, [0, 2], [0, 0.5]))] };
        var document = new ResultDocumentViewModel("C:/results/run.tresult", new ResultFileContent(run, ["bad chunk"], ResultFile.FormatVersion, 2048));
        Assert.Equal("run.tresult", document.FileName);
        Assert.False(document.IsIntact);
        Assert.Contains("1 Problem", document.IntegrityText);
        Assert.Contains(document.Header, h => h.Label == "Prüfling" && h.Value == "serial");
        Assert.Contains(document.Header, h => h.Label == "Entwicklungslauf" && h.Value == "ja");
        Assert.StartsWith("Measurement", Assert.Single(document.WaveformGroups).Title);
        Assert.Equal(1000, document.Plot[0].Points[^1].X);
    }

    private static ResultEvent Event(ResultEventKind kind = ResultEventKind.Step, object? value = null) => new(1, kind, null, "", 1, DateTimeOffset.UnixEpoch, Value: value);
    public sealed class NullText { public override string? ToString() => null; }
}
