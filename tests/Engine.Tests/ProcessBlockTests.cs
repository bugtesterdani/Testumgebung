using System.Diagnostics;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ProcessBlockTests
{
    private static string Probe => Path.Combine(AppContext.BaseDirectory, "fixtures", "process", "ProcessProbe.exe");
    private static Dictionary<string, string> Launch(string[] args) => new()
    {
        ["Datei"] = Probe, ["Argumente"] = JsonSerializer.Serialize(args), ["Zeitlimit"] = "20000",
        ["ProzessZiel"] = "Handle", ["PidZiel"] = "Pid", ["ExitcodeZiel"] = "Exit", ["StdoutZiel"] = "Output",
        ["StderrZiel"] = "Error", ["FertigZiel"] = "Done"
    };
    private static GroupDefinition Variables() => Group("loop").Var("Handle", VarType.String).Var("Pid", VarType.Int).Var("Exit", VarType.Int, -1L)
        .Var("Output", VarType.String).Var("Error", VarType.String).Var("Done", VarType.Bool);
    private static async Task<TestRun> Run(GroupDefinition loop)
    {
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within(); return run;
    }
    [Theory]
    [InlineData("LF")]
    [InlineData("CRLF")]
    public async Task Graphical_dialog_matches_signals_sends_commands_and_evaluates_exit(string ending)
    {
        var p = Launch(["dialog"]); p["Warten"] = "false";
        var loop = Variables().Var("Match", VarType.String).Var("Command", VarType.String, "hello");
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.WaitOutput, "ready", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}", ["Muster"] = "ready>" }));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Send, "send", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}", ["Text"] = "{Command}", ["Zeilenende"] = ending }));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.WaitOutput, "response", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}", ["Muster"] = @"value=\d+;hello;", ["Regex"] = "true", ["Stream"] = "stderr", ["TrefferZiel"] = "Match" }));
        string? match = null;
        loop.Add(new StepDefinition("inspect", "Inspect", new DelegateAction(ctx => { match = (string)ctx.Get("Match"); return Task.FromResult(StepOutcome.Unrated()); })));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Send, "finish", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}", ["Text"] = "done\n", ["Zeilenende"] = "Keines", ["Schliessen"] = "true" }));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Wait, "wait", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}" }));
        var run = await Run(loop);
        Assert.Equal("value=42;hello;", match);
        Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("response")).Rating);
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("wait")).Rating);
    }

    [Theory]
    [InlineData("0;7", ResultRating.Pass)]
    [InlineData("0", ResultRating.Fail)]
    [InlineData("", ResultRating.NResult)]
    [InlineData("not a number", ResultRating.Error)]
    public async Task Exit_codes_are_explicitly_evaluated_and_targets_receive_outputs(string expected, ResultRating rating)
    {
        var p = Launch(["echo", "7", "{Kennung}"]); p["ErwarteteExitcodes"] = expected; p["Protokoll"] = "true";
        var loop = Variables().Var("Kennung", VarType.String, "quote \" space & ü");
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p));
        object? exit = null, done = null; string? output = null;
        loop.Add(new StepDefinition("inspect", "Inspect", new DelegateAction(ctx => { exit = ctx.Get("Exit"); done = ctx.Get("Done"); output = (string)ctx.Get("Output"); return Task.FromResult(StepOutcome.Unrated()); })));
        var run = await Run(loop);
        Assert.Equal(rating, Assert.Single(run.Steps("start")).Rating);
        Assert.Equal(7L, exit); Assert.Equal(true, done); Assert.NotNull(output);
        using var json = JsonDocument.Parse(output!);
        Assert.Equal("quote \" space & ü", json.RootElement.GetProperty("Arguments")[0].GetString());
    }
    [Fact]
    public async Task Nonblocking_start_status_and_wait_use_the_same_handle()
    {
        var p = Launch(["sleep", "500"]); p["Warten"] = "false";
        var loop = Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Status, "status", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}", ["FertigZiel"] = "Done" }));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Wait, "wait", parameters: new Dictionary<string, string> { ["Prozess"] = "{Handle}", ["Zeitlimit"] = "20000", ["ExitcodeZiel"] = "Exit" }));
        var run = await Run(loop);
        Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("start")).Rating);
        Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("status")).Rating);
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("wait")).Rating);
        Assert.Equal(0, Assert.Single(run.Steps("wait")).Value);
    }
    [Fact]
    public async Task Stop_is_unrated_and_subsequent_wait_reports_termination()
    {
        var p = Launch(["sleep", "60000"]); p["Warten"] = "false";
        var loop = Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p));
        var handle = new Dictionary<string, string> { ["Prozess"] = "{Handle}" };
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Stop, "stop", parameters: handle));
        loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Wait, "wait", parameters: handle));
        var run = await Run(loop);
        Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("stop")).Rating);
        Assert.Equal(ResultRating.Error, Assert.Single(run.Steps("wait")).Rating);
    }
    [Theory]
    [InlineData("Warten", "false", "ProzessZiel", "")]
    [InlineData("Art", "DLL-Main", "Typ", "")]
    [InlineData("Zeitlimit", "0", "", "")]
    [InlineData("Zeitlimit", "NaN", "", "")]
    [InlineData("Ausgabelimit", "1.5", "", "")]
    [InlineData("Ausgabelimit", "1048577", "", "")]
    public void Invalid_factory_parameters_are_diagnosed(string key, string value, string second, string other)
    {
        var p = Launch(["sleep", "1"]); p[key] = value; if (second.Length != 0) p[second] = other;
        var step = BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p);
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(step));
    }
    [Theory]
    [InlineData("Argumente", "{}")]
    [InlineData("Argumente", "null")]
    [InlineData("Argumente", "[null]")]
    [InlineData("Umgebung", "[]")]
    [InlineData("Umgebung", "null")]
    [InlineData("Umgebung", "{\"A\":null}")]
    [InlineData("Datei", "relative.exe")]
    [InlineData("ProzessZiel", "Done")]
    [InlineData("ExitcodeZiel", "NotDeclared")]
    public async Task Invalid_dynamic_arguments_and_target_variables_fail_before_launch(string key, string value)
    {
        var p = Launch(["sleep", "1"]); p[key] = value;
        var run = await Run(Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p)));
        Assert.Equal(ResultRating.Error, Assert.Single(run.Steps("start")).Rating);
    }
    [Fact]
    public async Task Existing_project_format_preserves_process_parameters()
    {
        var loop = Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: Launch(["echo", "0"])));
        var serializer = new ProjectSerializer(); var project = new TestProject(Test(loop));
        var clone = serializer.Clone(project);
        Assert.Equal(serializer.Serialize(project), serializer.Serialize(clone));
        Assert.Empty(clone.Definition.Validate());
        await Run(clone.Definition.Loop);
    }
    [Fact]
    public async Task Targeted_start_cleans_bound_process_at_end_of_targeted_execution()
    {
        var p = Launch(["sleep", "60000"]); p["Warten"] = "false";
        var definition = Test(Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p)));
        var run = new TestRun(definition);
        var result = await run.ExecuteStepTargetedAsync("start").Within();
        Assert.Equal(ResultRating.NResult, result.Rating);
        Assert.DoesNotContain(run.Events.OfType<StepCompleted>(), step => step.Rating == ResultRating.Error);
    }
    [Fact]
    public async Task Python_variable_selection_and_explicit_override_are_resolved_by_the_process_block()
    {
        foreach (var explicitInterpreter in new[] { false, true })
        {
            var p = Launch(["{Handle}"]);
            p["Art"] = "Python";
            if (explicitInterpreter) p["Python"] = Probe;
            var loop = Variables().Var("PythonVenv", VarType.String, explicitInterpreter ? "not-present" : Probe);
            loop.Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p));
            var run = await Run(loop);
            Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("start")).Rating);
        }
    }
    [Fact]
    public async Task Parallel_test_runs_do_not_mix_process_arguments_or_exit_results()
    {
        async Task<TestRun> Execute(int code)
        {
            var p = Launch(["echo", code.ToString(), "{Serial}"]); p["ErwarteteExitcodes"] = code.ToString();
            return await Run(Variables().Var("Serial", VarType.String, "SN-" + code).Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p)));
        }
        var runs = await Task.WhenAll(Execute(3), Execute(4));
        Assert.Equal(3, Assert.Single(runs[0].Steps("start")).Value);
        Assert.Equal(4, Assert.Single(runs[1].Steps("start")).Value);
        Assert.All(runs, run => Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("start")).Rating));
    }
    [Theory]
    [InlineData("Gebunden", ResultRating.NResult)]
    [InlineData("Unabhängig", ResultRating.NResult)]
    public async Task No_wait_is_always_unrated_regardless_of_fast_exit(string mode, ResultRating expected)
    {
        var p = Launch(["echo", "0"]); p["Warten"] = "false"; p["Modus"] = mode;
        var run = await Run(Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p)));
        Assert.Equal(expected, Assert.Single(run.Steps("start")).Rating);
    }
    [Fact]
    public async Task DLL_host_exception_cannot_pass_even_when_253_is_allowed()
    {
        var p = Launch(["throw"]); p["Art"] = "DLL-Main"; p["Datei"] = Path.ChangeExtension(Probe, ".dll");
        p["Typ"] = "ProcessProbe.Methods"; p["ErwarteteExitcodes"] = "253";
        var run = await Run(Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p)));
        Assert.Equal(ResultRating.Error, Assert.Single(run.Steps("start")).Rating);
        Assert.Contains("fixture invocation failed", Assert.Single(run.Steps("start")).Message);
    }
    [Fact]
    public async Task Shipped_python_example_runs_with_its_configured_file_and_serial_number()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var serializer = new ProjectSerializer();
        var project = serializer.Load(Path.Combine(root.FullName, "docs/beispiele/prozess-python.tproj.json"));
        var index = project.Definition.Root.Variables.FindIndex(v => v.Name == "SkriptDatei");
        project.Definition.Root.Variables[index] = new VariableDefinition("SkriptDatei", VarType.String, Path.Combine(root.FullName, "docs/beispiele/prozess-demo.py"));
        project.Definition.Loop.Repetition = Repetition.Once;
        var op = new ScriptedOperator().Answer("Seriennummer erfassen", _ => "SN-Python");
        var run = new TestRun(project.Definition, new TestRunOptions { Operator = op });
        await run.RunAsync().Within();
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("prozess-start")).Rating);
        Assert.Equal("SN-Python", Assert.Single(run.Events.OfType<DutRunCompleted>()).DutId);
    }
    [Fact]
    public async Task Worker_abort_ends_a_waiting_bound_process_and_runs_finalization()
    {
        var p = Launch(["sleep", "60000"]);
        var loop = Variables().Add(BlockCatalog.Default.Create(ProcessBlocks.Start, "start", parameters: p));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalization = Group("fin").Add(new StepDefinition("cleanup", "Cleanup", new DelegateAction(_ => { done.SetResult(); return Task.FromResult(StepOutcome.Unrated()); })));
        var run = new TestRun(Test(loop, finalization: finalization));
        var running = run.RunAsync();
        await run.WaitFor<StepStarted>(s => s.NodeId == "start");
        run.RequestAbort();
        var status = await running.Within(); await done.Task.Within();
        Assert.True(status.OperatorAbort); Assert.True(Assert.Single(run.Steps("start")).Cancelled);
    }
}
