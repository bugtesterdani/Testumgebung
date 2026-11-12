using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class EnvelopeTraceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static CurveDefinition Band(double[]? times = null, double[]? lower = null, double[]? upper = null, string unit = "V") => new()
    {
        Id = "band", Name = "Grenzband", Version = "1", SampleRate = 1, Times = times ?? [0, 1, 2],
        Channels = [new("Untergrenze", unit, lower ?? [0, 0, 0]), new("Obergrenze", unit, upper ?? [10, 10, 10])],
    };
    private static EnvelopeRecordings Start(CurveDefinition? band = null, bool touch = true, double gap = 2, double duration = 0, int max = 100)
    {
        var recordings = new EnvelopeRecordings(); recordings.Start("owner", "trace", [new("U", "V", band ?? Band())], Now, max, gap, duration, touch); return recordings;
    }
    [Theory]
    [InlineData(5, true, true)] [InlineData(10, true, true)] [InlineData(10, false, false)] [InlineData(11, true, false)] [InlineData(-1, true, false)]
    public void Trace_checks_the_complete_linear_curve_and_records_limits(double value, bool touch, bool pass)
    {
        var recordings = Start(touch: touch); recordings.Add("owner", "trace", 0, [value], Now);
        Assert.NotNull(recordings.Add("owner", "trace", 2, [value], Now.AddSeconds(2)));
        var result = recordings.Stop("owner", "trace"); Assert.Equal(pass, result.Passed); Assert.True(result.Complete);
        Assert.Equal(3, result.Data.Channels.Count); Assert.Equal(3, result.Data.Channels[0].Count);
        var info = EnvelopeTraceMetadata.Decode(result.Data.Channels[0].Quality)!;
        Assert.Equal("band@1", info.Curve); Assert.Equal(Band().ContentHash(), info.Hash); Assert.Equal(new double[] { 0, 2 }, info.OriginalTimes);
        Assert.Equal(pass ? 0 : 3, info.ViolationCount);
        var plot = new WaveformPlotViewModel(); plot.SetData(result.Data);
        Assert.Equal(pass ? 0 : 3, plot.ViolationMarkers.Count); plot.Clear(); Assert.Empty(plot.ViolationMarkers);
    }
    [Fact]
    public void Pausing_excludes_wall_time_and_keeps_prior_points_and_violations()
    {
        var recordings = Start(gap: 1.1);
        recordings.SetPaused("owner", "trace", false, Now);
        recordings.Add("owner", "trace", null, [11], Now);
        recordings.SetPaused("owner", "trace", true, Now.AddSeconds(0.5));
        recordings.SetPaused("owner", "trace", true, Now.AddSeconds(2));
        Assert.True(recordings.IsPaused("owner", "trace"));
        Assert.Null(recordings.Add("owner", "trace", 99, [5], Now.AddSeconds(4)));
        recordings.SetPaused("owner", "trace", false, Now.AddSeconds(10.5));
        Assert.False(recordings.IsPaused("owner", "trace"));
        recordings.Add("owner", "trace", null, [5], Now.AddSeconds(11));

        var result = recordings.Stop("owner", "trace");
        Assert.True(result.Complete);
        Assert.False(result.Passed);
        Assert.Equal(new double[] { 0, 1 }, result.Channels[0].OriginalTimes);
    }

    [Fact]
    public async Task Pause_block_skips_reads_and_resumes_the_same_recording()
    {
        var clock = new VirtualClock();
        var loop = Group("loop").Add(
            Block(TraceBlocks.Start, "start", ("Grenzkurven", "band@1"), ("MaxLuecke", "1.1")),
            Block(TraceBlocks.Point, "first", ("Werte", "5")),
            Block(TraceBlocks.PauseTime, "pause", ("Aktion", "Pausieren")),
            Block(BlockCatalog.Wait, "switch", ("Dauer", "3000")),
            Block(TraceBlocks.Point, "skip", ("Werte", "undefined")),
            Block(TraceBlocks.PauseTime, "resume", ("Aktion", "Fortsetzen")),
            Block(BlockCatalog.Wait, "settle", ("Dauer", "1000")),
            Block(TraceBlocks.Point, "second", ("Werte", "7")),
            Block(TraceBlocks.Stop, "stop"));
        var definition = Test(loop);
        definition.Curves.Add(Band());
        var run = new TestRun(definition, new TestRunOptions { Clock = clock });

        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        Assert.Equal(ResultRating.NResult, run.Steps("skip").Single().Rating);
        var data = Assert.IsType<WaveformSet>(run.Steps("stop").Single().Value);
        Assert.Equal(new double[] { 0, 1 }, EnvelopeTraceMetadata.Decode(data.Channels[0].Quality)!.OriginalTimes);
        Assert.Equal(new double[] { 5, 7 }, data.Channels[0].Values);
    }

    [Fact]
    public void Single_sample_series_are_visible_without_trace_metadata()
    {
        var plot = new WaveformPlotViewModel();
        plot.SetData([new PlotSource("Spannung", "V", [0]), new PlotSource("Obergrenze", "V", [0.5])], 1, [0]);
        Assert.Equal(2, plot.SampleMarkers.Count);
        Assert.All(plot.SampleMarkers, marker => Assert.True(marker.X > 0));
        plot.Channels[0].IsVisible = false;
        Assert.Equal(1, Assert.Single(plot.SampleMarkers).ColorIndex);
    }

    [Fact]
    public void A_time_dependent_abort_curve_can_trip_between_points_inside_the_normal_band()
    {
        var abort = Band(upper: [10, 4, 10]);
        var recording = new EnvelopeRecordings();
        recording.Start("owner", "trace", [new("U", "V", Band()) { AbortLimits = abort }], Now, maxGap: 2);
        recording.Add("owner", "trace", 0, [5], Now);
        var result = recording.Add("owner", "trace", 2, [5], Now.AddSeconds(2))!;

        Assert.Equal(1, result.EarlyStop!.Time);
        Assert.Equal(4, result.EarlyStop.Upper);
        Assert.Empty(result.Channels[0].Violations);
        Assert.False(result.Passed);
        Assert.True(recording.HasViolation("owner", "trace"));
    }

    [Fact]
    public void A_violation_between_measured_points_is_detected_at_the_limit_knot()
    {
        var r = Start(Band(upper: [10, 1, 10])); r.Add("owner", "trace", 0, [5], Now); r.Add("owner", "trace", 2, [5], Now.AddSeconds(2));
        var result = r.Stop("owner", "trace"); Assert.False(result.Passed); Assert.Equal(new double[] { 1 }, result.Channels[0].Violations); Assert.Equal(4, result.Channels[0].MaximumDeviation);
    }
    [Fact]
    public void Multiple_channels_are_independent_and_limits_are_frozen()
    {
        var band = Band(); var r = new EnvelopeRecordings();
        r.Start("owner", "trace", [new("U", "V", band), new("I", "A", Band(unit: "A"))], Now, maxGap: 2);
        band.Channels[1].Samples[1] = -99;
        r.Add("owner", "trace", 0, [5, 11], Now); r.Add("owner", "trace", 2, [5, 11], Now.AddSeconds(2));
        var result = r.Stop("owner", "trace"); Assert.Equal(6, result.Data.Channels.Count); Assert.Equal(0, result.Channels[0].ViolationCount); Assert.Equal(3, result.Channels[1].ViolationCount);
        Assert.Throws<InvalidOperationException>(() => r.Stop("other", "trace"));
    }
    [Theory]
    [InlineData(-1)] [InlineData(3)] [InlineData(double.NaN)]
    public void Invalid_times_poison_a_capture_and_do_not_extrapolate(double time)
    {
        var r = Start(); r.Add("owner", "trace", 0, [5], Now);
        Assert.Throws<ArgumentException>(() => r.Add("owner", "trace", time, [5], Now)); Assert.False(r.Stop("owner", "trace").Passed);
    }
    [Fact]
    public void Incomplete_gap_overflow_duplicates_and_abort_cannot_pass()
    {
        var r = Start(); Assert.False(r.Stop("owner", "trace").Complete);
        r = Start(); r.Add("owner", "trace", null, [5], Now); Assert.False(r.Stop("owner", "trace").Complete);
        r = Start(gap: 0.5); r.Add("owner", "trace", 0, [5], Now); Assert.Throws<ArgumentException>(() => r.Add("owner", "trace", 1, [5], Now.AddSeconds(1))); Assert.False(r.Stop("owner", "trace").Passed);
        r = Start(max: 2); r.Add("owner", "trace", 0, [5], Now); r.Add("owner", "trace", 1, [5], Now.AddSeconds(1)); Assert.Throws<InvalidOperationException>(() => r.Add("owner", "trace", 2, [5], Now.AddSeconds(2))); Assert.False(r.Stop("owner", "trace").Passed);
        r = Start(duration: 2); r.Add("owner", "trace", 0, [5], Now); r.Add("owner", "trace", 1, [5], Now.AddSeconds(1)); Assert.False(r.Stop("owner", "trace").Passed);
        r = Start(); r.Add("owner", "trace", 0, [5], Now); r.Add("owner", "trace", 1, [5], Now.AddSeconds(1)); Assert.False(r.Stop("owner", "trace", false).Complete);
        r = Start(); Assert.Single(r.CloseOwner("owner")); Assert.Empty(r.CloseOwner("owner"));
    }
    [Fact]
    public void Limits_and_recording_configuration_are_validated()
    {
        Assert.Throws<ArgumentException>(() => Start(Band(lower: [1, 11, 1])));
        Assert.Throws<ArgumentException>(() => Start(Band(times: [0, 0, 1])));
        Assert.Throws<ArgumentException>(() => Start(Band(unit: "A")));
        Assert.Throws<ArgumentException>(() => Start(Band(upper: [10, double.NaN, 10])));
        Assert.Throws<ArgumentException>(() => Start(gap: 0)); Assert.Throws<ArgumentException>(() => Start(max: 1));
        Assert.Null(EnvelopeTraceMetadata.Decode("ordinary quality")); Assert.Null(EnvelopeTraceMetadata.Decode("Grenzband:not-json"));
    }
    [Fact]
    public void Decimal_sampling_roundoff_is_not_a_gap_or_duration_failure()
    {
        var r = Start(gap: 0.1, duration: 0.2);
        r.Add("owner", "trace", 0.3, [5], Now); r.Add("owner", "trace", 0.4, [5], Now.AddSeconds(1)); r.Add("owner", "trace", 0.5, [5], Now.AddSeconds(2));
        Assert.True(r.Stop("owner", "trace").Passed);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Operator_abort_stores_partial_dut_data_even_when_an_exit_evaluation_runs(bool evaluateInExit)
    {
        TestRun? run = null;
        var dut = Group("dut").Var("Kennung", VarType.String, "SN-ABORT"); dut.IsDutRun = true;
        dut.Add(Block(TraceBlocks.Start, "begin", ("Grenzkurven", "band@1")));
        dut.Add(Block(TraceBlocks.Point, "first", ("Werte", "5"), ("Zeit", "0")));
        dut.Add(Block(TraceBlocks.Point, "second", ("Werte", "5"), ("Zeit", "1")));
        dut.Add(Steps.Code("abort", "Abbruch", _ => { run!.RequestAbort(); return StepOutcome.Unrated(); }));
        if (evaluateInExit) dut.ExitGroup = Group("exit").Add(Block(TraceBlocks.Stop, "exit-evaluate"));
        var definition = Test(Group("loop").Add(dut)); definition.Curves.Add(Band());
        run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock() });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? result = null; recorder.RunRecorded += r => result = r;
        var status = await run.RunAsync().Within(); Assert.True(status.OperatorAbort); Assert.NotNull(result); Assert.True(result.OperatorAbort);
        Assert.Equal(3, result.Waveforms.Count);
        Assert.False(EnvelopeTraceMetadata.Decode(result.Waveforms[0].Data.Quality)!.Complete);
        Assert.NotEqual(ResultRating.Pass, result.Rating);
    }
    [Fact]
    public void Live_plot_follows_new_points_and_preserves_a_manual_zoom()
    {
        var recordings = Start(Band(times: [0, 10, 20])); var plot = new WaveformPlotViewModel();
        recordings.Add("owner", "trace", 0, [5], Now);
        for (var i = 1; i <= 10; i++) plot.SetData(recordings.Add("owner", "trace", i, [5], Now.AddSeconds(i))!.Data);
        Assert.Equal(10, plot.ViewEnd); Assert.Equal(10, plot.Duration);
        plot.SetView(2, 6); plot.SetData(recordings.Add("owner", "trace", 11, [5], Now.AddSeconds(11))!.Data);
        Assert.Equal(2, plot.ViewStart); Assert.Equal(6, plot.ViewEnd);
        var partial = Start(); partial.Add("owner", "trace", 0, [5], Now); plot.SetData(partial.Stop("owner", "trace").Data);
        Assert.All(plot.Series.SelectMany(series => series.Points), point => { Assert.True(double.IsFinite(point.X)); Assert.True(double.IsFinite(point.Y)); });
    }
    [Fact]
    public void Curve_library_shows_explicit_latest_and_missing_trace_references()
    {
        var definition = Test(Group("loop").Add(Block(TraceBlocks.Start, "used", ("Grenzkurven", "band@1")))); definition.Curves.Add(Band());
        definition.Init.Add(Block(TraceBlocks.Start, "latest", ("Grenzkurven", "band")), Block(TraceBlocks.Start, "missing", ("Grenzkurven", "missing@1")));
        var editor = new EditorViewModel(new TestProject(definition));
        var curves = new CurvesViewModel(editor, null, () => "fixture", () => null, Testumgebung.Presentation.Mvvm.ImmediateDispatcher.Instance);
        Assert.Contains("used", Assert.Single(curves.Curves).Usage); Assert.Contains("latest", curves.Curves[0].Usage);
    }
    [Theory]
    [InlineData(3.6, false)] [InlineData(6.6, false)]
    [InlineData(3.59, true)] [InlineData(6.61, true)]
    public void Early_stop_uses_each_absolute_limit_and_strictly_exceeds_the_percentage(double value, bool stops)
    {
        var r = new EnvelopeRecordings();
        r.Start("owner", "trace", [new("U", "V", Band(lower: [4, 4, 4], upper: [6, 6, 6]))], Now, stopPercent: 10);
        var preview = r.Add("owner", "trace", 0, [value], Now);
        Assert.Equal(stops, preview?.EarlyStop is not null);
        var result = r.Stop("owner", "trace"); Assert.False(result.Passed);
        Assert.Equal(stops, result.EarlyStop is not null);
        if (stops)
        {
            Assert.False(result.Complete); Assert.Single(result.Channels[0].OriginalTimes);
            Assert.Equal(10, EnvelopeTraceMetadata.Decode(result.Data.Channels[0].Quality)!.EarlyStop!.Percent);
        }
    }
    [Theory]
    [InlineData(-6.61, -6, -4)] [InlineData(-3.59, -6, -4)] [InlineData(-0.01, 0, 6)] [InlineData(-0.01, 0, 1e12)]
    public void Negative_and_zero_limits_use_absolute_percentage_without_reversing_the_margin(double value, double low, double high)
    {
        var r = new EnvelopeRecordings();
        r.Start("owner", "trace", [new("U", "V", Band(lower: [low, low, low], upper: [high, high, high]))], Now, stopPercent: 10);
        Assert.NotNull(r.Add("owner", "trace", 0, [value], Now)!.EarlyStop);
        Assert.Throws<InvalidOperationException>(() => r.Add("owner", "trace", 1, [5], Now));
        Assert.Empty(r.CloseOwner("owner"));
    }
    [Fact]
    public void Early_stop_is_optional_validated_and_checks_interpolated_multichannel_limits_without_preview_throttle()
    {
        var r = Start(); r.Add("owner", "trace", 0, [99], Now); Assert.Null(r.Stop("owner", "trace").EarlyStop);
        foreach (var percent in new[] { -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => r.Start("owner", "bad", [new("U", "V", Band())], Now, stopPercent: percent));
        r.Start("owner", "trace", [new("U", "V", Band()), new("I", "A", Band(upper: [10, 1, 10], unit: "A"))], Now, maxGap: 2, minimumDuration: 10, stopPercent: 10);
        r.Add("owner", "trace", 0, [5, 5], Now);
        var result = r.Add("owner", "trace", 2, [5, 5], Now)!;
        Assert.Equal("I", result.EarlyStop!.Channel); Assert.Equal(1, result.EarlyStop.Time);
        Assert.False(result.Passed); Assert.False(result.Complete);
        r.Start("owner", "zero", [new("U", "V", Band(lower: [-6, 6, 6]))], Now, stopPercent: 200);
        Assert.Null(r.Add("owner", "zero", 0, [-1], Now)!.EarlyStop);
        var zero = r.Add("owner", "zero", 1, [-1], Now)!;
        Assert.Equal(0.5, zero.EarlyStop!.Time);
        Assert.Contains(0.5, zero.Channels[0].Violations);
        // Once a preview was emitted, a third point must still trip inside its 250 ms throttle window.
        r.Start("owner", "throttle", [new("U", "V", Band())], Now, stopPercent: 0);
        r.Add("owner", "throttle", 0, [5], Now); r.Add("owner", "throttle", 0.1, [5], Now);
        Assert.NotNull(r.Add("owner", "throttle", 0.2, [11], Now)!.EarlyStop);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Early_fail_ends_only_its_recording_preserves_data_and_continues_following_steps(bool parallel)
    {
        var nextRan = false; var siblingRan = false;
        var dut = Group("dut").Var("Kennung", VarType.String, "SN-EARLY"); dut.IsDutRun = true;
        dut.Add(Block(TraceBlocks.Start, "begin", ("Grenzkurven", "band@1"), ("FailAbbruch", "true"), ("AbweichungProzent", "10")));
        dut.Add(Block(TraceBlocks.Point, "initial", ("Werte", "5"), ("Zeit", "0")));
        var points = Group("points"); points.Parallel = parallel;
        points.Add(Block(TraceBlocks.Point, "trip", ("Werte", "11.1"), ("Zeit", "0.5")),
            Steps.Code("sibling", "Sibling continues", _ => { siblingRan = true; return StepOutcome.Pass(); }));
        dut.Add(points, Block(TraceBlocks.Point, "ignored", ("Werte", "missingVariable"), ("Zeit", "1")),
            Steps.Code("next", "Continue", _ => { nextRan = true; return StepOutcome.Pass(); }));
        dut.ExitGroup = Group("cleanup").Add(Block(TraceBlocks.Stop, "evaluate-again"));
        var definition = Test(Group("loop").Add(dut)); definition.Curves.Add(Band());
        var run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock() });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? saved = null; recorder.RunRecorded += value => saved = value;
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Fail, status.Rating); Assert.False(status.OperatorAbort);
        Assert.True(nextRan); Assert.True(siblingRan);
        Assert.NotNull(saved); Assert.Equal(ResultRating.Fail, saved.Rating); Assert.False(saved.OperatorAbort);
        Assert.Equal(3, saved.Waveforms.Count);
        var reopened = Testumgebung.Persistence.Results.ResultFile.Read(Testumgebung.Persistence.Results.ResultFile.ToBytes(saved));
        Assert.Empty(reopened.Problems);
        Assert.Equal(10, EnvelopeTraceMetadata.Decode(reopened.Run.Waveforms[0].Data.Quality)!.EarlyStop!.Percent);
    }
    [Fact]
    public void Live_markers_show_original_irregular_measurements_starting_with_the_first_point()
    {
        var recording = Start(); var plot = new WaveformPlotViewModel();
        plot.SetData(recording.Add("owner", "trace", 0, [5], Now)!.Data);
        var first = Assert.Single(plot.SampleMarkers); Assert.Equal(0, first.X); Assert.True(double.IsFinite(first.Y));
        plot.SetData(recording.Add("owner", "trace", 0.5, [7], Now.AddSeconds(1))!.Data);
        plot.SetData(recording.Add("owner", "trace", 2, [5], Now.AddSeconds(2))!.Data);
        Assert.Equal(new double[] { 0, 250, 1000 }, plot.SampleMarkers.Select(p => p.X));
        Assert.Equal(4, plot.Count); // Interpolated boundary knot at t=1 has no measured-point dot.
        plot.Channels[0].IsVisible = false; Assert.Empty(plot.SampleMarkers);
        plot.Channels[0].IsVisible = true; Assert.Equal(3, plot.SampleMarkers.Count);
        plot.Clear(); Assert.Empty(plot.SampleMarkers);
        plot.SetData([new PlotSource("regular", "V", new double[] { 1, 2 })], 1, null); Assert.Empty(plot.SampleMarkers);
    }
    [Fact]
    public void Dense_marker_display_is_bounded_but_keeps_first_and_latest_samples()
    {
        var recording = Start(max: 3000); var plot = new WaveformPlotViewModel();
        for (var i = 0; i <= 2500; i++) recording.Add("owner", "trace", i / 1250d, [5], Now);
        plot.SetData(recording.Stop("owner", "trace").Data);
        Assert.Equal(1000, plot.SampleMarkers.Count); Assert.Equal(0, plot.SampleMarkers[0].X); Assert.Equal(1000, plot.SampleMarkers[^1].X);
    }
    [Fact]
    public async Task Two_station_displays_update_independently_while_their_recordings_are_still_running()
    {
        var loop = Group("loop").Add(Block(TraceBlocks.Start, "start", ("Grenzkurven", "band@1")),
            Block(TraceBlocks.Point, "first", ("Werte", "5"), ("Zeit", "0")),
            Block(BlockCatalog.Operator, "wait1", ("Art", "Anweisung"), ("Text", "Weiter"), ("Warten", "true")),
            Block(TraceBlocks.Point, "second", ("Werte", "7"), ("Zeit", "0.5")),
            Block(BlockCatalog.Operator, "wait2", ("Art", "Anweisung"), ("Text", "Fertig"), ("Warten", "true")),
            Block(TraceBlocks.Stop, "stop"));
        var definition = Test(loop); definition.Curves.Add(Band()); var project = new TestProject(definition);
        var ui = new QueueDispatcher();
        var first = new StationViewModel("A", () => project, new Testumgebung.Hardware.ResourceManager(), ui);
        var second = new StationViewModel("B", () => project, new Testumgebung.Hardware.ResourceManager(), ui);
        var run1 = first.StartSimulationAsync(); var run2 = second.StartSimulationAsync();
        try
        {
            await ui.Until(() => first.ActiveContent is { IsAwaiting: true } && second.ActiveContent is { IsAwaiting: true }, "live recordings paused for operator");
            Assert.True(first.HasLiveWaveform); Assert.True(second.HasLiveWaveform);
            Assert.Single(first.LiveWaveform.SampleMarkers); Assert.Single(second.LiveWaveform.SampleMarkers);
            Assert.Contains("Aufnahme läuft", first.LiveWaveformTitle); Assert.False(run1.IsCompleted);
            first.ActiveContent!.ConfirmCommand.Execute(null);
            await ui.Until(() => first.LiveWaveform.SampleMarkers.Count == 2 && first.ActiveContent is { IsAwaiting: true }, "second live point");
            Assert.Single(second.LiveWaveform.SampleMarkers); Assert.False(run2.IsCompleted);
            first.ActiveContent!.ConfirmCommand.Execute(null);
            await ui.Until(() => run1.IsCompleted, "first recording completed"); await run1;
            Assert.Equal(ResultRating.Pass, first.LastStatus!.Value.Rating);
        }
        finally
        {
            second.RequestAbort(); first.RequestAbort();
            await ui.Until(() => run1.IsCompleted && run2.IsCompleted, "recordings stopped");
            await Task.WhenAll(run1, run2);
        }
    }
    private sealed class Meter(string unit = "V") : IHardwareAdapter
    {
        public string Name => "Fixture";
        public ChannelCapabilities GetCapabilities(string channel) => new(unit, -100, 100);
        public Task<double> ReadAsync(string channel, CancellationToken token) => Task.FromResult(5d);
        public Task WriteAsync(string channel, double value, CancellationToken token) => Task.CompletedTask;
    }
    [Fact]
    public async Task Hardware_trace_survives_result_file_and_release_roundtrip()
    {
        var dut = Group("dut").Var("Kennung", VarType.String, "SN-75").Var("Details", VarType.String); dut.IsDutRun = true;
        dut.Add(Block(TraceBlocks.Start, "begin", ("Grenzkurven", "band@1"), ("MaxLuecke", "2")));
        dut.Add(Block(TraceBlocks.Point, "first", ("Quelle", "Geräte"), ("Ressourcen", "U"), ("Zeit", "0")));
        dut.Add(Block(TraceBlocks.Point, "second", ("Quelle", "Geräte"), ("Ressourcen", "U"), ("Zeit", "2")));
        dut.Add(Block(TraceBlocks.Stop, "end", ("ErgebnisZiel", "Details")));
        var definition = Test(Group("loop").Add(dut)); definition.Curves.Add(Band(upper: [10, 1, 10]));
        var project = new TestProject(definition);
        var package = Testumgebung.Persistence.Releases.ReleasePackage.Build(project, "fixture", Now);
        Assert.True(Testumgebung.Persistence.Releases.ReleasePackage.Open(package.Bytes).IsIntact);
        var run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = new Testumgebung.Hardware.HardwareRegistry().Bind("U", new Meter(), "u") });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? recorded = null; recorder.RunRecorded += value => recorded = value;
        Assert.Equal(ResultRating.Fail, (await run.RunAsync().Within()).Rating);
        Assert.NotNull(recorded); Assert.Equal("SN-75", recorded.DutId); Assert.Equal(3, recorded.Waveforms.Count);
        var saved = Testumgebung.Persistence.Results.ResultFile.Read(Testumgebung.Persistence.Results.ResultFile.ToBytes(recorded));
        Assert.Empty(saved.Problems); var measurement = saved.Run.Waveforms.Single(w => w.Data.Channel == "Spannung").Data;
        var metadata = EnvelopeTraceMetadata.Decode(measurement.Quality)!; Assert.Equal(new double[] { 1 }, metadata.Violations);
        Assert.Equal(new double[] { 0, 2 }, metadata.OriginalTimes);
        var plot = new WaveformPlotViewModel(); plot.SetData(new(saved.Run.Waveforms.Select(w => w.Data).ToArray())); Assert.Single(plot.ViolationMarkers);
        plot.Channels.First(c => c.Id == "Spannung").IsVisible = false; Assert.Empty(plot.ViolationMarkers);
        var wrong = new TestRun(definition, new TestRunOptions { Hardware = new Testumgebung.Hardware.HardwareRegistry().Bind("U", new Meter("A"), "u") });
        Assert.Equal(ResultRating.Error, (await wrong.RunAsync().Within()).Rating);
    }
    [Fact]
    public void Boundary_changes_used_in_init_prevent_mid_session_version_switch()
    {
        var dut = Group("dut").Var("Kennung", VarType.String); dut.IsDutRun = true;
        var definition = Test(Group("loop").Add(dut), Group("init").Add(Block(TraceBlocks.Start, "begin", ("Grenzkurven", "band@1")))); definition.Curves.Add(Band());
        var serializer = new ProjectSerializer(); var original = new TestProject(definition); var changed = serializer.Clone(original);
        changed.Definition.Curves.Find("band", "1")!.Channels[1].Samples[1] = 9;
        Assert.False(Testumgebung.Persistence.Releases.DefinitionCompatibility.CanSwapAtDut(original, changed, out var reason)); Assert.Contains("Kurve", reason);
    }
    private static StepDefinition Block(string type, string id, params (string, string)[] values) => BlockCatalog.Default.Create(type, id, id, values.ToDictionary(v => v.Item1, v => v.Item2));
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Blocks_capture_variables_and_unfinished_recordings_refuse_pass(bool stop)
    {
        var loop = Group("loop").Add(Block(TraceBlocks.Start, "begin", ("Grenzkurven", "band@1")));
        loop.Add(Block(TraceBlocks.Point, "first", ("Werte", "5"), ("Zeit", "0")));
        loop.Add(Block(TraceBlocks.Point, "second", ("Werte", "5"), ("Zeit", "1")));
        if (stop) loop.Add(Block(TraceBlocks.Stop, "end"));
        var definition = Test(loop); definition.Curves.Add(Band());
        var serialized = new ProjectSerializer().Serialize(new TestProject(definition)); var project = new ProjectSerializer().Deserialize(serialized);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock() });
        var status = await run.RunAsync().Within(); Assert.Equal(stop ? ResultRating.Pass : ResultRating.Error, status.Rating);
        Assert.Contains(run.Events.OfType<StepCompleted>(), e => e.Value is WaveformSet);
        Assert.DoesNotContain(run.Events, e => e is TraceUpdated);
    }
}