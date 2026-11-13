using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class AbsoluteTraceTests
{
    [Theory]
    [InlineData(-0.51, true)]
    [InlineData(-0.5, false)]
    [InlineData(4.2, false)]
    [InlineData(4.5, false)]
    [InlineData(4.51, true)]
    public void Slope_margin_extends_both_bounds_without_changing_fail_rating(double slope, bool early)
    {
        var recordings = new EnvelopeRecordings();
        var now = DateTimeOffset.UnixEpoch;
        recordings.Start("owner", "trace", [new("U", "V", null)], now,
            slope: new(SlopeWindowMode.Consecutive, 2, 0, 4), stopAbsolute: 0.5);
        recordings.Add("owner", "trace", 0, [0], now);
        recordings.Add("owner", "trace", 1, [slope], now);

        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Passed);
        Assert.Equal(early, result.EarlyStop is not null);
        Assert.Equal(early ? 0.5 : null, EnvelopeTraceMetadata.Decode(result.Data.Channels[0].Quality)!.EarlyStop?.AbsoluteDeviation);
    }

    [Fact]
    public void Explicit_abort_curve_takes_priority_over_absolute_margin()
    {
        static CurveDefinition Band(string id, double upper) => new()
        {
            Id = id, Name = id, Version = "1", SampleRate = 1, Times = [0, 1],
            Channels = [new("Untergrenze", "V", [0, 0]), new("Obergrenze", "V", [upper, upper])]
        };
        var recordings = new EnvelopeRecordings();
        var now = DateTimeOffset.UnixEpoch;
        recordings.Start("owner", "trace", [new("U", "V", Band("normal", 4)) { AbortLimits = Band("abort", 5) }],
            now, stopAbsolute: 2);
        var result = recordings.Add("owner", "trace", 0, [5.5], now)!;
        Assert.Equal("abort@1", result.EarlyStop!.AbortCurve);
        Assert.Null(result.EarlyStop.AbsoluteDeviation);
    }

    [Fact]
    public async Task Formula_recording_applies_absolute_margin_instead_of_saved_percent()
    {
        var capture = BlockCatalog.Default.Create(TraceBlocks.Record, "capture", parameters: new Dictionary<string, string>
        {
            [TraceBlocks.EvaluationParameter] = TraceBlocks.FormulaBand,
            [TraceBlocks.LowerFormulaParameter] = "0",
            [TraceBlocks.UpperFormulaParameter] = "4 + t",
            [TraceBlocks.StopEnabledParameter] = "true",
            [TraceBlocks.StopMarginModeParameter] = TraceBlocks.AbsoluteMargin,
            [TraceBlocks.AbsoluteMarginParameter] = "0,5",
            [TraceBlocks.PercentMarginParameter] = "100",
            ["Werte"] = "4.6"
        });
        var run = new TestRun(Test(Group("loop").Add(capture)), new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();

        var result = run.Steps("capture").Single();
        Assert.Equal(ResultRating.Fail, result.Rating);
        var data = Assert.IsType<WaveformSet>(result.Value);
        var trigger = EnvelopeTraceMetadata.Decode(data.Channels[0].Quality)!.EarlyStop!;
        Assert.Equal(0, trigger.Time);
        Assert.Equal(0.5, trigger.AbsoluteDeviation);
    }

    [Fact]
    public void Editor_switches_between_percent_and_absolute_fields()
    {
        var editor = new EditorViewModel(new TestProject(Test(Group("loop"))));
        editor.InsertAt(editor.Palette.SelectMany(category => category.Items).Single(item => item.BlockType == TraceBlocks.Start),
            editor.FindNode("loop")!, 0);
        var rows = Assert.IsType<StepPropertiesViewModel>(editor.Properties).Parameters.ToDictionary(row => row.Descriptor.Name);
        var mode = rows[TraceBlocks.StopMarginModeParameter];
        var percent = rows[TraceBlocks.PercentMarginParameter];
        var absolute = rows[TraceBlocks.AbsoluteMarginParameter];

        Assert.True(mode.IsHidden);
        rows[TraceBlocks.StopEnabledParameter].BoolValue = true;
        Assert.False(mode.IsHidden);
        Assert.False(percent.IsHidden);
        Assert.True(absolute.IsHidden);
        mode.Value = TraceBlocks.AbsoluteMargin;
        Assert.True(percent.IsHidden);
        Assert.False(absolute.IsHidden);
    }
}
