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
    [InlineData(false)]
    [InlineData(true)]
    public async Task Code_fail_is_cleared_from_live_display_before_the_next_reset_iteration(bool library)
    {
        using var runtime = new CodeRuntime();
        const string source = "Versuch++; return Versuch == 1 ? Fail() : Pass();";
        var code = BlockCatalog.Default.Create(library ? BlockCatalog.LibraryBlock : BlockCatalog.Code, "code", "Code", library
            ? new Dictionary<string, string> { ["Baustein"] = "lib", ["in.Versuch"] = "Versuch" }
            : new Dictionary<string, string> { ["Sprache"] = "C#", ["Quelltext"] = source });
        var checkpoint = BlockCatalog.Default.Create(BlockCatalog.Operator, "checkpoint", "Checkpoint", new Dictionary<string, string> { ["Text"] = "{GruppenErgebnis}" });
        var group = Group("repeat").Var("Versuch", VarType.Int, 0).Add(checkpoint, code);
        group.Repetition = Repetition.Times(2);
        group.ResultBeforeRepeat = RepeatResultPolicy.ResetToNResult;
        var project = new TestProject(Test(Group("loop").Add(group)));
        if (library)
        {
            var block = new CodeBlockDefinition("lib", "Library") { Language = CodeLanguage.CSharp, Source = source };
            block.Ports.Add(new CodePort("Versuch", VarType.Int, PortDirection.InOut));
            project.Definition.Library.Add(block);
        }
        var ui = new QueueDispatcher();
        var station = new StationViewModel("Test", () => project, new ResourceManager(), ui, clock: new VirtualClock(), codeRuntime: runtime);
        var run = station.StartAsync();
        try
        {
            await ui.Until(() => station.ActiveContent is { IsAwaiting: true }, "first iteration");
            var first = station.ActiveContent!;
            first.Confirm();
            await ui.Until(() => station.ActiveContent is { IsAwaiting: true } current && current.DisplayId != first.DisplayId, "second iteration");
            Assert.Equal("NRESULT", station.ActiveContent!.Text);
            Assert.Equal(ResultRating.NResult, station.DutRating);
            Assert.Equal(ResultRating.NResult, Assert.Single(station.Overview).Rating);
            station.ActiveContent.Confirm();
            await ui.Until(() => run.IsCompleted, "completed");
            await run;
            ui.Pump();
            Assert.Equal(ResultRating.Pass, station.LastStatus!.Value.Rating);
            Assert.Equal(ResultRating.Pass, station.DutRating);
        }
        finally
        {
            if (!run.IsCompleted) station.RequestAbort();
            await ui.Until(() => run.IsCompleted, "cleanup");
            await run;
        }
    }
}
