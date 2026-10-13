using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class RecordingQualityBoundaryTests
{
    [Fact]
    public void RecorderRetainsAllEventKindsAndMarksDevelopmentRuns()
    {
        var run = new TestRun(Kit.Test(Kit.Group("loop")));
        var recorder = new ResultRecorder(run);
        ResultRun? recorded = null;
        recorder.RunRecorded += value => recorded = value;
        Emit(run, new DutRunStarted("dut") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new StepCompleted(ResultRating.Pass, 1d, "V", 0, 2, "ok", false) { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new GroupCompleted(ResultRating.Pass, 1, false) { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new GroupCompleted(ResultRating.Pass, 1, true) { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new GroupNotEntered("condition") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new OperatorContentShown(new OperatorPrompt(OperatorPromptKind.Input, "Serial number")) { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new OperatorAnswered("prompt", "SN-1") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new Diagnostic("step", "diagnostic") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId });
        Emit(run, new ResourceNotAcquired(["meter"], "busy") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new OperatorAbortRequested(1, "abort") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId });
        Emit(run, new DevelopmentEvent("tester", "patch", "details") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId });
        Emit(run, new StepStarted() { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Emit(run, new DutRunCompleted("dut", "SN-1", new RunStatus(ResultRating.Pass, false), DateTimeOffset.UnixEpoch, "1.0") { Time = DateTimeOffset.UnixEpoch, RunId = run.RunId, NodeId = "step", Path = "loop > step" });
        Assert.NotNull(recorded);
        Assert.True(recorded.IsDevelopmentRun);
        Assert.Equal(10, recorded.Events.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), recorded.Events.Select(e => e.Sequence));
        Assert.Contains(recorded.Events, e => e.Kind == ResultEventKind.OperatorAnswer && Equals(e.Value, "SN-1"));
        Assert.Contains(recorded.Events, e => e.Kind == ResultEventKind.Diagnostic && e.Rating == ResultRating.Error && e.Message!.Contains("meter"));
        Assert.Contains(recorded.Events, e => e.Kind == ResultEventKind.Development && e.Message == "patch: details");
        Assert.Contains(recorded.Events, e => e.Kind == ResultEventKind.Group && e.Message == "Exit");
    }

    [Fact]
    public async Task RecoveryWithoutStorageTargetsRemovesTheJournal()
    {
        var path = Path.GetTempFileName();
        try
        {
            var incomplete = new IncompleteRun(path, new JournalHeader { RunId = "recovered", TestName = "test", Started = DateTimeOffset.UnixEpoch }, [], DateTimeOffset.UnixEpoch, false);
            var report = await new ResultPipeline(null, null).RecoverAsync(incomplete, DateTimeOffset.UnixEpoch.AddSeconds(1), "tester");
            Assert.Null(report.File);
            Assert.Null(report.Database);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
    private static void Emit(TestRun run, EngineEvent e)
        => typeof(TestRun).GetMethod("Emit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(run, [e]);

}
