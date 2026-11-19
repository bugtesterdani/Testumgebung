using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class PartialSlopeWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E3_final_disjoint_window_uses_the_actual_remainder_duration(bool fullRemainderRamp)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, duration: 2.29,
            slope: new(SlopeWindowMode.Disjoint, 50, -60, 6));
        for (var index = 0; index <= 229; index++)
        {
            var time = index * 0.01;
            var value = time <= 1.7 ? time * 5 : fullRemainderRamp
                ? time <= 2 ? 8.5 : 8.5 + (time - 2) * 10
                : time <= 2.2 ? 8.5 : 8.5 + (time - 2.2) / 0.09;
            recordings.Add("owner", "trace", time, [value], Now);
        }
        var result = recordings.Stop("owner", "trace");
        Assert.Equal(!fullRemainderRamp, result.Passed);
        Assert.Equal(fullRemainderRamp ? 1 : 0, Assert.Single(result.Channels).ViolationCount);
        Assert.Equal(fullRemainderRamp ? 10 : 1 / 0.29, result.Data.Channels[0].Values[^1], 8);
        Assert.Equal(2.29, Assert.Single(result.Channels[0].PartialWindowTimes), 8);
    }
    [Theory]
    [InlineData(4, true, 98)]
    [InlineData(5, true, 100)]
    [InlineData(6, false, 100)]
    public void Single_multiple_and_exact_window_endings_are_evaluated_once(int count, bool partial, double lastSlope)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Disjoint, 3, 0, 2));
        for (var i = 0; i < count; i++)
            recordings.Add("owner", "trace", i, [i < 3 ? i : (i - 2) * 100], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Passed);
        Assert.Equal(2, result.Data.Channels[0].Count);
        Assert.Equal(lastSlope, result.Data.Channels[0].Values[^1]);
        Assert.Equal(partial ? 1 : 0, result.Channels[0].PartialWindowTimes.Count);
    }

    [Fact]
    public void Target_preview_and_stop_do_not_duplicate_the_final_suppressed_window()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, minimumPoints: 4,
            slope: new(SlopeWindowMode.Disjoint, 3, -1, 1) { PositiveThreshold = 0.2, NegativeThreshold = -0.2 },
            target: new TraceTargetBand(4, 6, 4));
        for (var i = 0; i < 3; i++) recordings.Add("owner", "trace", i, [5], Now);
        var preview = recordings.Add("owner", "trace", 3, [5], Now)!;
        Assert.True(preview.Passed);
        Assert.Equal(2, preview.Data.Channels[0].Count);
        var stopped = recordings.Stop("owner", "trace");
        Assert.Equal(2, stopped.Data.Channels[0].Count);
        Assert.Equal(2, stopped.Channels[0].SuppressedSlopes.Count);
        Assert.Single(stopped.Channels[0].PartialWindowTimes);
        var plot = new Testumgebung.Presentation.ViewModels.WaveformPlotViewModel();
        plot.SetData(stopped.Data);
        Assert.True(plot.HasPartialWindowHint);
        Assert.Contains("verkürztes", plot.PartialWindowHint);
        plot.Clear();
        Assert.False(plot.HasPartialWindowHint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsafe_or_invalid_endings_do_not_finalize_the_remainder(bool faulted)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null) { SafetyMaximum = 10 }], Now,
            slope: new(SlopeWindowMode.Disjoint, 3, -1, 1));
        for (var i = 0; i < 4; i++) recordings.Add("owner", "trace", i, [0], Now);
        if (faulted) recordings.Invalidate("owner", "trace");
        else recordings.Add("owner", "trace", 4, [20], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Passed);
        Assert.Empty(result.Channels[0].PartialWindowTimes);
        Assert.Equal(1, result.Data.Channels[0].Count);
    }

    [Fact]
    public void Startup_rules_apply_to_a_partial_first_window()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Disjoint, 5, -1, 1)
            { InitialExceptionCount = 1, InitialExceptionAbove = 5, InitialExceptionWindow = 1 });
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 0.01, [0.5], Now);
        recordings.Add("owner", "trace", 0.02, [1], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Complete);
        Assert.Single(result.Channels[0].SuppressedSlopes);
        Assert.Single(result.Channels[0].PartialWindowTimes);
        Assert.Contains("ausschließlich Anlaufausnahmen", result.IncompleteReason);
    }

}
