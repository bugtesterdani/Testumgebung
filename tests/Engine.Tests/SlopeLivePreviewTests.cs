using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class SlopeLivePreviewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(SlopeWindowMode.Consecutive, 2)]
    [InlineData(SlopeWindowMode.Disjoint, 50)]
    [InlineData(SlopeWindowMode.Rolling, 50)]
    public void First_raw_sample_is_reported_without_waiting_for_a_slope_window(SlopeWindowMode mode, int points)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(mode, points, -1, 1));
        var preview = recordings.Add("owner", "trace", 0, [5], Now);
        Assert.NotNull(preview);
        Assert.False(preview.Finished);
        var raw = Assert.Single(preview.Data.Recording!.RawChannels);
        Assert.Equal(5, Assert.Single(raw.Values));
        Assert.DoesNotContain(preview.Data.Channels, wave => wave.Count > 0 && wave.Unit == "V/s");
    }
    [Fact]
    public void Large_disjoint_recording_reports_raw_samples_every_250_ms_with_throttling()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Disjoint, 50, -1, 1));
        var reportTimes = new List<int>();
        for (var i = 0; i < 10000; i++)
        {
            var preview = recordings.Add("owner", "trace", i * 0.01, [5], Now.AddMilliseconds(i * 10));
            if (preview is null) continue;
            reportTimes.Add(i * 10);
            Assert.Equal(i + 1, preview.Data.Recording!.RawChannels[0].Count);
            var slope = preview.Data.Channels.FirstOrDefault(w => w.Channel == "U");
            Assert.Equal((i + 1) / 50, slope?.Count ?? 0);
        }
        Assert.Equal(0, reportTimes[0]);
        Assert.Equal(10, reportTimes[1]);
        Assert.InRange(reportTimes.Count, 399, 402);
        Assert.All(reportTimes.Zip(reportTimes.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 10, 250));
        Assert.True(recordings.Stop("owner", "trace").Passed);
    }

    [Fact]
    public void Violation_and_end_are_reported_immediately_inside_the_throttle_interval()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, duration: 0.03,
            slope: new(SlopeWindowMode.Disjoint, 3, -1, 1));
        Assert.NotNull(recordings.Add("owner", "trace", 0, [0], Now));
        Assert.NotNull(recordings.Add("owner", "trace", 0.01, [0], Now.AddMilliseconds(10)));
        var failure = recordings.Add("owner", "trace", 0.02, [1], Now.AddMilliseconds(20));
        Assert.NotNull(failure);
        Assert.Equal(1, failure.Violations);
        var end = recordings.Add("owner", "trace", 0.03, [1], Now.AddMilliseconds(30));
        Assert.True(end!.Finished);
        Assert.False(end.Passed);
        Assert.Equal(2, end.Data.Channels[0].Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Additional_previews_preserve_point_ratings_and_stable_target_output(bool violating)
    {
        static StepDefinition Block(string type, string id, params (string Key, string Value)[] values)
            => BlockCatalog.Default.Create(type, id, id, values.ToDictionary(v => v.Key, v => v.Value));
        var loop = Group("loop")
            .Var("Stable", VarType.Bool, false)
            .Var("Observed", VarType.Bool, false);
        loop.Add(Block(TraceBlocks.Start, "begin",
            ("Grenzauswertung", TraceBlocks.DisjointSlope), ("Steigungsfenster", "3"),
            ("SteigungMin", "-1"), ("SteigungMax", "1"), ("ZielerkennungAktiv", "true"),
            ("ZielbandMin", "-1"), ("ZielbandMax", "2"), ("ZielbandPunkte", "3")));
        for (var i = 0; i < 3; i++)
            loop.Add(Block(TraceBlocks.Point, "point" + i,
                ("Zeit", (i * 0.01).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("Werte", i == 2 && violating ? "1" : "0"), ("StabilesZiel", "Stable")));
        loop.Add(Block(BlockCatalog.SetValue, "flag", ("Ziel", "Observed"), ("Ausdruck", "Stable")));
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });
        var updates = new List<TraceUpdated>();
        run.EventRaised += e => { if (e is TraceUpdated trace) updates.Add(trace); };
        await run.RunAsync().Within();
        Assert.Equal(2, updates.Count);
        Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("point0")).Rating);
        Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("point1")).Rating);
        Assert.Equal(violating ? ResultRating.Fail : ResultRating.Pass, Assert.Single(run.Steps("point2")).Rating);
        Assert.Equal(true, Assert.Single(run.Steps("flag")).Value);
    }

}
