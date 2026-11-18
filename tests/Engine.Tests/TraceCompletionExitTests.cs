using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class TraceCompletionExitTests
{
    [Theory]
    [InlineData("timed", "5", ResultRating.Pass)]
    [InlineData("timed", "11", ResultRating.Fail)]
    [InlineData("timed", "missing", ResultRating.Error)]
    [InlineData("point", "5", ResultRating.Pass)]
    [InlineData("point", "11", ResultRating.Fail)]
    [InlineData("point", "missing", ResultRating.Error)]
    [InlineData("stop", "5", ResultRating.Pass)]
    [InlineData("stop", "11", ResultRating.Fail)]
    [InlineData("stop", "missing", ResultRating.Error)]
    public async Task All_outcomes_skip_regular_steps_and_execute_cleanup(string mode, string value, ResultRating rating)
    {
        var settings = new Dictionary<string, string>
        {
            [TraceBlocks.EvaluationParameter] = TraceBlocks.FormulaBand,
            [TraceBlocks.LowerFormulaParameter] = "0",
            [TraceBlocks.UpperFormulaParameter] = "10",
            ["Dauer"] = mode == "point" ? "1" : "0",
            [TraceBlocks.ExitDutOnEndParameter] = "true"
        };
        var group = Group("measurement");
        if (mode == "timed")
        {
            settings["Dauer"] = "1";
            settings["Werte"] = value;
            group.Add(BlockCatalog.Default.Create(TraceBlocks.Record, "finish", parameters: settings));
        }
        else
        {
            settings.Remove(TraceBlocks.ExitDutOnEndParameter);
            group.Add(BlockCatalog.Default.Create(TraceBlocks.Start, "begin", parameters: settings));
            group.Add(Block(TraceBlocks.Point, "first", ("Zeit", "0"), ("Werte", "5")));
            group.Add(Block(TraceBlocks.Point, mode == "point" ? "finish" : "last", ("Zeit", "1"),
                ("Werte", value == "missing" && mode == "stop" ? "5" : value),
                (TraceBlocks.ExitDutOnEndParameter, mode == "point" ? "true" : "false")));
            if (mode == "stop") group.Add(Block(TraceBlocks.Stop, "finish",
                ("ErgebnisZiel", value == "missing" ? "undefined" : ""), (TraceBlocks.ExitDutOnEndParameter, "true")));
        }
        group.Add(Block(BlockCatalog.SetRating, "must-skip", ("Bewertung", "ERROR")));
        group.ExitGroup = Group("inner-exit").Add(Block(BlockCatalog.SetRating, "inner-cleanup", ("Bewertung", "NRESULT")));
        var dut = Group("dut").Var("Kennung", VarType.String, "PSU");
        dut.IsDutRun = true;
        dut.Add(group, Block(BlockCatalog.SetRating, "must-also-skip", ("Bewertung", "ERROR")));
        dut.ExitGroup = Group("dut-exit").Add(Block(BlockCatalog.SetRating, "dut-cleanup", ("Bewertung", "NRESULT")));
        var run = new TestRun(Test(Group("loop").Add(dut)), new TestRunOptions { Clock = new VirtualClock() });

        Assert.Equal(rating, (await run.RunAsync().Within()).Rating);
        Assert.Equal(rating, Assert.Single(run.Steps("finish")).Rating);
        Assert.Empty(run.Steps("must-skip"));
        Assert.Empty(run.Steps("must-also-skip"));
        Assert.Equal(new[] { "inner-cleanup", "dut-cleanup" },
            run.Events.OfType<StepCompleted>().Where(step => step.NodeId.EndsWith("cleanup")).Select(step => step.NodeId));
    }

    [Fact]
    public async Task A_failed_point_in_a_running_recording_does_not_exit_early()
    {
        var dut = Group("dut").Var("Kennung", VarType.String, "PSU");
        dut.IsDutRun = true;
        dut.Add(
            Block(TraceBlocks.Start, "begin", (TraceBlocks.EvaluationParameter, TraceBlocks.FormulaBand),
                (TraceBlocks.LowerFormulaParameter, "0"), (TraceBlocks.UpperFormulaParameter, "10"), ("Dauer", "1")),
            Block(TraceBlocks.Point, "first", ("Zeit", "0"), ("Werte", "11"), (TraceBlocks.ExitDutOnEndParameter, "true")),
            Block(BlockCatalog.SetRating, "between", ("Bewertung", "NRESULT")),
            Block(TraceBlocks.Point, "last", ("Zeit", "1"), ("Werte", "5"), (TraceBlocks.ExitDutOnEndParameter, "true")),
            Block(BlockCatalog.SetRating, "skip", ("Bewertung", "ERROR")));
        dut.ExitGroup = Group("exit").Add(Block(BlockCatalog.SetRating, "cleanup", ("Bewertung", "NRESULT")));
        var run = new TestRun(Test(Group("loop").Add(dut)));
        Assert.Equal(ResultRating.Fail, (await run.RunAsync().Within()).Rating);
        Assert.Single(run.Steps("between"));
        Assert.Empty(run.Steps("skip"));
        Assert.Single(run.Steps("cleanup"));
    }

    private static StepDefinition Block(string type, string id, params (string Key, string Value)[] values)
        => BlockCatalog.Default.Create(type, id, parameters: values.ToDictionary(entry => entry.Key, entry => entry.Value));
}
