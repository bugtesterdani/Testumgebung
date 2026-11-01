using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;
namespace Testumgebung.Engine.Tests;

public sealed class NodeActivationTests
{
    [Fact]
    public async Task Disabled_group_skips_children_exit_locks_and_breakpoints_then_can_be_reactivated()
    {
        var calls = 0;
        StepDefinition Work(string id) => new(id, id, new DelegateAction(_ => { calls++; return Task.FromResult(StepOutcome.Pass()); }));
        var child = Group("off").Add(Work("child")); child.ExitGroup = Group("exit").Add(Work("cleanup"));
        child.Resources.Add("R"); child.Disabled = true;
        var definition = Test(Group("loop").Add(child, Work("next")));
        var resources = new ResourceManager(); var debugger = new DebugController(); debugger.SetBreakpoint("off");
        debugger.Paused += _ => debugger.Continue();
        var first = new TestRun(definition,new TestRunOptions { Resources=resources, Debugger=debugger });
        Assert.Equal(ResultRating.Pass,(await first.RunAsync().Within()).Rating); Assert.Equal(1,calls);
        Assert.Single(first.Events.OfType<NodeSkipped>());Assert.Empty(first.Events.OfType<Paused>());Assert.Empty(resources.Snapshot());
        child.Disabled=false;
        var second=new TestRun(definition,new TestRunOptions { Resources=resources, Debugger=debugger });
        Assert.Equal(ResultRating.Pass,(await second.RunAsync().Within()).Rating);Assert.Equal(4,calls);
        Assert.Single(second.Events.OfType<Paused>());Assert.Empty(resources.Snapshot());
    }
    [Fact]
    public async Task Disabled_steps_remain_unrated_and_targeted_execution_respects_disabled_ancestors()
    {
        var step=BlockCatalog.Default.Create(BlockCatalog.RaiseError,"skip");step.Disabled=true;
        var parent=Group("parent").Add(step);var definition=Test(Group("loop").Add(parent));
        var run=new TestRun(definition);Assert.Equal(ResultRating.NResult,(await run.RunAsync().Within()).Rating);
        Assert.Empty(run.Steps("skip"));Assert.False((await new TestRun(definition).ExecuteStepTargetedAsync("skip")).Executed);
        step.Disabled=false;parent.Disabled=true;
        Assert.False((await new TestRun(definition).ExecuteStepTargetedAsync("skip")).Executed);
        Assert.False((await new TestRun(definition).ExecuteGroupTargetedAsync("parent")).Executed);
        Assert.DoesNotContain(definition.EnabledNodes(),n=>n.Id=="skip" || n.Id=="parent");
    }
    [Fact]
    public void Settings_roundtrip_copy_diff_undo_and_default_legacy_format_are_preserved()
    {
        var step=BlockCatalog.Default.Create(BlockCatalog.Wait,"step");
        var project=new TestProject(Test(Group("loop").Add(step)));
        var serializer=new ProjectSerializer();var before=serializer.Clone(project);
        Assert.DoesNotContain("disabled",serializer.Serialize(project));Assert.DoesNotContain("breakpointRule",serializer.Serialize(project));
        var editor=new EditorViewModel(project) { Breakpoints=new DebugController() };
        editor.SelectedNode=editor.FindNode("step");var options=editor.Properties!.Options!;
        options.Enabled=false; Assert.True(step.Disabled);Assert.True(editor.SelectedNode!.IsInactive);Assert.True(editor.IsDirty);
        options.Card.Mode=BreakpointMode.FailOrError;options.Card.FirstHit="2";options.Card.Enabled=true;
        Assert.Equal(2,editor.Breakpoints!.GetBreakpointRule("step").FirstHit);
        var code=options.Code;code.Line="3";code.Mode=BreakpointMode.Expression;Assert.NotNull(code.Error);
        code.Condition="i == 3";code.FirstHit="2";code.Every="2";code.Enabled=true;
        Assert.True(editor.Breakpoints.HasCodeBreakpoint("step",3));
        var saved=(StepDefinition)serializer.Clone(project).Definition.Find("step")!;
        Assert.True(saved.Disabled);Assert.Equal(step.BreakpointRule,saved.BreakpointRule);Assert.Equal(step.CodeBreakpointRules[3],saved.CodeBreakpointRules[3]);
        var changes=ProjectDiff.Compare(before,project);Assert.Contains(changes,c=>c.Detail=="Deaktiviert");Assert.Contains(changes,c=>c.Detail=="Haltebedingungen geändert");
        code.Line="0";Assert.NotNull(code.Error);code.Line="3";Assert.Equal("2",code.FirstHit);
        code.FirstHit="bad";Assert.NotNull(code.Error);Assert.Equal(2,step.CodeBreakpointRules[3].FirstHit);
        code.FirstHit="1";code.Every="1";code.Mode=BreakpointMode.Always;code.Condition="";
        Assert.DoesNotContain(3,step.CodeBreakpointRules.Keys);
        options.Enabled=true;Assert.False(step.Disabled);
        editor.ToggleEnabledCommand.Execute(null);Assert.True(step.Disabled);
        editor.UndoCommand.Execute(null);Assert.False(editor.Project.Definition.Find("step")!.Disabled);
    }
    [Fact]
    public void System_sections_remain_enabled_and_disabled_conversion_moves_flag_to_call()
    {
        var child=Group("g").Add(BlockCatalog.Default.Create(BlockCatalog.Wait,"w"));child.Disabled=true;
        var definition=Test(Group("loop").Add(child));var editor=new EditorViewModel(new(definition));
        editor.SelectedNode=editor.FindNode("loop");editor.Properties!.Options!.Enabled=false;Assert.False(definition.Loop.Disabled);
        Assert.False(editor.ToggleEnabledCommand.CanExecute(null));definition.Init.Disabled=true;Assert.NotEmpty(definition.Validate());definition.Init.Disabled=false;
        var converted=GroupBlockConverter.Convert(definition,child,"block",BlockCatalog.Default);
        Assert.True(converted.Step.Disabled);Assert.False(converted.Block.Body.Disabled);
    }
    [Fact]
    public async Task Operator_view_and_recorded_dut_show_skips_without_fake_pass_measurements()
    {
        var off=BlockCatalog.Default.Create(BlockCatalog.RaiseError,"off");off.Disabled=true;
        var hidden=Group("hidden").Add(BlockCatalog.Default.Create(BlockCatalog.RaiseError,"never"));hidden.Disabled=true;
        var dut=Group("dut").Var("Kennung",VarType.String,"SN").Add(off,hidden,BlockCatalog.Default.Create(BlockCatalog.SetRating,"pass"));dut.IsDutRun=true;
        var definition=Test(Group("loop").Add(dut));var project=new TestProject(definition);
        var station=new StationViewModel("Test",()=>project,new ResourceManager());
        await station.StartSimulationAsync().Within();Assert.Null(station.Error);
        Assert.Equal(2,station.Protocol.Count(p=>p.Status=="Deaktiviert"));Assert.Equal(ResultRating.Pass,station.DutRating);
        var run=new TestRun(definition);var recorder=new Testumgebung.Persistence.Results.ResultRecorder(run);
        Testumgebung.Persistence.Results.ResultRun? recorded=null;recorder.RunRecorded+=r=>recorded=r;
        await run.RunAsync().Within();Assert.NotNull(recorded);
        Assert.Contains(recorded!.Events,e=>e.NodeId=="off" && e.Message=="Deaktiviert");
        Assert.DoesNotContain(recorded.Events,e=>e.NodeId=="off" && e.Kind==Testumgebung.Persistence.Results.ResultEventKind.Step);
    }

    [Fact]
    public void Disabled_lock_paths_do_not_create_phantom_cycles_and_deleted_code_breakpoints_are_removed()
    {
        var inner=Group("inner");inner.Resources.Add("B");var outer=Group("outer").Add(inner);outer.Resources.Add("A");
        var definition=Test(Group("loop").Add(outer));Assert.Single(LockOrderAnalysis.Edges(definition));
        inner.Disabled=true;Assert.Empty(LockOrderAnalysis.Edges(definition));inner.Disabled=false;outer.Disabled=true;Assert.Empty(LockOrderAnalysis.Edges(definition));
        var project=new TestProject(definition);var editor=new EditorViewModel(project){Breakpoints=new DebugController()};
        editor.Breakpoints.SetBreakpoint("inner");editor.Breakpoints.SetCodeBreakpoint("inner",3);
        editor.SelectedNode=editor.FindNode("outer");editor.DeleteCommand.Execute(null);
        Assert.False(editor.Breakpoints.HasBreakpoint("inner"));Assert.False(editor.Breakpoints.HasCodeBreakpoint("inner",3));
    }

}
