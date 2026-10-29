using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ParallelGroupTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static StepDefinition Action(string id, Func<IStepContext, Task<StepOutcome>> action)
        => new(id, id, new DelegateAction(action));
    private static GroupDefinition Parallel(string id, params NodeDefinition[] children)
        => new GroupDefinition(id, id) { Parallel = true }.Add(children);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Kit.Timeout);
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task Siblings_start_together_and_join_before_exit_and_next_iteration()
    {
        var entered = 0;
        var all = Signal();
        var finished = 0;
        var exits = 0;
        async Task<StepOutcome> Work(IStepContext ctx)
        {
            if (Interlocked.Increment(ref entered) == 2) all.TrySetResult();
            await all.Task.WaitAsync(ctx.CancellationToken);
            Interlocked.Increment(ref finished);
            return StepOutcome.Pass();
        }
        var group = Parallel("parallel", Action("a", Work), Group("nested").Add(Action("b", Work)));
        group.ExitGroup = Group("exit").Add(Action("cleanup", _ =>
        {
            Assert.Equal(2, finished);
            exits++;
            return Task.FromResult(StepOutcome.Pass());
        }));
        var run = new TestRun(Test(Group("loop").Add(group)));
        var task = run.RunAsync();
        try
        {
            Assert.Equal(ResultRating.Pass, (await task.Within()).Rating);
            Assert.Equal(1, exits);
            Assert.Empty(run.ExecutionStack);
            Assert.Equal(run.Events.Count, run.Events.Select(e => e.Sequence).Distinct().Count());
        }
        finally { run.RequestAbort(); await task.Within(); }
    }

    [Theory]
    [InlineData(RepeatResultPolicy.Keep, ResultRating.Fail)]
    [InlineData(RepeatResultPolicy.ResetToNResult, ResultRating.Pass)]
    public async Task Repetition_joins_all_branches_before_resetting_result(RepeatResultPolicy policy, ResultRating expected)
    {
        var count = 0;
        var group = Parallel("p", Action("measure", _ => Task.FromResult(Interlocked.Increment(ref count) == 1 ? StepOutcome.Fail() : StepOutcome.Pass())),
            Action("other", _ => Task.FromResult(StepOutcome.Pass())));
        group.Repetition = Repetition.Times(3);
        group.ResultBeforeRepeat = policy;
        var run = new TestRun(Test(Group("loop").Add(group)));
        Assert.Equal(expected, (await run.RunAsync().Within()).Rating);
        Assert.Equal(3, count);
        Assert.Equal(3, run.Steps("other").Count());
    }

    [Fact]
    public async Task Abort_waits_for_parallel_cleanup_before_parent_exit()
    {
        var entered = 0;
        var ready = Signal();
        var exits = new ConcurrentBag<string>();
        GroupDefinition Child(string id)
        {
            var child = Group(id).Add(Action(id + ".work", async ctx =>
            {
                if (Interlocked.Increment(ref entered) == 2) ready.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ctx.CancellationToken);
                return StepOutcome.Pass();
            }));
            child.ExitGroup = Group(id + ".exit").Add(Action(id + ".clean", _ =>
            { exits.Add(id); return Task.FromResult(StepOutcome.Pass()); }));
            return child;
        }
        var parent = Parallel("p", Child("a"), Child("b"));
        parent.ExitGroup = Group("parent.exit").Add(Action("parent.clean", _ =>
        { Assert.Equal(2, exits.Count); return Task.FromResult(StepOutcome.Pass()); }));
        var run = new TestRun(Test(Group("loop").Add(parent)));
        var task = run.RunAsync();
        try
        {
            await ready.Task.Within();
            run.RequestAbort();
            Assert.True((await task.Within()).OperatorAbort);
            Assert.Single(run.Steps("parent.clean"));
            Assert.Empty(run.ExecutionStack);
        }
        finally { run.RequestAbort(); await task.Within(); }
    }

    [Fact]
    public async Task Independent_devices_really_overlap_while_parent_reservations_remain_active()
    {
        var active = 0;
        var ready = Signal();
        var release = Signal();
        var adapter = new ProbeAdapter(async (_, token) =>
        {
            if (Interlocked.Increment(ref active) == 2) ready.TrySetResult();
            await release.Task.WaitAsync(token);
            return 230;
        });
        var hardware = new HardwareRegistry().Bind("A", adapter, "a", requiresLock: true, lockKey: "A")
            .Bind("B", adapter, "b", requiresLock: true, lockKey: "B");
        var group = Parallel("p", Action("a", async ctx => StepOutcome.Pass(await ctx.ReadAsync("A"))),
            Action("b", async ctx => StepOutcome.Pass(await ctx.ReadAsync("B"))));
        group.Resources.AddRange(["A", "B"]);
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Hardware = hardware });
        var task = run.RunAsync();
        try { await ready.Task.Within(); release.TrySetResult(); Assert.Equal(ResultRating.Pass, (await task.Within()).Rating); }
        finally { release.TrySetResult(); run.RequestAbort(); await task.Within(); }
        Assert.Empty(run.Resources.Snapshot());
    }

    [Fact]
    public async Task Same_device_is_exclusive_until_the_entire_step_finishes()
    {
        var started = Signal();
        var release = Signal();
        var active = 0;
        var maximum = 0;
        var adapter = new ProbeAdapter(async (_, token) =>
        {
            var now = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, now);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
            return 1;
        });
        var group = Parallel("p", Action("a", async ctx => StepOutcome.Pass(await ctx.ReadAsync("R"))),
            Action("b", async ctx => StepOutcome.Pass(await ctx.ReadAsync("R"))));
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Hardware = new HardwareRegistry().Bind("R", adapter, "x") });
        var task = run.RunAsync();
        try
        {
            await started.Task.Within();
            await Until(() => run.Resources.Snapshot().Any(r => r.WaitingRuns.Count > 0));
            Assert.Equal(1, active);
            release.TrySetResult();
            Assert.Equal(ResultRating.Pass, (await task.Within()).Rating);
            Assert.Equal(1, maximum);
        }
        finally { release.TrySetResult(); run.RequestAbort(); await task.Within(); }
    }

    [Fact]
    public async Task Sibling_group_reservations_cover_all_their_steps_even_with_parent_reservation()
    {
        var log = new ConcurrentQueue<string>();
        var first = Signal();
        var release = Signal();
        GroupDefinition Child(string id)
        {
            var child = Group(id).Add(Action(id + "1", async ctx =>
            { log.Enqueue(id + "1"); first.TrySetResult(); await release.Task.WaitAsync(ctx.CancellationToken); return StepOutcome.Pass(); }),
                Action(id + "2", _ => { log.Enqueue(id + "2"); return Task.FromResult(StepOutcome.Pass()); }));
            child.Resources.Add("R");
            return child;
        }
        var parent = Parallel("p", Child("A"), Child("B"));
        parent.Resources.Add("R");
        var run = new TestRun(Test(Group("loop").Add(parent)));
        var task = run.RunAsync();
        try
        {
            await first.Task.Within();
            await Until(() => run.Resources.Snapshot().Any(r => r.WaitingRuns.Count > 0));
            Assert.Single(log);
            release.TrySetResult();
            Assert.Equal(ResultRating.Pass, (await task.Within()).Rating);
            var rows = log.ToArray();
            Assert.True(rows.SequenceEqual(new[] { "A1", "A2", "B1", "B2" }) || rows.SequenceEqual(new[] { "B1", "B2", "A1", "A2" }));
        }
        finally { release.TrySetResult(); run.RequestAbort(); await task.Within(); }
    }

    [Fact]
    public void Editor_persistence_and_diff_keep_parallel_setting()
    {
        var project = new TestProject(Test(Group("loop")));
        var serializer = new ProjectSerializer();
        var before = serializer.Clone(project);
        var editor = new EditorViewModel(project);
        editor.SelectedNode = editor.FindNode("loop");
        var properties = Assert.IsType<GroupPropertiesViewModel>(editor.Properties);
        Assert.False(properties.Parallel);
        properties.Parallel = true;
        properties.Parallel = true;
        Assert.True(properties.Parallel);
        Assert.Contains("parallel", editor.SelectedNode!.Annotation);
        Assert.True(editor.IsDirty);
        Assert.True(serializer.Clone(project).Definition.Loop.Parallel);
        Assert.Contains(ProjectDiff.Compare(before, project), d => d.Detail.Contains("Parallele Ausführung"));
        properties.Parallel = false;
        Assert.False(properties.Parallel);
        Assert.DoesNotContain("\"parallel\"", serializer.Serialize(project), StringComparison.Ordinal);
    }

    [Fact]
    public void Parallel_root_and_concurrent_dut_groups_are_rejected()
    {
        var definition = Test(Parallel("loop", Group("dut")));
        definition.Root.Parallel = true;
        Assert.Contains(definition.Validate(), d => d.Contains("Hauptablauf"));
        definition.Root.Parallel = false;
        ((GroupDefinition)definition.Loop.Children[0]).IsDutRun = true;
        Assert.Contains(definition.Validate(), d => d.Contains("Prüflingsdurchläufe"));
    }

    [Fact]
    public void Parallel_duration_disables_stop_between_individual_steps()
    {
        var project = new TestProject(Test(Group("loop")));
        var editor = new EditorViewModel(project);
        editor.SelectedNode = editor.FindNode("loop");
        var props = Assert.IsType<GroupPropertiesViewModel>(editor.Properties);
        Assert.False(props.CanStopWithinIteration);
        props.RepeatMode = RepeatMode.Duration;
        Assert.True(props.CanStopWithinIteration);
        props.StopWithinIteration = true;
        props.Parallel = true;
        Assert.False(props.StopWithinIteration);
        Assert.False(props.CanStopWithinIteration);
        Assert.Empty(project.Definition.Validate());
        project.Definition.Loop.Repetition = project.Definition.Loop.Repetition with { StopWithinIteration = true };
        Assert.Contains(project.Definition.Validate(), d => d.Contains("Zeitspanne zwischen"));
    }

    [Fact]
    public async Task Parallel_breakpoints_keep_separate_pause_contexts()
    {
        var dbg = new DebugController();
        dbg.SetBreakpoint("a", "test");
        dbg.SetBreakpoint("b", "test");
        var seen = new ConcurrentBag<string>();
        var group = Parallel("p", Action("a", _ => Task.FromResult(StepOutcome.Pass())), Action("b", _ => Task.FromResult(StepOutcome.Pass())));
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Debugger = dbg });
        run.EventRaised += e => { if (e is Paused pause) { seen.Add(pause.Info.NodeId); dbg.Continue(); } };
        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        Assert.Equal(new[] { "a", "b" }, seen.Order());
    }

    [Fact]
    public async Task A_parallel_branch_cannot_use_reservation_owned_only_by_its_sibling()
    {
        var owning = Signal();
        var release = Signal();
        var owner = Group("owner").Add(Action("hold", async ctx =>
        { owning.TrySetResult(); await release.Task.WaitAsync(ctx.CancellationToken); return StepOutcome.Pass(); }));
        owner.Resources.Add("R");
        var intruder = Action("intruder", async ctx =>
        {
            await owning.Task.WaitAsync(ctx.CancellationToken);
            try { return StepOutcome.Pass(await ctx.ReadAsync("R")); }
            finally { release.TrySetResult(); }
        });
        var adapter = new ProbeAdapter((_, _) => Task.FromResult(1.0));
        var run = new TestRun(Test(Group("loop").Add(Parallel("p", owner, intruder))),
            new TestRunOptions { Hardware = new HardwareRegistry().Bind("R", adapter, "x", requiresLock: true) });
        var task = run.RunAsync();
        try
        {
            Assert.Equal(ResultRating.Error, (await task.Within()).Rating);
            Assert.Equal(ResultRating.Error, run.Steps("intruder").Single().Rating);
            Assert.Contains("ohne Reservierung", run.Steps("intruder").Single().Message);
        }
        finally { release.TrySetResult(); run.RequestAbort(); await task.Within(); }
    }

    [Fact]
    public async Task A_dut_in_a_library_call_cannot_replace_an_already_active_dut()
    {
        var body = Group("hidden.body").Var("Kennung", VarType.String, "hidden");
        body.IsDutRun = true;
        var call = new StepDefinition("call", "call", new GroupBlockAction("hidden", new Dictionary<string, string>(), new Dictionary<string, string>()));
        var dut = Group("dut").Var("Kennung", VarType.String, "outer").Add(Parallel("p", call));
        dut.IsDutRun = true;
        var definition = Test(Group("loop").Add(dut));
        definition.GroupLibrary.Add(new GroupBlockDefinition("hidden", "hidden", body));
        var run = new TestRun(definition);
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
        Assert.Single(run.Events.OfType<DutRunStarted>());
        Assert.Equal("outer", Assert.Single(run.Events.OfType<DutRunCompleted>()).DutId);
    }

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 2.0)]
    public async Task Virtual_parallel_time_counts_overlap_and_serialized_device_waits(bool shared, double expectedSeconds)
    {
        var clock = new VirtualClock();
        var started = clock.Now;
        var adapter = new ProbeAdapter((_, _) => Task.FromResult(1.0));
        var hardware = new HardwareRegistry().Bind("A", adapter, "a", lockKey: "A")
            .Bind("B", adapter, "b", lockKey: shared ? "A" : "B");
        StepDefinition Work(string id, string resource) => Action(id, async ctx =>
        {
            await ctx.ReadAsync(resource);
            await ctx.Clock.Delay(TimeSpan.FromSeconds(1), ctx.CancellationToken);
            return StepOutcome.Pass();
        });
        var run = new TestRun(Test(Group("loop").Add(Parallel("p", Work("a", "A"), Work("b", "B")))), new TestRunOptions { Hardware = hardware, Clock = clock });
        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        Assert.Equal(expectedSeconds, (clock.Now - started).TotalSeconds);
    }

    [Fact]
    public async Task Nested_parallel_virtual_time_joins_at_latest_branch_completion()
    {
        var clock = new VirtualClock();
        var started = clock.Now;
        StepDefinition Wait(string id, double seconds) => Action(id, async ctx =>
        { await ctx.Clock.Delay(TimeSpan.FromSeconds(seconds), ctx.CancellationToken); return StepOutcome.Pass(); });
        var nested = Group("nested").Add(Wait("before", .5), Parallel("inner", Wait("one", 1), Wait("two", 2)), Wait("after", .5));
        var run = new TestRun(Test(Group("loop").Add(Parallel("outer", nested, Wait("other", 1)))), new TestRunOptions { Clock = clock });
        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        Assert.Equal(3, (clock.Now - started).TotalSeconds);
    }

    [Fact]
    public async Task Manually_advanced_clock_waits_for_both_parallel_timers()
    {
        var clock = new VirtualClock(autoAdvance: false);
        var run = new TestRun(Test(Group("loop").Add(Parallel("p", Action("a", async ctx =>
        { await ctx.Clock.Delay(TimeSpan.FromSeconds(1), ctx.CancellationToken); return StepOutcome.Pass(); }),
            Action("b", async ctx => { await ctx.Clock.Delay(TimeSpan.FromSeconds(2), ctx.CancellationToken); return StepOutcome.Pass(); })))), new TestRunOptions { Clock = clock });
        var task = run.RunAsync();
        try
        {
            await Until(() => clock.PendingTimers == 2);
            clock.Advance(TimeSpan.FromSeconds(1));
            await run.WaitFor<StepCompleted>(e => e.NodeId == "a");
            Assert.False(task.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(ResultRating.Pass, (await task.Within()).Rating);
        }
        finally { run.RequestAbort(); await task.Within(); }
    }

    private sealed class ProbeAdapter(Func<string, CancellationToken, Task<double>> read) : IHardwareAdapter
    {
        public string Name => "probe";
        public ChannelCapabilities GetCapabilities(string channel) => new("V", 0, 300);
        public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => read(channel, cancellationToken);
        public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
