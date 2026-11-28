using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class RepeatResultDisplayTests
{
    [Theory]
    [InlineData(false, "C#", true, false)]
    [InlineData(true, "C#", true, false)]
    [InlineData(false, "Python", true, false)]
    [InlineData(true, "Python", true, false)]
    [InlineData(false, "C++", true, false)]
    [InlineData(true, "C++", true, false)]
    [InlineData(false, "C#", false, false)]
    [InlineData(true, "C#", false, false)]
    [InlineData(false, "C#", true, true)]
    [InlineData(true, "C#", true, true)]
    [InlineData(false, "C#", true, false, true)]
    [InlineData(true, "C#", true, false, true)]
    [InlineData(false, "C#", true, true, true)]
    [InlineData(true, "C#", false, false, true)]
    public async Task Code_fail_is_cleared_from_live_display_before_the_next_reset_iteration(bool library, string language, bool reset, bool outsideFailure, bool dut = false)
    {
        using var runtime = new CodeRuntime();
        var source = language == "Python" ? "Versuch += 1\nreturn Fail() if Versuch == 1 else Pass()" : "Versuch++; return Versuch == 1 ? Fail() : Pass();";
        var code = BlockCatalog.Default.Create(library ? BlockCatalog.LibraryBlock : BlockCatalog.Code, "code", "Code", library
            ? new Dictionary<string, string> { ["Baustein"] = "lib", ["in.Versuch"] = "Versuch" }
            : new Dictionary<string, string> { ["Sprache"] = language, ["Quelltext"] = source });
        var checkpoint = BlockCatalog.Default.Create(BlockCatalog.Operator, "checkpoint", "Checkpoint", new Dictionary<string, string> { ["Text"] = "{GruppenErgebnis}" });
        var group = Group("repeat").Var("Versuch", VarType.Int, 0).Add(checkpoint, code);
        group.Repetition = Repetition.Times(2);
        group.ResultBeforeRepeat = reset ? RepeatResultPolicy.ResetToNResult : RepeatResultPolicy.Keep;
        var loop = Group("loop");
        var container = dut ? Group("dut").Var("Kennung", VarType.String, "DUT-1") : loop;
        if (dut) { container.IsDutRun = true; loop.Add(container); }
        container.Add(group);
        var project = new TestProject(Test(loop));
        if (outsideFailure)
            container.Children.Insert(0, BlockCatalog.Default.Create(BlockCatalog.Code, "outside", "Outside failure",
                new Dictionary<string, string> { ["Sprache"] = "C#", ["Quelltext"] = "return Fail();" }));
        if (library)
        {
            var block = new CodeBlockDefinition("lib", "Library") { Language = BlockCatalog.ParseLanguage(language), Source = source };
            block.Ports.Add(new CodePort("Versuch", VarType.Int, PortDirection.InOut));
            project.Definition.Library.Add(block);
        }
        var ui = new QueueDispatcher();
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, clock: new VirtualClock(), codeRuntime: runtime);
        var run = station.StartAsync();
        try
        {
            await Until(ui, () => station.ActiveContent is { IsAwaiting: true }, "first iteration");
            var first = station.ActiveContent!;
            first.Confirm();
            await Until(ui, () => station.ActiveContent is { IsAwaiting: true } current && current.DisplayId != first.DisplayId, "second iteration");
            Assert.Equal(reset ? "NRESULT" : "FAIL", station.ActiveContent!.Text);
            Assert.Equal(reset && !outsideFailure ? ResultRating.NResult : ResultRating.Fail, station.DutRating);
            Assert.Equal(reset ? ResultRating.NResult : ResultRating.Fail, station.Overview.Single(row => row.NodeId == "repeat").Rating);
            station.ActiveContent.Confirm();
            await Until(ui, () => run.IsCompleted, "completed");
            await run;
            ui.Pump();
            Assert.Equal(reset && !outsideFailure ? ResultRating.Pass : ResultRating.Fail, station.LastStatus!.Value.Rating);
            Assert.Equal(reset && !outsideFailure ? ResultRating.Pass : ResultRating.Fail, station.DutRating);
            Assert.Equal(new[] { ResultRating.Fail, ResultRating.Pass }, station.Run!.Steps("code").Select(step => step.Rating));
            Assert.Equal(reset ? ResultRating.Pass : ResultRating.Fail, station.Run!.GroupResult("repeat").Rating);
            var entered = station.Run!.Events.OfType<GroupEntered>().Single(item => item.NodeId == "repeat");
            Assert.All(station.Run!.Steps("code"), item => Assert.Equal(entered.ScopeId, item.GroupScopeId));
            Assert.Equal(new[] { false, reset }, station.Run!.Events.OfType<GroupIterationStarted>().Where(item => item.NodeId == "repeat").Select(item => item.ResultReset));
        }
        finally
        {
            if (!run.IsCompleted) station.RequestAbort();
            await Until(ui, () => run.IsCompleted, "cleanup");
            await run;
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_of_the_loop_or_dut_itself_clears_direct_step_rows(bool dut)
    {
        using var runtime = new CodeRuntime();
        var checkpoint = BlockCatalog.Default.Create(BlockCatalog.Operator, "checkpoint", "Checkpoint", new Dictionary<string, string> { ["Text"] = "{GruppenErgebnis}" });
        var code = BlockCatalog.Default.Create(BlockCatalog.Code, "code", "Code", new Dictionary<string, string>
            { ["Sprache"] = "C#", ["Quelltext"] = "Versuch++; return Versuch == 1 ? Fail() : Pass();" });
        var repeat = Group("repeat").Var("Versuch", VarType.Int, 0).Var("Kennung", VarType.String, "DUT-1").Add(checkpoint, code);
        repeat.IsDutRun = dut;
        repeat.Repetition = Repetition.Times(2);
        repeat.ResultBeforeRepeat = RepeatResultPolicy.ResetToNResult;
        var project = new TestProject(Test(dut ? Group("loop").Add(repeat) : repeat));
        var ui = new QueueDispatcher();
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, clock: new VirtualClock(), codeRuntime: runtime);
        var run = station.StartAsync();
        try
        {
            await Until(ui, () => station.ActiveContent is { IsAwaiting: true }, "first attempt");
            var first = station.ActiveContent!;
            first.Confirm();
            await Until(ui, () => station.ActiveContent is { IsAwaiting: true } current && current.DisplayId != first.DisplayId, "second attempt");
            Assert.Equal(ResultRating.NResult, station.DutRating);
            var row = station.Overview.Single(item => item.NodeId == "code");
            Assert.Equal(ResultRating.NResult, row.Rating);
            Assert.Equal(ItemState.Pending, row.State);
            station.ActiveContent!.Confirm();
            await Until(ui, () => run.IsCompleted, "finished");
            await run;
            ui.Pump();
            Assert.Equal(ResultRating.Pass, station.DutRating);
        }
        finally
        {
            if (!run.IsCompleted) station.RequestAbort();
            await Until(ui, () => run.IsCompleted, "cleanup");
            await run;
        }
    }

    private static async Task Until(QueueDispatcher ui, Func<bool> condition, string label)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            ui.Pump();
            if (condition()) return;
            try { await Task.Delay(5, timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException(label); }
        }
    }
}
