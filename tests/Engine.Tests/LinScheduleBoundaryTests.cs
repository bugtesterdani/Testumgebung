using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Hardware;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class LinScheduleBoundaryTests
{
    private static StepDefinition S(string type, string id, params (string Key, string Value)[] p)
        => BlockCatalog.Default.Create(type, id, id, p.ToDictionary(x => x.Key, x => x.Value));

    private static async Task<(TestRun Run, SimulationAdapter Sim)> RunAsync(TestProject project)
    {
        var (sim, registry) = SampleProjects.CreateSimulation(project);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry });
        await run.RunAsync().Within();
        return (run, sim);
    }
    [Theory]
    [InlineData("", false)]
    [InlineData("0x21:0 0", false)]
    [InlineData("0x21:9 0", false)]
    [InlineData("0x40 0", false)]
    [InlineData("0x21:4", true)]
    [InlineData("0x21:4 ignored", true)]
    [InlineData("0x21:4 -1", true)]
    [InlineData("0x21:4 0", true)]
    [InlineData("0x21=01", true)]
    [InlineData("0x21=01 FF", true)]
    [InlineData("0x21=01 02 0", true)]
    public async Task Schedule_handles_default_gaps_zero_gaps_and_invalid_frame_lengths(string table, bool valid)
    {
        var project = new TestProject(Test(Group("loop").Add(S(BusBlocks.LinSchedule, "plan", ("Ressource", "lin"), ("Tabelle", table), ("Durchlaeufe", "2")))));
        project.SimulationComm.Add(new SimulatedComm("lin", "lin", CommKind.Lin, "0x21 => 01 02 03 04"));
        var (run, _) = await RunAsync(project);
        var result = run.Steps("plan").Single();
        Assert.Equal(valid ? ResultRating.NResult : ResultRating.Error, result.Rating);
        if (valid) { Assert.Equal(2, Convert.ToInt32(result.Value)); Assert.Contains("2 Frames", result.Message); }
    }

    [Theory]
    [InlineData("FH_Status", true)]
    [InlineData("FH_Status:4", true)]
    [InlineData("ETF_Status", true)]
    [InlineData("not-found", false)]
    [InlineData("FH_Status:invalid", false)]
    public async Task Schedule_resolves_database_frame_names_and_reports_missing_names(string frame, bool valid)
    {
        var project = new TestProject(Test(Group("loop").Add(S(BusBlocks.LinSchedule, "plan", ("Ressource", "lin"), ("Datenbank", "db"), ("Tabelle", frame + " 0")))));
        project.Definition.BusDatabases.Add(new BusDatabaseDefinition("db", BusDatabaseFormat.Ldf, BusTests.Ldf, "fixture.ldf"));
        project.SimulationComm.Add(new SimulatedComm("lin", "lin", CommKind.Lin, "0x21 => 01 02 03 04"));
        var (run, _) = await RunAsync(project);
        Assert.Equal(valid ? ResultRating.NResult : ResultRating.Error, run.Steps("plan").Single().Rating);
    }
}
