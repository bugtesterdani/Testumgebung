using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;
namespace Testumgebung.Engine.Tests;
public sealed class BreakpointRuleTests
{
    [Theory]
    [InlineData(BreakpointMode.Always,6)] [InlineData(BreakpointMode.FailOrError,2)]
    [InlineData(BreakpointMode.Fail,1)] [InlineData(BreakpointMode.Error,1)] [InlineData(BreakpointMode.Expression,3)]
    public async Task Ratings_expressions_and_defaults_control_actual_stops(BreakpointMode mode,int expected)
    {
        var count=0;var values=new[]{ResultRating.Pass,ResultRating.Fail,ResultRating.Error,ResultRating.NResult,ResultRating.Pass,ResultRating.Pass};
        var step=new StepDefinition("step","step",new DelegateAction(ctx=> { ctx.Set("Count",(long)++count);return Task.FromResult(new StepOutcome(values[count-1])); }));
        var loop=Group("loop").Var("Count",VarType.Int).Add(step);loop.Repetition=Repetition.Times(6);
        var debugger=new DebugController();debugger.ConfigureBreakpoint("step",new(mode,"Count >= 4 && Debug.Iteration >= 4 && Debug.Treffer >= 4"));
        debugger.Paused+=_=>debugger.Continue();var run=new TestRun(Test(loop),new TestRunOptions{Debugger=debugger});
        Assert.Equal(ResultRating.Error,(await run.RunAsync().Within()).Rating);Assert.Equal(expected,run.Events.OfType<Paused>().Count());
    }
    [Fact]
    public async Task Hit_filters_count_once_per_execution_and_reset_for_each_run()
    {
        var step=new StepDefinition("step","step",new DelegateAction(_=>Task.FromResult(StepOutcome.Pass())));
        var loop=Group("loop").Add(step);loop.Repetition=Repetition.Times(6);
        var debugger=new DebugController();debugger.ConfigureBreakpoint("step",new(FirstHit:2,Every:2));debugger.Paused+=_=>debugger.Continue();
        for(var i=0;i<2;i++)
        {
            var run=new TestRun(Test(loop),new TestRunOptions{Debugger=debugger});await run.RunAsync().Within();
            Assert.Equal(new[]{2,4,6},run.Events.OfType<Paused>().Select(p=>p.Info.Iteration));
        }
    }
    [Fact]
    public async Task Group_conditions_see_left_local_snapshot_and_condition_errors_stop_with_diagnostic()
    {
        var group=Group("group").Var("Local",VarType.Int,42L);
        var debugger=new DebugController();debugger.ConfigureBreakpoint("group",new(BreakpointMode.Expression,"Local == 42 && ../Outer == 7"));
        debugger.Paused+=_=>debugger.Continue();
        var run=new TestRun(Test(Group("loop").Var("Outer",VarType.Int,7L).Add(group)),new TestRunOptions{Debugger=debugger});
        await run.RunAsync().Within();Assert.Single(run.Events.OfType<Paused>());
        debugger.ConfigureBreakpoint("group",new(BreakpointMode.Expression,"Unknown > 1"));
        var error=new TestRun(run.Definition,new TestRunOptions{Debugger=debugger});await error.RunAsync().Within();
        Assert.Single(error.Events.OfType<Paused>());Assert.Contains(error.Events.OfType<Diagnostic>(),d=>d.Message.Contains("Haltebedingung nicht auswertbar"));
    }
    [Theory]
    [InlineData(0,1)] [InlineData(1,0)] [InlineData(-1,2)]
    public void Invalid_hit_filters_are_rejected(long first,long every) => Assert.Throws<ArgumentException>(()=>new BreakpointRule(FirstHit:first,Every:every).Validate());
    [Fact]
    public void Invalid_modes_and_expressions_are_rejected()
    {
        Assert.Throws<ArgumentException>(()=>new BreakpointRule((BreakpointMode)99).Validate());
        Assert.Throws<ArgumentException>(()=>new BreakpointRule(BreakpointMode.Expression).Validate());
        Assert.Throws<ArgumentException>(()=>new BreakpointRule(BreakpointMode.Fail).Validate(code:true));
        Assert.ThrowsAny<Exception>(()=>new BreakpointRule(BreakpointMode.Expression,"(").Validate());
    }
    [Fact]
    public async Task False_conditions_do_not_wait_behind_a_paused_parallel_sibling()
    {
        var paused=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var debugger=new DebugController();debugger.SetBreakpoint("a");debugger.ConfigureBreakpoint("b",new(BreakpointMode.Expression,"false"));
        debugger.Paused+=_=>paused.TrySetResult();
        var a=new StepDefinition("a","a",new DelegateAction(_=>Task.FromResult(StepOutcome.Pass())));
        var b=new StepDefinition("b","b",new DelegateAction(async _=>{await paused.Task;return StepOutcome.Pass();}));
        var release=new StepDefinition("release","release",new DelegateAction(_=>{debugger.Continue();return Task.FromResult(StepOutcome.Pass());}));
        var parallel=Group("parallel").Add(a,Group("branch").Add(b,release));parallel.Parallel=true;
        var run=new TestRun(Test(Group("loop").Add(parallel)),new TestRunOptions{Debugger=debugger});var task=run.RunAsync();
        try {Assert.Equal(ResultRating.Pass,(await task.Within()).Rating);Assert.Single(run.Events.OfType<Paused>());}
        finally {run.RequestAbort();await task.Within();}
    }

}
