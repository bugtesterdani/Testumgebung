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
}
