using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class LiveGroupResultBoundaryTests
{
    [Fact]
    public async Task Exit_sees_committed_parent_rating_and_keeps_its_own_result_separate()
    {
        string? parent = null, own = null;
        var group = Group("g").Add(Steps.Rate("fail", ResultRating.Fail));
        group.ExitGroup = Group("exit").Add(Steps.Code("read", "read", ctx =>
        {
            parent = (string)ctx.Get("../GruppenErgebnis"); own = (string)ctx.Get("GruppenErgebnis");
            return StepOutcome.Unrated();
        }), Steps.Rate("error", ResultRating.Error));
        var run = new TestRun(Test(Group("loop").Add(group)));
        await run.RunAsync().Within();
        Assert.Equal("FAIL", parent); Assert.Equal("NRESULT", own);
        Assert.Equal(ResultRating.Error, run.GroupResult("g").Rating);
    }

    [Theory]
    [InlineData(RepeatMode.While, "GruppenErgebnis != \"PASS\"")]
    [InlineData(RepeatMode.Until, "GruppenErgebnis == \"PASS\"")]
    public async Task Repeat_expressions_read_committed_live_rating(RepeatMode mode, string condition)
    {
        var group = Group("g").Add(Steps.Rate("pass", ResultRating.Pass));
        group.Repetition = new Repetition(mode, Expression: condition);
        var run = new TestRun(Test(Group("loop").Add(group)));
        await run.RunAsync().Within(); Assert.Single(run.Steps("pass"));
    }

    [Theory]
    [InlineData("C#", "return GruppenErgebnis == \"NRESULT\" ? NResult() : Error(\"falsch\");")]
    [InlineData("Python", "return NResult() if GruppenErgebnis == \"NRESULT\" else Error(\"falsch\")")]
    [InlineData("C++", "return GruppenErgebnis == \"NRESULT\" ? NResult() : Error(\"falsch\");")]
    public async Task Editor_build_knows_the_implicit_string_variable(string language, string source)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Code, "code", parameters: new Dictionary<string, string>
        { ["Sprache"] = language, ["Quelltext"] = source });
        var editor = new EditorViewModel(new TestProject(Test(Group("loop").Add(step)))) { CodeRuntime = CodeBlockTests.SharedRuntime.Value };
        editor.SelectedNode = editor.FindNode("code");
        var tools = ((StepPropertiesViewModel)editor.Properties!).CodeTools!;
        Assert.Contains(CodeBlockConverter.VisibleVariables(editor.Project.Definition, step.Id), v => v.Name == Scope.GroupResultVariable && v.Type == VarType.String);
        await tools.BuildAsync(); Assert.True(tools.BuildOk, tools.BuildStatus);
    }

    [Fact]
    public void Conversion_uses_input_for_code_and_local_system_value_for_graphical_groups()
    {
        var code = BlockCatalog.Default.Create(BlockCatalog.Code, "code", parameters: new Dictionary<string, string>
        { ["Quelltext"] = "return GruppenErgebnis == \"NRESULT\" ? Pass() : Fail();" });
        var group = Group("group").Add(code); var definition = Test(Group("loop").Add(group));
        var visible = CodeBlockConverter.VisibleVariables(definition, code.Id);
        var converted = CodeBlockConverter.Convert(code, visible, "lib", BlockCatalog.Default);
        var port = Assert.Single(converted.Block.Ports);
        Assert.Equal(Scope.GroupResultVariable, port.Name); Assert.Equal(PortDirection.Input, port.Direction);
        Assert.Equal(Scope.GroupResultVariable, converted.Step.Parameters[BlockCatalog.InputBindingPrefix + Scope.GroupResultVariable]);
        Assert.DoesNotContain(GroupBlockConverter.Analyze(definition, group, BlockCatalog.Default).Ports, p => p.Name == Scope.GroupResultVariable);
        code.Parameters["Quelltext"] = "GruppenErgebnis = \"PASS\"; return Pass();";
        Assert.Throws<InvalidOperationException>(() => CodeBlockConverter.Convert(code, visible, "invalid", BlockCatalog.Default));
        var row = new VariableRowViewModel(new VariableDefinition(Scope.GroupResultVariable, VarType.String), () => { });
        Assert.Null(row.ToDefinition()); Assert.Contains("Systemvariable", row.Error);
        Assert.Throws<InvalidOperationException>(() => new Scope("manual", null).Define(Scope.GroupResultVariable, VarType.String));
        var graphical = new GroupBlockDefinition("graphical", "Graphisch", Group("body"));
        graphical.Ports.Add(new CodePort(Scope.GroupResultVariable, VarType.String, PortDirection.Input));
        definition.GroupLibrary.Add(graphical);
        Assert.Contains(definition.Validate(), d => d.Contains("kein grafischer Portname"));
    }

    [Theory]
    [InlineData("C#", "GruppenErgebnis = \"PASS\"; return Pass();")]
    [InlineData("Python", "GruppenErgebnis = \"PASS\"\nreturn Pass()")]
    [InlineData("C++", "GruppenErgebnis = \"PASS\"; return Pass();")]
    public async Task Changed_worker_snapshot_cannot_be_written_back_to_system_result(string language, string source)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Code, "code", parameters: new Dictionary<string, string>
        { ["Sprache"] = language, ["Quelltext"] = source, ["Timeout"] = "15" });
        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        await run.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ResultRating.Error, Assert.Single(run.Steps("code")).Rating);
        Assert.Contains("schreibgeschützt", Assert.Single(run.Steps("code")).Message);
    }

    [Fact]
    public async Task Debugger_does_not_offer_or_apply_writes_to_system_value()
    {
        var debug = new DebugController(); debug.SetBreakpoint("pass");
        var run = new TestRun(Test(Group("loop").Add(Steps.Rate("pass", ResultRating.Pass))), new TestRunOptions { Debugger = debug });
        debug.Paused += _ =>
        {
            Assert.True(debug.ActiveVariables().Single(v => v.LookupPath == Scope.GroupResultVariable).IsReadOnly);
            Assert.Throws<InvalidOperationException>(() => debug.SetVariable(Scope.GroupResultVariable, "PASS", "test"));
            debug.Continue();
        };
        await run.RunAsync().Within();
        Assert.DoesNotContain(run.Events.OfType<DevelopmentEvent>(), e => e.Kind == "Variable geändert");
    }

    [Fact]
    public async Task A_long_running_codeblock_reads_new_commits_through_host_without_writing_back_its_old_snapshot()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var code = BlockCatalog.Default.Create(BlockCatalog.Code, "reader", parameters: new Dictionary<string, string>
        {
            ["Quelltext"] = "Log(\"ready\"); while (Var<string>(\"GruppenErgebnis\") != \"FAIL\") Sleep(5); return NResult(GruppenErgebnis);",
            ["Timeout"] = "10"
        });
        var rate = Steps.Code("fail", "fail", async _ => { await ready.Task.Within(); return StepOutcome.Fail(); });
        var group = new GroupDefinition("parallel", "parallel") { Parallel = true }.Add(code, rate);
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        run.EventRaised += e => { if (e is Diagnostic { NodeId: "reader", Message: "ready" }) ready.TrySetResult(); };
        await run.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var step = Assert.Single(run.Steps("reader"));
        Assert.Equal(ResultRating.NResult, step.Rating); Assert.Equal("NRESULT", step.Value);
        Assert.Equal(ResultRating.Fail, run.GroupResult("parallel").Rating);
    }

    [Fact]
    public void Debugger_row_marks_computed_variable_as_read_only()
    {
        var shell = new ShellViewModel();
        var row = new DebugVariableRowViewModel(shell.SelectedStation.Debug,
            new DebugVariable(Scope.GroupResultVariable, Scope.GroupResultVariable, VarType.String, "NRESULT", "loop", IsReadOnly: true));
        Assert.True(row.IsReadOnly); Assert.False(row.CommitCommand.CanExecute(null));
        row.ValueText = "PASS"; row.Commit(); Assert.Null(row.Error);
        Assert.Equal("Schreibgeschützt", row.StatusText);
    }
}
