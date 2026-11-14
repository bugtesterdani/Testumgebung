using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class SlopeTraceTests
{
    private static StepDefinition Block(string type, string id, params (string Key, string Value)[] parameters)
        => BlockCatalog.Default.Create(type, id, id, parameters.ToDictionary(pair => pair.Key, pair => pair.Value));
    [Theory]
    [InlineData(TraceBlocks.Start)]
    [InlineData(TraceBlocks.Record)]
    public void Editor_shows_only_the_fields_for_the_selected_trace_evaluation(string blockType)
    {
        var editor = new EditorViewModel(new TestProject(Test(Group("loop"))));
        editor.InsertAt(editor.Palette.SelectMany(category => category.Items).Single(item => item.BlockType == blockType),
            editor.FindNode("loop")!, 0);
        var properties = Assert.IsType<StepPropertiesViewModel>(editor.Properties);
        var parameters = properties.Parameters.ToDictionary(row => row.Descriptor.Name);
        var mode = parameters[TraceBlocks.EvaluationParameter];

        Assert.True(parameters["Kanaele"].IsTable);
        Assert.True(parameters["Steigungsfenster"].IsHidden);
        Assert.True(parameters["SteigungMin"].IsHidden);

        mode.Value = TraceBlocks.PairSlope;
        Assert.True(parameters["Grenzkurven"].IsHidden);
        Assert.True(parameters[TraceBlocks.AbortCurvesParameter].IsHidden);
        Assert.True(parameters["Steigungsfenster"].IsHidden);
        Assert.False(parameters["SteigungMin"].IsHidden);
        Assert.False(parameters["SteigungMax"].IsHidden);

        mode.Value = TraceBlocks.DisjointSlope;
        Assert.False(parameters["Steigungsfenster"].IsHidden);
        mode.Value = TraceBlocks.RollingSlope;
        Assert.False(parameters["Steigungsfenster"].IsHidden);
    }

    [Theory]
    [InlineData("Steigung je Nachbarpaar", 5, new double[] { 1, 3, 4, 6, 7 }, new double[] { 2, -1, 6, 2, -2 })]
    [InlineData("Steigung in getrennten Fenstern", 3, new double[] { 3, 7 }, new double[] { 0, 2.0 / 3 })]
    [InlineData("Steigung im gleitenden Fenster", 3, new double[] { 3, 4, 6, 7 }, new double[] { 0, 4.0 / 3, 10.0 / 3, 2.0 / 3 })]
    public async Task Manual_trace_rates_completed_windows_using_elapsed_seconds(
        string mode, int points, double[] expectedTimes, double[] expectedValues)
    {
        var loop = Group("loop").Add(Block(TraceBlocks.Start, "start",
            ("Grenzauswertung", mode), ("Steigungsfenster", points.ToString()),
            ("SteigungMin", "-1"), ("SteigungMax", "3"), ("MaxLuecke", "2")));
        foreach (var (time, value) in new[] { ("0", "0"), ("1", "2"), ("3", "0"), ("4", "6"), ("6", "10"), ("7", "8") })
            loop.Add(Block(TraceBlocks.Point, "point-" + time, ("Zeit", time), ("Werte", value)));
        loop.Add(Block(TraceBlocks.Stop, "stop"));
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });

        await run.RunAsync().Within();
        var stop = run.Steps("stop").Single();
        Assert.Equal(expectedValues.Any(value => value < -1 || value > 3) ? ResultRating.Fail : ResultRating.Pass, stop.Rating);
        var data = Assert.IsType<WaveformSet>(stop.Value);
        Assert.Equal("V/s", data.Channels[0].Unit);
        Assert.Equal(expectedValues, data.Channels[0].Samples);
        var metadata = EnvelopeTraceMetadata.Decode(data.Channels[0].Quality)!;
        Assert.Equal(expectedTimes, metadata.OriginalTimes);
        Assert.Equal(expectedTimes.Where((_, index) => expectedValues[index] < -1 || expectedValues[index] > 3).ToArray(), metadata.Violations);
    }

    [Fact]
    public async Task Timed_trace_uses_slope_limits_without_loading_value_curves()
    {
        var capture = Block(TraceBlocks.Record, "capture", ("Grenzauswertung", TraceBlocks.RollingSlope),
            ("Steigungsfenster", "3"), ("SteigungMin", "-1"), ("SteigungMax", "1"),
            ("Grenzkurven", "unused@1"), ("Dauer", "2"), ("Intervall", "0.5"), ("Werte", "5"));
        var run = new TestRun(Test(Group("loop").Add(capture)), new TestRunOptions { Clock = new VirtualClock() });

        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        var data = Assert.IsType<WaveformSet>(run.Steps("capture").Single().Value);
        Assert.Equal(new double[] { 1, 1.5, 2 }, data.Channels[0].Times);
        Assert.Equal(new double[] { 0, 0, 0 }, data.Channels[0].Samples);
    }

    [Fact]
    public void Disjoint_windows_ignore_trailing_points_and_require_at_least_one_full_window()
    {
        var now = DateTimeOffset.UnixEpoch;
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "short", [new("U", "V", null)], now,
            slope: new(SlopeWindowMode.Disjoint, 3, 0, 2));
        recordings.Add("owner", "short", 0, [0], now);
        recordings.Add("owner", "short", 1, [1], now);
        Assert.False(recordings.Stop("owner", "short").Complete);

        recordings.Start("owner", "full", [new("U", "V", null)], now,
            slope: new(SlopeWindowMode.Disjoint, 3, 0, 2));
        recordings.Add("owner", "full", 0, [0], now);
        recordings.Add("owner", "full", 1, [1], now);
        recordings.Add("owner", "full", 2, [2], now);
        recordings.Add("owner", "full", 3, [100], now);
        var result = recordings.Stop("owner", "full");
        Assert.True(result.Passed);
        Assert.Equal(1, Assert.Single(result.Data.Channels[0].Values));
    }

    [Fact]
    public void Slope_early_stop_triggers_on_the_first_violating_window_end()
    {
        var now = DateTimeOffset.UnixEpoch;
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], now,
            stopPercent: 10, slope: new(SlopeWindowMode.Rolling, 3, 0, 2));
        recordings.Add("owner", "trace", 0, [0], now);
        recordings.Add("owner", "trace", 1, [8], now);
        Assert.False(recordings.IsEnded("owner", "trace"));
        var result = recordings.Add("owner", "trace", 2, [6], now)!;
        Assert.True(result.Finished);
        Assert.False(result.Passed);
        Assert.Equal(2, result.EarlyStop!.Time);
        Assert.Equal(3, result.EarlyStop.Value);
    }
}
