using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class SlopeAuditDisplayTests
{
    private static readonly SuppressedSlopeSample Sample = new(1, 100, 0.99, 0, 1, "Anlaufausnahme 1 von 1");
    private static Waveform Wave(string channel, bool metadata = true) => new(channel, "V", 1, DateTimeOffset.UnixEpoch, Enumerable.Range(0, 11).Select(i => i == 1 ? 100.0 : i).ToArray(), Enumerable.Range(0, 11).Select(i => (double)i).ToArray(),
        metadata ? new EnvelopeTraceMetadata("Steigung", "", channel, [0, 1, 2], [], 0, 0, true) { SuppressedSlopes = [Sample] }.Encode() : null);

    [Fact]
    public void Plot_displays_neutral_markers_with_complete_audit_details()
    {
        var plot = new WaveformPlotViewModel();
        plot.SetData(new WaveformSet([Wave("U"), Wave("I")]));
        Assert.Equal(2, plot.SuppressionMarkers.Count);
        var marker = plot.SuppressionMarkers[0];
        Assert.Equal(100, marker.X);
        Assert.Equal(plot.Series[0].Points.Single(point => point.X == marker.X).Y, marker.Y);
        Assert.Contains("Steigung=100", marker.Description);
        Assert.Contains("Start t=", marker.Description);
        Assert.Contains("Endwert=1", marker.Description);
        Assert.Contains(Sample.Reason, marker.Description);
        Assert.Empty(plot.ViolationMarkers);
        plot.Channels[0].IsVisible = false;
        Assert.StartsWith("I:", Assert.Single(plot.SuppressionMarkers).Description);
        plot.SetView(6, 10);
        Assert.Empty(plot.SuppressionMarkers);
        plot.Clear();
        Assert.Empty(plot.SuppressionMarkers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Result_reader_lists_saved_exclusions_per_channel_and_accepts_old_results(bool metadata)
    {
        var document = new ResultDocumentViewModel(new ResultRun { RunId = "audit", Waveforms =
            [new ResultWaveform("u", 1, null, Wave("U", metadata)), new ResultWaveform("i", 1, null, Wave("I", metadata))] });
        Assert.Equal(metadata ? 2 : 0, document.SuppressedSlopes.Count);
        if (metadata)
        {
            Assert.Equal(new[] { "U", "I" }, document.SuppressedSlopes.Select(row => row.Channel));
            Assert.All(document.SuppressedSlopes, row => Assert.Equal(Sample, row.Sample));
        }
        document.SelectedGroup = null;
        Assert.Empty(document.SuppressedSlopes);
    }
}
