using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class TraceRestartTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static TraceChannelDefinition[] Channels() => [new("U", "V", null) { SafetyMaximum = 10 }];

    [Fact]
    public void Early_fail_keeps_its_result_until_an_explicit_restart_then_starts_fresh()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        var failed = recordings.Add("owner", "trace", 0, [11], Now)!;
        Assert.True(failed.Finished);
        Assert.False(failed.Passed);
        recordings.Start("owner", "trace", Channels(), Now.AddSeconds(1), duration: 1, formula: new FormulaLimits("0", "10"));
        Assert.False(recordings.IsEnded("owner", "trace"));
        Assert.False(recordings.HasViolation("owner", "trace"));
        Assert.False(recordings.IsPaused("owner", "trace"));
        recordings.Add("owner", "trace", null, [5], Now.AddSeconds(1));
        recordings.Add("owner", "trace", null, [5], Now.AddSeconds(2));
        var next = recordings.Stop("owner", "trace");
        Assert.True(next.Passed);
        Assert.Equal(new double[] { 0, 1 }, next.Data.Recording!.RawChannels[0].Times);
        Assert.NotNull(failed.EarlyStop);
        Assert.Equal(11, Assert.Single(failed.Data.Recording!.RawChannels[0].Values));
    }
    [Theory]
    [InlineData("deviation")]
    [InlineData("duration")]
    [InlineData("target")]
    [InlineData("slope")]
    public void Automatic_endings_allow_reusing_the_name(string mode)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            stopPercent: mode is "deviation" or "slope" ? 0 : null,
            duration: mode == "duration" ? 1 : null,
            formula: mode == "slope" ? null : new FormulaLimits("0", "10"),
            slope: mode == "slope" ? new(SlopeWindowMode.Consecutive, 2, -1, 1) : null,
            target: mode == "target" ? new TraceTargetBand(4, 6, 2) : null);
        var ended = recordings.Add("owner", "trace", 0, [mode == "deviation" ? 11 : 5], Now);
        if (mode != "deviation") ended = recordings.Add("owner", "trace", 1, [mode == "slope" ? 8 : 5], Now.AddSeconds(1));
        Assert.True(ended!.Finished);
        recordings.Start("owner", "trace", Channels(), Now.AddSeconds(2), formula: new FormulaLimits("0", "10"));
        Assert.False(recordings.IsEnded("owner", "trace"));
        Assert.False(recordings.TargetReached("owner", "trace"));
        Assert.False(recordings.HasViolation("owner", "trace"));
        recordings.Add("owner", "trace", 0, [5], Now);
        recordings.Add("owner", "trace", 1, [5], Now);
        Assert.True(recordings.Stop("owner", "trace").Passed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_or_paused_recordings_cannot_be_overwritten(bool paused)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        recordings.Add("owner", "trace", 0, [3], Now);
        recordings.SetPaused("owner", "trace", paused, Now);
        Assert.Throws<InvalidOperationException>(() => recordings.Start("owner", "trace", Channels(), Now,
            formula: new FormulaLimits("0", "10")));
        Assert.Equal(paused, recordings.IsPaused("owner", "trace"));
        Assert.Equal(3, Assert.Single(recordings.Stop("owner", "trace").Data.Recording!.RawChannels[0].Values));
    }

    [Fact]
    public void Invalid_restart_keeps_the_ended_fail_result_available()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        recordings.Add("owner", "trace", 0, [11], Now);
        Assert.Throws<ArgumentException>(() => recordings.Start("owner", "trace", Channels(), Now,
            maxGap: -1, formula: new FormulaLimits("0", "10")));
        var original = recordings.Stop("owner", "trace");
        Assert.False(original.Passed);
        Assert.NotNull(original.EarlyStop);
        Assert.Equal(11, Assert.Single(original.Data.Recording!.RawChannels[0].Values));
    }

    [Fact]
    public void Replacement_at_capacity_is_allowed_but_other_names_and_owners_stay_isolated()
    {
        var recordings = new EnvelopeRecordings();
        for (var i = 0; i < 8; i++)
            recordings.Start("owner", "trace" + i, Channels(), Now, formula: new FormulaLimits("0", "10"));
        recordings.Add("owner", "trace0", 0, [11], Now);
        recordings.Start("owner", "trace0", Channels(), Now, formula: new FormulaLimits("0", "10"));
        Assert.Throws<InvalidOperationException>(() => recordings.Start("other", "trace0", Channels(), Now,
            formula: new FormulaLimits("0", "10")));
        Assert.Throws<InvalidOperationException>(() => recordings.Start("owner", "extra", Channels(), Now,
            formula: new FormulaLimits("0", "10")));
        Assert.False(recordings.IsEnded("owner", "trace0"));
        Assert.Equal(8, recordings.CloseOwner("owner").Count);
    }

    [Fact]
    public async Task Repeated_manual_capture_after_early_fail_does_not_turn_into_name_errors()
    {
        static StepDefinition Block(string type, string id, params (string Key, string Value)[] values)
            => BlockCatalog.Default.Create(type, id, id, values.ToDictionary(v => v.Key, v => v.Value));
        var loop = Group("loop").Add(
            Block(TraceBlocks.Start, "begin", ("Grenzauswertung", TraceBlocks.FormulaBand),
                ("Untergrenzenformel", "0"), ("Obergrenzenformel", "10"), ("FailAbbruch", "true"), ("AbweichungProzent", "0")),
            Block(TraceBlocks.Point, "point", ("Zeit", "0"), ("Werte", "11")));
        loop.Repetition = Repetition.Times(3);
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });
        var result = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Fail, result.Rating);
        Assert.Equal(3, run.Steps("begin").Count());
        Assert.All(run.Steps("begin"), step => Assert.Equal(ResultRating.NResult, step.Rating));
        Assert.All(run.Steps("point"), step =>
        {
            Assert.Equal(ResultRating.Fail, step.Rating);
            Assert.NotNull(Assert.IsType<WaveformSet>(step.Value).Recording);
        });
        Assert.DoesNotContain(run.Events.OfType<StepCompleted>(), step => step.Rating == ResultRating.Error);
    }

    [Fact]
    public void Same_name_for_another_owner_remains_independent_during_restart()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("first", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        recordings.Start("second", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        recordings.Add("second", "trace", 0, [7], Now);
        recordings.Add("first", "trace", 0, [11], Now);
        recordings.Start("first", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        Assert.False(recordings.IsEnded("first", "trace"));
        Assert.Throws<InvalidOperationException>(() => recordings.Start("second", "trace", Channels(), Now,
            formula: new FormulaLimits("0", "10")));
        Assert.Equal(7, Assert.Single(recordings.Stop("second", "trace").Data.Recording!.RawChannels[0].Values));
        Assert.Empty(recordings.Stop("first", "trace").Data.Recording!.RawChannels[0].Values);
    }

}
