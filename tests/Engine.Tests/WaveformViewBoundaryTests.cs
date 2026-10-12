using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class WaveformViewBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zoom_pan_cursor_and_visibility_preserve_valid_view(bool explicitTimes)
    {
        var plot = new WaveformPlotViewModel(); plot.SetView(-1, 5); Assert.False(plot.HasData); Assert.Equal("Keine Kurve", plot.ViewText); Assert.Equal("", plot.RangeText); Assert.Equal(0, plot.CursorX);
        var values = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
        plot.SetData([new PlotSource("A", "V", values)], 10, explicitTimes ? values.Select(v => v / 10).ToArray() : null);
        Assert.Equal(10, plot.Duration, 9); Assert.Equal(100, plot.Count); Assert.Single(plot.Series); Assert.Equal(5, plot.YLabels.Count); Assert.NotEmpty(plot.YTopText); Assert.NotEmpty(plot.YMidText); Assert.NotEmpty(plot.YBottomText);
        plot.SetView(-1, 1); Assert.Equal(0, plot.ViewStart); Assert.Equal(2, plot.ViewEnd);
        plot.SetView(9, 12); Assert.Equal(7, plot.ViewStart); Assert.Equal(10, plot.ViewEnd);
        plot.SetView(0, 100); Assert.Equal(0, plot.ViewStart); Assert.Equal(10, plot.ViewEnd);
        plot.SetView(2, 2); Assert.True(plot.ViewEnd > plot.ViewStart); plot.SetView(2, 6);
        plot.CursorTime = 4; plot.ZoomInCommand.Execute(null); Assert.Equal(3, plot.ViewStart); Assert.Equal(5, plot.ViewEnd); Assert.True(plot.HasCursor); Assert.Equal(500, plot.CursorX); Assert.Equal("40 V", plot.Channels[0].CursorText);
        plot.CursorTime = 0; plot.ZoomOutCommand.Execute(null); Assert.Equal(2, plot.ViewStart); Assert.Equal(6, plot.ViewEnd); Assert.False(plot.HasCursor);
        plot.ClearCursorCommand.Execute(null); Assert.False(plot.HasCursor); Assert.Contains("klicken", plot.CursorText);
        plot.PanLeftCommand.Execute(null); Assert.Equal(1, plot.ViewStart); plot.PanRightCommand.Execute(null); Assert.Equal(2, plot.ViewStart);
        plot.ZoomAllCommand.Execute(null); plot.ZoomToX(100, 101); Assert.Equal(10, plot.ViewEnd); plot.ZoomToX(600, 200); Assert.Equal(2, plot.ViewStart); Assert.Equal(6, plot.ViewEnd);
        plot.WheelZoom(500, true); Assert.Equal(2.4, plot.ViewStart, 9); plot.WheelZoom(500, false); Assert.Equal(2, plot.ViewStart, 9);
        plot.SetCursorAtX(-10); Assert.Equal(plot.ViewStart, plot.CursorTime); plot.SetCursorAtX(1100); Assert.Equal(plot.ViewEnd, plot.CursorTime); plot.CursorTime = 100; Assert.Equal(10, plot.CursorTime); Assert.Equal("99 V", plot.Channels[0].CursorText);
        plot.Channels[0].IsVisible = false; Assert.Empty(plot.Series); plot.SetData([new PlotSource("A", "V", values)], 10, null, true); Assert.False(plot.Channels[0].IsVisible); Assert.Equal(2, plot.ViewStart);
        plot.SetData([new PlotSource("A", "V", new double[] { 7 })], 0, new double[] { .5 }, true); Assert.Null(plot.CursorTime); Assert.Equal(.5, plot.Duration); plot.Channels[0].IsVisible = true; Assert.Single(plot.Series); Assert.Contains("7", plot.Channels[0].RmsText);
        plot.Clear(); Assert.Empty(plot.Series); Assert.Empty(plot.Channels); Assert.Equal(0, plot.Duration);
    }

    [Fact]
    public void Pixel_reduction_keeps_positive_and_negative_spikes_in_time_order()
    {
        var samples = new double[5000]; samples[1] = 10; samples[2] = -10; samples[7] = -20; samples[8] = 20;
        var plot = new WaveformPlotViewModel(); plot.SetData([new PlotSource("spikes", "V", samples)], 1000, null);
        var points = Assert.Single(plot.Series).Points; Assert.True(points.Count <= 2000); Assert.True(points.Count > 1000); Assert.Equal(points.OrderBy(p => p.X).ToArray(), points.ToArray()); Assert.True(plot.ShowZero);
        Assert.Contains(points, p => p.Y < 20); Assert.Contains(points, p => p.Y > 240);
        Assert.Equal("10000 ms", WaveformPlotViewModel.FormatTime(10 - 1e-10)); Assert.Equal("10 s", WaveformPlotViewModel.FormatTime(10));
    }
}
