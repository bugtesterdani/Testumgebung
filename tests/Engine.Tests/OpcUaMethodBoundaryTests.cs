using Testumgebung.Contracts;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class OpcUaMethodBoundaryTests
{
    [Theory]
    [InlineData("\"text\"", "text")]
    [InlineData("TRUE", "1")]
    [InlineData("FALSE", "0")]
    [InlineData("17", "17")]
    [InlineData("1.25", "1.25")]
    [InlineData("unquoted", "unquoted")]
    [InlineData("\"", "\"")]
    [InlineData("\"open", "\"open")]
    [InlineData("close\"", "close\"")]
    [InlineData("1;false;\"text\"", "text; 0; 1")]
    [InlineData("", "")]
    public async Task Method_arguments_preserve_types_and_multiple_outputs(string arguments, string expected)
    {
        var call = BlockCatalog.Default.Create(BusBlocks.OpcUaAccess, "call", "Call", new Dictionary<string, string>
        { ["Ressource"] = "OPC", ["Aktion"] = "Methode aufrufen", ["Objekt"] = "ns=2;s=O", ["Methode"] = "ns=2;s=M", ["Argumente"] = arguments, ["Ziel"] = "Output" });
        var read = new Testumgebung.Domain.Definitions.StepDefinition("check", "Check", new DelegateAction(c =>
        {
            Assert.Equal(expected, c.Get("Output")); return Task.FromResult(Testumgebung.Domain.Definitions.StepOutcome.Pass());
        }));
        var project = new TestProject(Test(Group("loop").Var("Output", VarType.String).Add(call, read)));
        project.SimulationComm.Add(new SimulatedComm("OPC", "opc", CommKind.OpcUa, "methode ns=2;s=O ns=2;s=M => spiegeln"));
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = SampleProjects.CreateSimulation(project).Registry });
        await run.RunAsync().Within(); Assert.Equal(ResultRating.NResult, Assert.Single(run.Steps("call")).Rating); Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("check")).Rating);
    }
}
