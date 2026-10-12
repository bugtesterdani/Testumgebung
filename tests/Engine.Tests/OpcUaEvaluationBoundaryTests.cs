using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class OpcUaEvaluationBoundaryTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var actual in new[] { "1", "1.5", "true", "false", "\"text\"" })
            foreach (var expected in new[] { "", "1", "1.5", "true", "false", "\"text\"" })
                foreach (var unit in new[] { "", "V" }) yield return [actual, expected, unit];
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Read_values_use_numeric_or_text_comparison_and_preserve_units(string actual, string expected, string unit)
    {
        var parameters = new Dictionary<string, string> { ["Ressource"] = "OPC", ["Aktion"] = "Lesen", ["Knoten"] = "ns=2;s=A", ["Sollwert"] = expected, ["Einheit"] = unit };
        var step = BlockCatalog.Default.Create(BusBlocks.OpcUaAccess, "read", "Read", parameters);
        var project = new TestProject(Test(Group("loop").Add(step)));
        project.SimulationComm.Add(new SimulatedComm("OPC", "opc", CommKind.OpcUa, "ns=2;s=A = " + actual));
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = SampleProjects.CreateSimulation(project).Registry });
        await run.RunAsync().Within();
        var result = Assert.Single(run.Steps("read"));
        // Communication text represents Boolean values as 1/0.
        var equivalent = actual == expected || (actual == "1" && expected == "true") || (actual == "true" && expected == "1");
        Assert.Equal(expected.Length == 0 ? ResultRating.NResult : equivalent ? ResultRating.Pass : ResultRating.Fail, result.Rating);
        Assert.Equal(unit.Length == 0 ? null : unit, result.Unit);
        if (result.Rating == ResultRating.Fail) Assert.Contains("erwartet", result.Message);
    }

    [Theory]
    [InlineData("", "2", ResultRating.Pass)]
    [InlineData("2", "", ResultRating.Fail)]
    [InlineData("0", "", ResultRating.Pass)]
    [InlineData("", "0", ResultRating.Fail)]
    [InlineData("0", "2", ResultRating.Pass)]
    public async Task One_sided_numeric_limits_rate_measurements(string low, string high, ResultRating expected)
    {
        var step = BlockCatalog.Default.Create(BusBlocks.OpcUaAccess, "read", "Read", new Dictionary<string, string>
        { ["Ressource"] = "OPC", ["Knoten"] = "ns=2;s=A", ["Untergrenze"] = low, ["Obergrenze"] = high, ["Einheit"] = "V" });
        var project = new TestProject(Test(Group("loop").Add(step)));
        project.SimulationComm.Add(new SimulatedComm("OPC", "opc", CommKind.OpcUa, "ns=2;s=A = 1"));
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = SampleProjects.CreateSimulation(project).Registry });
        await run.RunAsync().Within();
        Assert.Equal(expected, Assert.Single(run.Steps("read")).Rating);
    }
}
