using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;
namespace Testumgebung.Engine.Tests;
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class ConditionalCodeBreakpointTests
{
    [Theory]
    [InlineData("Python","n=0\nfor i in range(1,5):\n    n=i\n    pass\nreturn Pass(n)")]
    [InlineData("C#","long n=0;\nfor (long i=1; i<=4; i++)\n{\n    n=i;\n}\nreturn Pass(n);")]
    public async Task Worker_line_conditions_see_local_variables(string language,string source)
    {
        var debugger=new DebugController();debugger.ConfigureCodeBreakpoint("code",4,new(BreakpointMode.Expression,"i == 3 && Debug.Treffer == 3 && Debug.Iteration == 1"));
        var stops=new List<CodeLinePause>();debugger.CodeLinePaused+=p=>{stops.Add(p);debugger.CodeContinue();};
        var code=BlockCatalog.Default.Create(BlockCatalog.Code,"code",parameters:new Dictionary<string,string>{["Sprache"]=language,["Quelltext"]=source,["Timeout"]="30"});
        var run=new TestRun(Test(Group("loop").Add(code)),new TestRunOptions{Debugger=debugger,CodeRuntime=CodeBlockTests.SharedRuntime.Value});
        Assert.Equal(ResultRating.Pass,(await run.RunAsync().WaitAsync(TimeSpan.FromMinutes(2))).Rating);
        Assert.Equal(3,Convert.ToInt64(Assert.Single(stops).Variables.Single(v=>v.Name=="i").Value));
    }
    [Fact]
    public async Task Code_hit_filters_reduced_mode_and_missing_variables_are_explicit()
    {
        var debugger=new DebugController();debugger.ConfigureCodeBreakpoint("code",3,new(FirstHit:2,Every:2));
        var stops=0;debugger.CodeLinePaused+=_=>{stops++;debugger.CodeContinue();};
        var step=new StepDefinition("code","code",new DelegateAction(async ctx=>
        {
            for(var i=0;i<5;i++) await ctx.CodeDebugAsync(new(3,"line",new Dictionary<string,object?>()),CodeDebugCapability.FullLineStepping);
            return StepOutcome.Pass();
        }));
        for(var i=0;i<2;i++) {var run=new TestRun(Test(Group("loop").Add(step)),new TestRunOptions{Debugger=debugger});await run.RunAsync().Within();}
        Assert.Equal(4,stops);
        debugger.ConfigureCodeBreakpoint("code",3,new(BreakpointMode.Expression,"missing > 0"));
        var failed=new TestRun(Test(Group("loop").Add(step)),new TestRunOptions{Debugger=debugger});await failed.RunAsync().Within();
        Assert.Equal(9,stops);Assert.Equal(5,failed.Events.OfType<Diagnostic>().Count(d=>d.Message.Contains("Haltebedingung")));
        debugger.ConfigureCodeBreakpoint("code",3,new(BreakpointMode.Expression,"value == 7"));
        var reduced=new StepDefinition("code","code",new DelegateAction(async ctx=>
        {
            await ctx.CodeDebugAsync(new(0,"entry",new Dictionary<string,object?>{{"value",7L}}),CodeDebugCapability.EntryExitOnly);
            return StepOutcome.Pass();
        }));
        await new TestRun(Test(Group("loop").Add(reduced)),new TestRunOptions{Debugger=debugger}).RunAsync().Within();Assert.Equal(10,stops);
        debugger.ClearCodeBreakpoints("code");Assert.Empty(debugger.CodeBreakpoints("code"));Assert.Equal(BreakpointRule.Default,debugger.GetCodeBreakpointRule("code",3));
        Assert.Throws<ArgumentOutOfRangeException>(()=>debugger.ConfigureCodeBreakpoint("code",0,new()));
    }
    [Fact]
    public async Task Native_conditions_inspect_current_values_once_and_reuse_the_snapshot()
    {
        var debugger=new DebugController();debugger.ConfigureCodeBreakpoint("code",3,new(BreakpointMode.Expression,"counter == 42 && ready && voltage > 1 && label == \"text\""));
        var calls=0;var stops=0;debugger.CodeLinePaused+=p=>{stops++;Assert.Contains(p.Variables,v=>v.Name=="GDB: counter");debugger.CodeContinue();};
        var step=new StepDefinition("code","code",new DelegateAction(async ctx=>
        {
            var e=new CodeDebugEvent(3,"line",new Dictionary<string,object?>()) {InspectNativeAsync=_=>
            {
                calls++;return Task.FromResult(new NativeDebugSnapshot(new Dictionary<string,string>{{"counter","42"},{"ready","true"},{"voltage","1.5"},{"label","text"}},[]));
            }};
            await ctx.CodeDebugAsync(e,CodeDebugCapability.FullLineStepping);return StepOutcome.Pass();
        }));
        var run=new TestRun(Test(Group("loop").Add(step)),new TestRunOptions{Debugger=debugger});
        Assert.Equal(ResultRating.Pass,(await run.RunAsync().Within()).Rating);Assert.Equal(1,calls);Assert.Equal(1,stops);
    }
    [Fact]
    public async Task False_code_condition_does_not_block_independent_worker_behind_another_code_pause()
    {
        var debugger=new DebugController();debugger.SetCodeBreakpoint("a",1);debugger.ConfigureCodeBreakpoint("b",1,new(BreakpointMode.Expression,"false"));
        var paused=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);debugger.CodeLinePaused+=_=>paused.TrySetResult();
        var a=new StepDefinition("a","a",new DelegateAction(async ctx=>{await ctx.CodeDebugAsync(new(1,"line",new Dictionary<string,object?>()),CodeDebugCapability.FullLineStepping);return StepOutcome.Pass();}));
        var b=new StepDefinition("b","b",new DelegateAction(async ctx=>
        {await paused.Task;await ctx.CodeDebugAsync(new(1,"line",new Dictionary<string,object?>()),CodeDebugCapability.FullLineStepping);debugger.CodeContinue();return StepOutcome.Pass();}));
        var parallel=Group("parallel").Add(a,b);parallel.Parallel=true;
        var run=new TestRun(Test(Group("loop").Add(parallel)),new TestRunOptions{Debugger=debugger});var task=run.RunAsync();
        try {Assert.Equal(ResultRating.Pass,(await task.Within()).Rating);}
        finally {run.RequestAbort();await task.Within();}
    }

}
