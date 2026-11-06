using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class TraceExampleTests
{
    private sealed class Answers(string scenario) : IOperatorInterface
    {
        public bool SawFollowingStep { get; private set; }
        public Task<object?> AskAsync(string runId, OperatorPrompt prompt, CancellationToken token)
        {
            if (prompt.Kind == OperatorPromptKind.Input) return Task.FromResult<object?>("DEMO-TEST");
            if (prompt.Kind == OperatorPromptKind.Choice) return Task.FromResult<object?>(scenario);
            SawFollowingStep = prompt.Text.Contains("Folgeschritt wurde erreicht", StringComparison.Ordinal);
            return Task.FromResult<object?>(true);
        }
    }
    [Theory]
    [InlineData("PASS", ResultRating.Pass, 5, 11)]
    [InlineData("FAIL", ResultRating.Fail, 5, 11)]
    [InlineData("Fruehes Ende", ResultRating.Fail, 0, 1)]
    [InlineData("Kurve", ResultRating.Pass, 5, 6)]
    public async Task Shipped_example_is_loadable_and_demonstrates_rating_duration_continuation_and_saved_curves(string scenario, ResultRating expected, int seconds, int points)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var project = new ProjectSerializer().Load(Path.Combine(root.FullName, "docs", "beispiele", "grenzband-demo.tproj.json"));
        Assert.Empty(project.Definition.Validate());
        foreach (var step in project.Definition.AllNodes().OfType<StepDefinition>()) Assert.Empty(BlockCatalog.Default.Rebuild(step));
        var clock = new VirtualClock(); var started = clock.Now; var answers = new Answers(scenario);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = clock, Operator = answers });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? recorded = null; recorder.RunRecorded += result => recorded = result;
        var status = await run.RunAsync().Within();
        Assert.Equal(expected, status.Rating); Assert.False(status.OperatorAbort); Assert.Equal(seconds, (clock.Now - started).TotalSeconds);
        Assert.True(answers.SawFollowingStep); Assert.Single(run.Steps("demo-next")); Assert.NotNull(recorded);
        Assert.Equal("DEMO-TEST", recorded.DutId); Assert.Equal(expected, recorded.Rating); Assert.Equal(3, recorded.Waveforms.Count);
        var metadata = EnvelopeTraceMetadata.Decode(recorded.Waveforms.Single(w => w.Data.Channel == "Spannung").Data.Quality)!;
        Assert.Equal(points, metadata.OriginalTimes.Length); Assert.Equal(5, metadata.PlannedDuration);
        Assert.Equal("demo-spannungsband@1", metadata.Curve); Assert.Equal(scenario == "Fruehes Ende", metadata.EarlyStop is not null);
        Assert.Equal(scenario != "Fruehes Ende", metadata.Complete);
    }

    [Theory]
    [InlineData(0.1, ResultRating.Pass)]
    [InlineData(0.2, ResultRating.Fail)]
    public async Task Capacitor_example_reads_simulated_devices_and_switches_supply_off(double capacitance, ResultRating expected)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var project = new ProjectSerializer().Load(Path.Combine(root.FullName, "docs", "beispiele", "kondensatorladung-demo.tproj.json"));
        Assert.Empty(project.Definition.Validate());
        foreach (var step in project.Definition.AllNodes().OfType<StepDefinition>()) Assert.Empty(BlockCatalog.Default.Rebuild(step));
        // Change only the test specimen. The shipped example retains the nominal 0.1 F.
        var dut = (GroupDefinition)project.Definition.Loop.Children.Single();
        var original = dut.Variables.Single(v => v.Name == "Kapazitaet");
        dut.Variables.Remove(original);
        dut.Var("Kapazitaet", VarType.Double, capacitance);
        var (adapter, hardware) = SampleProjects.CreateSimulation(project);
        var clock = new VirtualClock(); var started = clock.Now;
        var run = new TestRun(project.Definition, new TestRunOptions { Hardware = hardware, Clock = clock, Operator = new Answers("PASS") });
        var recorder = new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? recorded = null; recorder.RunRecorded += result => recorded = result;
        var status = await run.RunAsync().Within();
        Assert.Equal(expected, status.Rating);
        Assert.Equal(8, (clock.Now - started).TotalSeconds);
        Assert.Equal(0d, adapter.GetOutput("cap_psu_on"));
        Assert.Equal(41, run.Steps("cap-point").Count());
        Assert.NotNull(recorded);
        var waveform = recorded.Waveforms.Single(w => w.Data.Channel == "Kondensatorspannung").Data;
        var metadata = EnvelopeTraceMetadata.Decode(waveform.Quality)!;
        Assert.Equal(41, metadata.OriginalTimes.Length);
        Assert.Equal(0, metadata.OriginalTimes[0]); Assert.Equal(8, metadata.OriginalTimes[^1]);
        Assert.True(metadata.Complete); Assert.Null(metadata.EarlyStop);
        var writes = adapter.WriteLog.Where(w => w.Channel == "cap_dmm_v").Select(w => w.Value).ToArray();
        Assert.Equal(0d, writes[0]);
        Assert.Equal(2.0 / capacitance * 0.1, writes[11], 8);
        Assert.Equal(Math.Min(5, 0.1 * 8 / capacitance), writes[^1], 8);
    }
}
