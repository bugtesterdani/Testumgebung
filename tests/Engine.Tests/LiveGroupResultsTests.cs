using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class LiveGroupResultsTests
{
    [Theory]
    [InlineData(ResultRating.NResult, false)]
    [InlineData(ResultRating.Pass, true)]
    [InlineData(ResultRating.Fail, false)]
    [InlineData(ResultRating.Fail, true)]
    [InlineData(ResultRating.Error, false)]
    [InlineData(ResultRating.Error, true)]
    public void Reset_preserves_sibling_ratings_including_active_parallel_branches(ResultRating sibling, bool completed)
    {
        var display = new LiveGroupResults();
        Enter(display, "root", "r");
        Enter(display, "retry", "a", "r");
        Enter(display, "other", "b", "r");
        Step(display, "other-code", "b", sibling);
        if (completed) Complete(display, "other", "b", sibling);
        Step(display, "retry-code", "a", ResultRating.Fail);
        Reset(display, "retry", "a");
        Assert.Equal(ResultRating.NResult, display.ForNode("retry"));
        Assert.Null(display.ForNode("retry-code"));
        Assert.Equal(sibling, display.ForNode("other"));
        Assert.Equal(sibling, display.CurrentRating);
        Step(display, "retry-code", "a", ResultRating.Pass);
        Assert.Equal(sibling.Merge(ResultRating.Pass), display.CurrentRating);
    }

    [Fact]
    public void Parent_keep_preserves_previous_invocation_while_the_current_child_starts_fresh()
    {
        var display = new LiveGroupResults();
        Enter(display, "parent", "p");
        Enter(display, "child", "first", "p");
        Step(display, "code", "first", ResultRating.Fail);
        Complete(display, "child", "first", ResultRating.Fail);
        Enter(display, "child", "second", "p");
        Assert.Equal(ResultRating.NResult, display.ForNode("child"));
        Assert.Null(display.ForNode("code"));
        Assert.Equal(ResultRating.Fail, display.CurrentRating);
        Step(display, "code", "second", ResultRating.Pass);
        Complete(display, "child", "second", ResultRating.Pass);
        Assert.Equal(ResultRating.Pass, display.ForNode("child"));
        Assert.Equal(ResultRating.Fail, display.CurrentRating);
        Reset(display, "parent", "p");
        Assert.Equal(ResultRating.NResult, display.CurrentRating);
        Assert.Null(display.ForNode("child"));
        Assert.Null(display.ForNode("code"));
    }

    [Fact]
    public void Completed_duts_do_not_contaminate_the_current_dut_after_a_retry_reset()
    {
        var display = new LiveGroupResults();
        Enter(display, "loop", "loop");
        Enter(display, "dut", "first", "loop");
        Dut(display, "first");
        Step(display, "code", "first", ResultRating.Fail);
        Assert.Equal(ResultRating.Fail, display.CurrentRating);
        Complete(display, "dut", "first", ResultRating.Fail);
        Assert.Null(display.CurrentRating);
        Enter(display, "dut", "second", "loop");
        Assert.Null(display.CurrentRating);
        Dut(display, "second");
        Enter(display, "retry", "retry", "second");
        Step(display, "code", "retry", ResultRating.Fail);
        Reset(display, "retry", "retry");
        Assert.Equal(ResultRating.NResult, display.CurrentRating);
        Step(display, "code", "retry", ResultRating.Pass);
        Assert.Equal(ResultRating.Pass, display.CurrentRating);
        Complete(display, "retry", "retry", ResultRating.Pass);
        Complete(display, "dut", "second", ResultRating.Pass);
        Assert.Null(display.CurrentRating);
        Assert.Equal(ResultRating.Fail, display.ForNode("loop"));
    }

    [Fact]
    public void Concurrent_instances_of_a_shared_group_are_tracked_separately()
    {
        var display = new LiveGroupResults();
        Enter(display, "root", "root");
        Enter(display, "shared", "a", "root");
        Enter(display, "shared", "b", "root");
        Step(display, "code-a", "a", ResultRating.Fail);
        Step(display, "code-b", "b", ResultRating.Pass);
        Reset(display, "shared", "b");
        Assert.Equal(ResultRating.Fail, display.CurrentRating);
        Assert.Equal(ResultRating.NResult, display.ForNode("shared"));
        Complete(display, "shared", "b", ResultRating.Pass);
        Enter(display, "shared", "c", "root");
        Assert.Equal(ResultRating.Fail, display.CurrentRating);
        Complete(display, "shared", "a", ResultRating.Error);
        Enter(display, "shared", "d", "root");
        Assert.Equal(ResultRating.Error, display.CurrentRating);
        Assert.Equal(ResultRating.NResult, display.ForNode("shared"));
    }

    [Fact]
    public void Missing_legacy_scope_metadata_is_ignored_and_a_new_run_clears_old_state()
    {
        var display = new LiveGroupResults();
        Step(display, "legacy", "", ResultRating.Fail);
        Complete(display, "legacy", "", ResultRating.Fail);
        Reset(display, "legacy", "");
        Dut(display, "");
        Assert.Null(display.ForNode("legacy"));
        Assert.Equal(ResultRating.NResult, display.CurrentRating);
        Enter(display, "root", "root");
        Enter(display, "orphan", "orphan", "unknown-parent");
        Step(display, "orphan-code", "orphan", ResultRating.Fail);
        Reset(display, "root", "root");
        Assert.Equal(ResultRating.Fail, display.ForNode("orphan"));
        Complete(display, "orphan", "orphan", ResultRating.Fail);
        Step(display, "known", "root", ResultRating.Error);
        Assert.Equal(ResultRating.Error, display.CurrentRating);
        display.Clear();
        Assert.Equal(ResultRating.NResult, display.CurrentRating);
        Assert.Null(display.ForNode("root"));
    }

    [Fact]
    public async Task Graphical_block_scopes_keep_the_runtime_parent_across_the_port_boundary()
    {
        var attempts = 0;
        var body = Kit.Group("body").Add(Steps.Code("rate", "rate", _ =>
            ++attempts == 1 ? StepOutcome.Fail() : StepOutcome.Pass()));
        body.Repetition = Repetition.Times(2);
        body.ResultBeforeRepeat = RepeatResultPolicy.ResetToNResult;
        var definition = Kit.Test(Kit.Group("loop").Add(BlockCatalog.Default.Create(
            BlockCatalog.GroupBlock, "call", parameters: new Dictionary<string, string> { ["Baustein"] = "block" })));
        definition.GroupLibrary.Add(new GroupBlockDefinition("block", "Block", body));
        var run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync();
        var loop = run.Events.OfType<GroupEntered>().Single(item => item.NodeId == "loop");
        var block = run.Events.OfType<GroupEntered>().Single(item => item.NodeId == "body");
        Assert.Equal(loop.ScopeId, block.ParentScopeId);
        var display = new LiveGroupResults();
        foreach (var item in run.Events)
        {
            display.Apply(item);
            if (item is GroupIterationStarted { ResultReset: true }) Assert.Equal(ResultRating.NResult, display.CurrentRating);
        }
        Assert.Equal(ResultRating.Pass, display.CurrentRating);
    }

    private static void Enter(LiveGroupResults display, string node, string scope, string? parent = null)
        => display.Apply(new GroupEntered(scope, false) { Time = DateTimeOffset.UnixEpoch, RunId = "run", NodeId = node, Path = node, ParentScopeId = parent });
    private static void Step(LiveGroupResults display, string node, string scope, ResultRating rating)
        => display.Apply(new StepCompleted(rating, null, null, null, null, null, false) { Time = DateTimeOffset.UnixEpoch, RunId = "run", NodeId = node, Path = node, GroupScopeId = scope });
    private static void Complete(LiveGroupResults display, string node, string scope, ResultRating rating)
        => display.Apply(new GroupCompleted(rating, 1, false) { Time = DateTimeOffset.UnixEpoch, RunId = "run", NodeId = node, Path = node, ScopeId = scope });
    private static void Reset(LiveGroupResults display, string node, string scope)
        => display.Apply(new GroupIterationStarted(2) { Time = DateTimeOffset.UnixEpoch, RunId = "run", NodeId = node, Path = node, ScopeId = scope, ResultReset = true });
    private static void Dut(LiveGroupResults display, string scope)
        => display.Apply(new DutRunStarted(scope) { Time = DateTimeOffset.UnixEpoch, RunId = "run", NodeId = "dut", Path = "dut", ScopeId = scope });
}
