using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class SlopeSuppressionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(SlopeWindowMode.Consecutive)]
    [InlineData(SlopeWindowMode.Disjoint)]
    [InlineData(SlopeWindowMode.Rolling)]
    public void Stable_target_can_finish_when_all_channels_have_only_suppressed_slopes(SlopeWindowMode mode)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null), new("I", "A", null)], Now,
            minimumDuration: 3, minimumPoints: 4,
            slope: new(mode, 2, -100, 100) { PositiveThreshold = 10, NegativeThreshold = -10 },
            target: new TraceTargetBand(4, 6, 2));
        recordings.Add("owner", "trace", 0, [5, 1], Now);
        recordings.Add("owner", "trace", 1, [5, 1], Now);
        Assert.False(recordings.TargetReached("owner", "trace"));
        recordings.Add("owner", "trace", 2, [5, 1], Now);
        Assert.False(recordings.IsEnded("owner", "trace"));
        var preview = recordings.Add("owner", "trace", 3, [5, 1], Now)!;
        Assert.True(preview.TargetReached);
        Assert.True(recordings.IsEnded("owner", "trace"));
        var result = recordings.Stop("owner", "trace");
        Assert.True(result.TargetReached);
        Assert.True(result.Complete);
        Assert.True(result.Passed);
        Assert.All(result.Channels, channel =>
        {
            Assert.NotEmpty(channel.SuppressedSlopes);
            Assert.Empty(channel.Violations);
        });
    }

    [Fact]
    public void Stable_target_does_not_erase_an_earlier_slope_violation()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -10, 10) { PositiveThreshold = 1, NegativeThreshold = -1 },
            target: new TraceTargetBand(4, 6, 2));
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 0.1, [5], Now);
        recordings.Add("owner", "trace", 0.2, [5], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.True(result.TargetReached);
        Assert.True(result.Complete);
        Assert.False(result.Passed);
        Assert.Single(Assert.Single(result.Channels).Violations);
    }


    [Theory]
    [InlineData(SlopeWindowMode.Consecutive, 2)]
    [InlineData(SlopeWindowMode.Disjoint, 2)]
    [InlineData(SlopeWindowMode.Rolling, 2)]
    public void Only_the_first_matching_violations_are_exempt_and_the_next_one_stops(SlopeWindowMode mode, int points)
    {
        var settings = new SlopeLimits(mode, points, -10, 10)
        { InitialExceptionCount = 1, InitialExceptionAbove = 20 };
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, slope: settings, stopPercent: 10);
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 0.01, [0.5], Now);
        Assert.False(recordings.IsEnded("owner", "trace"));
        recordings.Add("owner", "trace", 0.02, [0.5], Now);
        var end = recordings.Add("owner", "trace", 0.03, [1], Now)!;
        Assert.True(recordings.IsEnded("owner", "trace"));
        Assert.NotNull(end.EarlyStop);
        var metadata = Assert.Single(end.Channels);
        var exempt = Assert.Single(metadata.SuppressedSlopes);
        Assert.Equal(50, exempt.Slope, 8);
        Assert.Equal("Anlaufausnahme 1 von 1", exempt.Reason);
        Assert.Equal(0.03, Assert.Single(metadata.Violations), 8);
        Assert.Equal(exempt, Assert.Single(EnvelopeTraceMetadata.Decode(end.Data.Channels[0].Quality)!.SuppressedSlopes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Threshold_keeps_the_initial_time_until_a_signed_slope_is_large_enough(int sign)
    {
        var settings = new SlopeLimits(SlopeWindowMode.Consecutive, 2, -100, 100)
        { PositiveThreshold = 10, NegativeThreshold = -10 };
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, slope: settings);
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 0.01, [sign * 0.05], Now);
        recordings.Add("owner", "trace", 0.02, [sign * 0.1], Now);
        recordings.Add("owner", "trace", 0.03, [sign * 0.6], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.True(result.Passed);
        var expected = new[] { sign * 5d, sign * 5d, sign * 50d };
        Assert.Equal(expected.Length, result.Data.Channels[0].Count);
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], result.Data.Channels[0].Values[index], 8);
        var details = Assert.Single(result.Channels);
        Assert.Equal(2, details.SuppressedSlopes.Count);
        Assert.All(details.SuppressedSlopes, point =>
        {
            Assert.Equal(0, point.StartTime);
            Assert.Equal("Mindeststeigung nicht erreicht", point.Reason);
        });
        Assert.Empty(details.Violations);
    }

    [Fact]
    public void Suppressed_only_data_is_incomplete_and_hard_limits_still_stop_every_sample()
    {
        var settings = new SlopeLimits(SlopeWindowMode.Consecutive, 2, -1000, 1000)
        { PositiveThreshold = 100, NegativeThreshold = -100 };
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null) { SafetyMaximum = 1 }], Now, slope: settings);
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 0.01, [0.5], Now);
        var stop = recordings.Add("owner", "trace", 0.02, [2], Now)!;
        Assert.True(stop.EarlyStop!.HardLimit);
        Assert.Single(Assert.Single(stop.Channels).SuppressedSlopes);
        Assert.False(stop.Passed);

        recordings.Start("owner", "small", [new("U", "V", null)], Now, slope: settings);
        recordings.Add("owner", "small", 0, [0], Now);
        recordings.Add("owner", "small", 0.01, [0.1], Now);
        Assert.False(recordings.Stop("owner", "small").Complete);
    }

    [Fact]
    public void Channels_have_independent_exception_counts_and_only_limit_violations_use_the_quota()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null), new("I", "A", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -10, 100) { InitialExceptionCount = 1, InitialExceptionAbove = 20 });
        recordings.Add("owner", "trace", 0, [0, 0], Now);
        recordings.Add("owner", "trace", 0.01, [0.5, 2], Now);
        recordings.Add("owner", "trace", 0.02, [2.5, 4], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.Empty(result.Channels[0].SuppressedSlopes);
        Assert.Single(result.Channels[0].Violations);
        Assert.Single(result.Channels[1].SuppressedSlopes);
        Assert.Single(result.Channels[1].Violations);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(20, 1)]
    [InlineData(-10, 0)]
    [InlineData(-20, 1)]
    public void Direction_thresholds_are_strict_and_remain_in_force_at_the_boundary(double slope, int evaluated)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -100, 100) { PositiveThreshold = 10, NegativeThreshold = -10 });
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 1, [slope], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.Equal(evaluated == 1, result.Complete);
        Assert.Equal(1 - evaluated, Assert.Single(result.Channels).SuppressedSlopes.Count);
    }

    [Theory]
    [InlineData("SteigungAnlaufAnzahl", "-1")]
    [InlineData("SteigungAnlaufAnzahl", "1.5")]
    [InlineData("SteigungSchwellePositiv", "-1")]
    [InlineData("SteigungSchwelleNegativ", "1")]
    [InlineData("SteigungAnlaufSchwelle", "NaN")]
    public async Task Invalid_filter_configuration_errors(string parameter, string value)
    {
        var step = BlockCatalog.Default.Create(TraceBlocks.Record, "record", parameters: new Dictionary<string, string>
        {
            [TraceBlocks.EvaluationParameter] = TraceBlocks.PairSlope,
            ["SteigungMin"] = "-100", ["SteigungMax"] = "100", ["Werte"] = "0", [parameter] = value
        });
        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { Clock = new VirtualClock() });
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Later_channel_overflow_keeps_all_prior_samples_and_filter_state_consistent(bool closeOwner)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null), new("I", "A", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -10, 10) { InitialExceptionCount = 1, InitialExceptionAbove = 20 });
        recordings.Add("owner", "trace", 0, [0, -1e308], Now);
        recordings.Add("owner", "trace", 1, [0, -1e308], Now);
        Assert.Throws<ArgumentException>(() => recordings.Add("owner", "trace", 2, [100, 1e308], Now));

        var result = closeOwner ? Assert.Single(recordings.CloseOwner("owner")) : recordings.Stop("owner", "trace");
        Assert.False(result.Complete);
        Assert.All(result.Data.Channels, wave =>
        {
            Assert.Single(wave.Values);
            Assert.Single(wave.Times!);
        });
        Assert.All(result.Channels, channel =>
        {
            Assert.Empty(channel.SuppressedSlopes);
            Assert.Empty(channel.Violations);
        });
        var plot = new Testumgebung.Presentation.ViewModels.WaveformPlotViewModel();
        plot.SetData(result.Data);
        Assert.NotEmpty(plot.Channels);
    }

    [Theory]
    [InlineData(SlopeExceptionDirection.Falling, -50, true)]
    [InlineData(SlopeExceptionDirection.Falling, 50, false)]
    [InlineData(SlopeExceptionDirection.Rising, 50, true)]
    [InlineData(SlopeExceptionDirection.Rising, -50, false)]
    [InlineData(SlopeExceptionDirection.Both, -50, true)]
    [InlineData(SlopeExceptionDirection.Both, 50, true)]
    public void Initial_exception_direction_is_explicit(SlopeExceptionDirection direction, double slope, bool excluded)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -2, 2)
            { InitialExceptionCount = 1, InitialExceptionAbove = 10, ExceptionDirection = direction });
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 1, [slope], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.Equal(excluded ? 1 : 0, result.Channels[0].SuppressedSlopes.Count);
        Assert.Equal(excluded ? 0 : 1, result.Channels[0].ViolationCount);
    }

    [Fact]
    public void Legacy_slope_parameters_load_with_rising_direction()
    {
        var step = BlockCatalog.Default.Create(TraceBlocks.Start, "begin");
        step.Parameters.Remove("SteigungAnlaufrichtung");
        step.Parameters["SteigungAnlaufSchwelle"] = "-3";
        var copy = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(step))));
        var loaded = Assert.IsType<StepDefinition>(copy.Definition.Find("begin"));
        Assert.Equal("Steigend", loaded.Parameters["SteigungAnlaufrichtung"]);
        Assert.Equal("-3", loaded.Parameters["SteigungAnlaufSchwelle"]);
    }

    [Theory]
    [InlineData(-2, 5, -1, 10, true, false)]
    [InlineData(1, 5, -0.5, 0.5, true, false)]
    [InlineData(-2, 2, -1, 1, true, true)]
    [InlineData(-2, 2, -2, 2, true, true)]
    [InlineData(-2, 2, -2, 2, false, false)]
    [InlineData(-2, 2, -1, 1, false, true)]
    public void Invalid_threshold_regions_load_but_are_diagnosed(double lower, double upper,
        double negative, double positive, bool touch, bool valid)
    {
        static string N(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var step = BlockCatalog.Default.Create(TraceBlocks.Start, "begin", parameters: new Dictionary<string, string>
        {
            [TraceBlocks.EvaluationParameter] = TraceBlocks.PairSlope,
            ["SteigungMin"] = N(lower), ["SteigungMax"] = N(upper),
            ["SteigungSchwelleNegativ"] = N(negative), ["SteigungSchwellePositiv"] = N(positive),
            ["Beruehren"] = touch.ToString()
        });
        var serializer = new ProjectSerializer();
        var copy = serializer.Clone(new TestProject(Test(Group("loop").Add(step))));
        var loaded = Assert.IsType<StepDefinition>(copy.Definition.Find("begin"));
        Assert.Equal(valid, BlockCatalog.Default.Rebuild(loaded).Count == 0);
        if (!valid)
        {
            var error = Assert.IsType<ErrorAction>(loaded.Action);
            Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(500)]
    public void A_jump_after_any_plateau_is_evaluated_at_the_current_window(int plateauPoints)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(SlopeWindowMode.Consecutive, 2, -2, 2) { PositiveThreshold = 1, NegativeThreshold = -1 });
        for (var i = 0; i <= plateauPoints; i++)
            recordings.Add("owner", "trace", i * 0.01, [12], Now);
        recordings.Add("owner", "trace", (plateauPoints + 1) * 0.01, [13], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Passed);
        Assert.True(result.Complete);
        Assert.Equal(100, result.Data.Channels[0].Values.Last(), 8);
        Assert.Single(result.Channels[0].Violations);
        Assert.Equal(plateauPoints, result.Channels[0].SuppressedSlopes.Count);
    }

    [Theory]
    [InlineData(SlopeExceptionDirection.Rising, 1)]
    [InlineData(SlopeExceptionDirection.Falling, -1)]
    [InlineData(SlopeExceptionDirection.Both, 1)]
    [InlineData(SlopeExceptionDirection.Both, -1)]
    public void Unused_initial_exceptions_cannot_hide_a_later_spike(SlopeExceptionDirection direction, int sign)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, maxGap: 4,
            slope: new(SlopeWindowMode.Consecutive, 2, -2, 2)
            { InitialExceptionCount = 3, InitialExceptionAbove = 10, ExceptionDirection = direction });
        recordings.Add("owner", "trace", 0, [0], Now);
        recordings.Add("owner", "trace", 1, [sign], Now);
        recordings.Add("owner", "trace", 4, [sign], Now);
        recordings.Add("owner", "trace", 4.01, [2 * sign], Now);
        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Passed);
        Assert.Empty(result.Channels[0].SuppressedSlopes);
        Assert.Equal(4.01, Assert.Single(result.Channels[0].Violations));
    }

    [Fact]
    public async Task Standard_blocks_serialize_configuration_and_write_suppression_details()
    {
        var parameters = new Dictionary<string, string>
        {
            [TraceBlocks.EvaluationParameter] = TraceBlocks.PairSlope,
            ["SteigungMin"] = "-10", ["SteigungMax"] = "10", ["SteigungAnlaufAnzahl"] = "1",
            ["SteigungAnlaufSchwelle"] = "20", ["ErgebnisZiel"] = "Details"
        };
        var capture = BlockCatalog.Default.Create(TraceBlocks.Start, "begin", parameters: parameters);
        var loop = Group("loop").Var("Details", VarType.String).Add(capture,
            BlockCatalog.Default.Create(TraceBlocks.Point, "one", parameters: new Dictionary<string, string> { ["Zeit"] = "0", ["Werte"] = "0" }),
            BlockCatalog.Default.Create(TraceBlocks.Point, "two", parameters: new Dictionary<string, string> { ["Zeit"] = "0.01", ["Werte"] = "0.5" }),
            BlockCatalog.Default.Create(TraceBlocks.Point, "three", parameters: new Dictionary<string, string> { ["Zeit"] = "0.02", ["Werte"] = "0.5" }),
            BlockCatalog.Default.Create(TraceBlocks.Stop, "stop", parameters: new Dictionary<string, string> { ["ErgebnisZiel"] = "Details" }));
        var project = new ProjectSerializer().Clone(new TestProject(Test(loop)));
        var run = new TestRun(project.Definition);
        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        var wave = Assert.IsType<WaveformSet>(Assert.Single(run.Steps("stop")).Value);
        Assert.Contains("Anlaufausnahme", wave.Channels[0].Quality);
        Assert.Contains("1 Steigung(en)", Assert.Single(run.Steps("stop")).Message);
    }
}
