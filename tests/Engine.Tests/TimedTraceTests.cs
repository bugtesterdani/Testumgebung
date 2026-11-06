using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class TimedTraceTests
{
    private static CurveDefinition Band(double start = 0, double end = 2) => new()
    {
        Id = "band", Name = "Grenzband", Version = "1", SampleRate = 1, Times = [start, end],
        Channels = [new("Untergrenze", "V", [4, 4]), new("Obergrenze", "V", [6, 6])],
    };
    private static StepDefinition Block(string type, string id, params (string, string)[] parameters)
        => BlockCatalog.Default.Create(type, id, id, parameters.ToDictionary(pair => pair.Item1, pair => pair.Item2));
    private static StepDefinition Capture(params (string, string)[] parameters)
    {
        var step = Block(TraceBlocks.Record, "capture", ("Grenzkurven", "band@1"), ("Dauer", "2"), ("Intervall", "0.5"), ("Werte", "5"), ("FailAbbruch", "true"));
        foreach (var (key, value) in parameters) step.Parameters[key] = value;
        BlockCatalog.Default.Rebuild(step); return step;
    }
    [Theory]
    [InlineData(3.999, false)] [InlineData(3.599, true)]
    [InlineData(6.001, false)] [InlineData(6.601, true)]
    [InlineData(3.6, false)] [InlineData(6.6, false)]
    public async Task Normal_limits_always_fail_but_extra_percentage_only_shortens_the_capture(double value, bool early)
    {
        var followingRan = false; var clock = new VirtualClock(); var started = clock.Now;
        var capture = Capture(("Werte", value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var dut = Group("dut").Var("Kennung", VarType.String, "SN-DURATION"); dut.IsDutRun = true;
        dut.Add(capture, Steps.Code("next", "Continue", _ => { followingRan = true; return StepOutcome.Pass(); }));
        var definition = Test(Group("loop").Add(dut)); definition.Curves.Add(Band());
        var run = new TestRun(definition, new TestRunOptions { Clock = clock });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? recorded = null; recorder.RunRecorded += value => recorded = value;
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Fail, status.Rating); Assert.False(status.OperatorAbort); Assert.True(followingRan);
        Assert.Equal(early ? 0 : 2, (clock.Now - started).TotalSeconds);
        Assert.Equal(ResultRating.Fail, run.Steps("capture").Single().Rating);
        Assert.NotNull(recorded); Assert.Equal(ResultRating.Fail, recorded.Rating);
        var saved = Testumgebung.Persistence.Results.ResultFile.Read(Testumgebung.Persistence.Results.ResultFile.ToBytes(recorded));
        Assert.Empty(saved.Problems); Assert.Equal(3, saved.Run.Waveforms.Count);
        var metadata = EnvelopeTraceMetadata.Decode(saved.Run.Waveforms[0].Data.Quality)!;
        Assert.Equal(2, metadata.PlannedDuration); Assert.Equal(!early, metadata.Complete); Assert.Equal(early, metadata.EarlyStop is not null);
        Assert.Equal(early ? 1 : 5, metadata.OriginalTimes.Length); Assert.True(metadata.ViolationCount > 0);
    }
    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task Full_window_passes_only_inside_the_original_band(double value)
    {
        var definition = Test(Group("loop").Add(Capture(("Werte", value.ToString())))); definition.Curves.Add(Band());
        var project = new ProjectSerializer().Clone(new TestProject(definition));
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock() });
        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        var data = Assert.IsType<WaveformSet>(run.Steps("capture").Single().Value);
        var metadata = EnvelopeTraceMetadata.Decode(data.Channels[0].Quality)!;
        Assert.Equal(new double[] { 0, 0.5, 1, 1.5, 2 }, metadata.OriginalTimes); Assert.True(metadata.Complete);
        var plot = new WaveformPlotViewModel(); plot.SetData(data); Assert.Equal(5, plot.SampleMarkers.Count);
    }
    private sealed class Meter(params double[] readings) : IHardwareAdapter
    {
        public int Calls { get; private set; }
        public string Name => "Timed fixture";
        public ChannelCapabilities GetCapabilities(string channel) => new("V", -100, 100);
        public Task<double> ReadAsync(string channel, CancellationToken token) => Task.FromResult(readings[Math.Min(Calls++, readings.Length - 1)]);
        public Task WriteAsync(string channel, double value, CancellationToken token) => Task.CompletedTask;
    }
    [Fact]
    public async Task An_earlier_violation_cannot_be_erased_by_recovery_and_an_early_threshold_stops_device_polling()
    {
        foreach (var early in new[] { false, true })
        {
            var meter = new Meter(5, early ? 3.599 : 3.999, 5);
            var definition = Test(Group("loop").Add(Capture(("Quelle", "Geräte"), ("Ressourcen", "U")))); definition.Curves.Add(Band());
            var clock = new VirtualClock(); var start = clock.Now;
            var run = new TestRun(definition, new TestRunOptions { Clock = clock, Hardware = new Testumgebung.Hardware.HardwareRegistry().Bind("U", meter, "u") });
            Assert.Equal(ResultRating.Fail, (await run.RunAsync().Within()).Rating);
            Assert.Equal(early ? 2 : 5, meter.Calls); Assert.Equal(early ? 0.5 : 2, (clock.Now - start).TotalSeconds);
        }
    }
    [Theory]
    [InlineData(0.1, 2)] [InlineData(0, 1.999)]
    public async Task All_limits_must_cover_the_complete_planned_window_before_any_device_read(double start, double end)
    {
        var meter = new Meter(5); var definition = Test(Group("loop").Add(Capture(("Quelle", "Geräte"), ("Ressourcen", "U")))); definition.Curves.Add(Band(start, end));
        var run = new TestRun(definition, new TestRunOptions { Hardware = new Testumgebung.Hardware.HardwareRegistry().Bind("U", meter, "u") });
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating); Assert.Equal(0, meter.Calls);
    }
    [Fact]
    public void Manual_duration_requires_data_from_zero_through_the_end_and_rejects_short_or_missing_limits()
    {
        var registry = new EnvelopeRecordings(); var now = DateTimeOffset.UnixEpoch;
        Assert.Throws<ArgumentException>(() => registry.Start("owner", "short", [new("U", "V", Band(end: 1))], now, duration: 2));
        Assert.Throws<ArgumentException>(() => registry.Start("owner", "late", [new("U", "V", Band(start: 0.1))], now, duration: 2));
        foreach (var duration in new[] { 0, -1d, double.NaN })
            Assert.Throws<ArgumentException>(() => registry.Start("owner", "invalid", [new("U", "V", Band())], now, duration: duration));
        registry.Start("owner", "missing-start", [new("U", "V", Band())], now, maxGap: 2, duration: 2);
        registry.Add("owner", "missing-start", 1, [5], now); var final = registry.Add("owner", "missing-start", 2, [5], now)!;
        Assert.True(final.Finished); Assert.False(final.Passed); Assert.False(final.Complete);
        registry.Start("owner", "missing-end", [new("U", "V", Band())], now, duration: 2);
        registry.Add("owner", "missing-end", 0, [5], now); registry.Add("owner", "missing-end", 1, [5], now);
        Assert.False(registry.Stop("owner", "missing-end").Complete);
        Assert.Throws<ArgumentException>(() => registry.Start("owner", "minimum", [new("U", "V", Band())], now, minimumDuration: 3, duration: 2));
        registry.Start("owner", "past-end", [new("U", "V", Band())], now, duration: 1);
        Assert.Throws<ArgumentException>(() => registry.Add("owner", "past-end", 1.1, [5], now));
    }
    [Fact]
    public async Task Manual_points_mark_normal_violations_as_fail_immediately_without_stopping_later_steps()
    {
        var loop = Group("loop").Add(Block(TraceBlocks.Start, "begin", ("Grenzkurven", "band@1"), ("Dauer", "2"), ("FailAbbruch", "true")),
            Block(TraceBlocks.Point, "bad", ("Werte", "3.999"), ("Zeit", "0")),
            Block(TraceBlocks.Point, "recover", ("Werte", "5"), ("Zeit", "1")),
            Block(TraceBlocks.Point, "end", ("Werte", "5"), ("Zeit", "2")),
            Block(TraceBlocks.Point, "ignored", ("Werte", "missing"), ("Zeit", "3")), Block(TraceBlocks.Stop, "stop"));
        var definition = Test(loop); definition.Curves.Add(Band()); var run = new TestRun(definition);
        Assert.Equal(ResultRating.Fail, (await run.RunAsync().Within()).Rating);
        Assert.Equal(ResultRating.Fail, run.Steps("bad").Single().Rating); Assert.Equal(ResultRating.Fail, run.Steps("recover").Single().Rating);
        Assert.Equal(ResultRating.Fail, run.Steps("end").Single().Rating); Assert.Equal(ResultRating.NResult, run.Steps("ignored").Single().Rating);
        Assert.Null(run.Steps("stop").Single().Value); Assert.IsType<WaveformSet>(run.Steps("end").Single().Value);
    }
    [Theory]
    [InlineData("Dauer", "0")] [InlineData("Dauer", "-1")] [InlineData("Dauer", "NaN")]
    [InlineData("Intervall", "0")] [InlineData("Intervall", "NaN")] [InlineData("MaxPunkte", "2")]
    public void Invalid_timed_configuration_is_reported_in_the_editor(string parameter, string value)
    {
        var capture = Capture((parameter, value)); Assert.NotEmpty(BlockCatalog.Default.Rebuild(capture));
    }
    [Fact]
    public async Task Operator_abort_during_a_timed_capture_preserves_partial_data_and_abort_status()
    {
        var dut = Group("dut").Var("Kennung", VarType.String, "SN-ABORT"); dut.IsDutRun = true; dut.Add(Capture());
        var definition = Test(Group("loop").Add(dut)); definition.Curves.Add(Band());
        var clock = new VirtualClock(autoAdvance: false); var run = new TestRun(definition, new TestRunOptions { Clock = clock });
        var preview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        run.EventRaised += e => { if (e is TraceUpdated) preview.TrySetResult(); };
        var running = run.RunAsync(); await preview.Task.Within(); run.RequestAbort();
        var status = await running.Within(); Assert.True(status.OperatorAbort); Assert.NotEqual(ResultRating.Pass, status.Rating);
        Assert.Contains(run.Events.OfType<StepCompleted>(), step => step.Value is WaveformSet { Channels.Count: 3 });
    }
    private sealed class SlowMeter(VirtualClock clock) : IHardwareAdapter
    {
        private int _calls;
        public string Name => "Slow fixture";
        public ChannelCapabilities GetCapabilities(string channel) => new("V", -100, 100);
        public Task<double> ReadAsync(string channel, CancellationToken token)
        {
            if (++_calls > 1) clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(5d);
        }
        public Task WriteAsync(string channel, double value, CancellationToken token) => Task.CompletedTask;
    }
    [Fact]
    public async Task Too_slow_device_reads_error_and_keep_the_previous_partial_measurements()
    {
        var clock = new VirtualClock(); var definition = Test(Group("loop").Add(Capture(("Quelle", "Geräte"), ("Ressourcen", "U")))); definition.Curves.Add(Band());
        var run = new TestRun(definition, new TestRunOptions { Clock = clock, Hardware = new Testumgebung.Hardware.HardwareRegistry().Bind("U", new SlowMeter(clock), "u") });
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
        var partial = run.Events.OfType<StepCompleted>().Select(step => step.Value).OfType<WaveformSet>().Single();
        var metadata = EnvelopeTraceMetadata.Decode(partial.Channels[0].Quality)!;
        Assert.Single(metadata.OriginalTimes); Assert.False(metadata.Complete);
    }
    [Fact]
    public void The_first_normal_violation_is_published_even_inside_the_preview_throttle_window()
    {
        var now = DateTimeOffset.UnixEpoch; var registry = new EnvelopeRecordings();
        registry.Start("owner", "trace", [new("U", "V", Band())], now, duration: 2, stopPercent: 10);
        registry.Add("owner", "trace", 0, [5], now); registry.Add("owner", "trace", 0.1, [5], now);
        var preview = registry.Add("owner", "trace", 0.2, [3.999], now)!;
        Assert.NotNull(preview); Assert.Null(preview.EarlyStop); Assert.False(preview.Finished);
        var plot = new WaveformPlotViewModel(); plot.SetData(preview.Data); Assert.True(plot.HasEnvelopeViolation);
        Assert.True(registry.HasViolation("owner", "trace")); Assert.False(registry.IsEnded("owner", "trace"));
        Assert.Null(registry.Add("owner", "trace", 0.3, [5], now));
    }
    [Fact]
    public void Timed_limit_references_are_visible_and_prevent_an_incompatible_init_version_swap()
    {
        var dut = Group("dut").Var("Kennung", VarType.String); dut.IsDutRun = true;
        var definition = Test(Group("loop").Add(dut), Group("init").Add(Capture())); definition.Curves.Add(Band());
        var original = new TestProject(definition); var changed = new ProjectSerializer().Clone(original);
        changed.Definition.Curves.Find("band", "1")!.Channels[1].Samples[0] = 7;
        Assert.False(Testumgebung.Persistence.Releases.DefinitionCompatibility.CanSwapAtDut(original, changed, out var reason)); Assert.Contains("Kurve", reason);
        var editor = new EditorViewModel(original);
        var curves = new CurvesViewModel(editor, null, () => "fixture", () => null, Testumgebung.Presentation.Mvvm.ImmediateDispatcher.Instance);
        Assert.Contains("capture", curves.Curves.Single().Usage);
    }
}
