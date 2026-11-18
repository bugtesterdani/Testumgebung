using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class TraceEndChoiceTests
{
    [Theory]
    [InlineData(false, false, false, TraceEndOptions.Continue)]
    [InlineData(true, false, false, TraceEndOptions.Early)]
    [InlineData(false, true, false, TraceEndOptions.Error)]
    [InlineData(true, true, false, TraceEndOptions.EarlyOrError)]
    [InlineData(false, false, true, TraceEndOptions.Always)]
    [InlineData(true, false, true, TraceEndOptions.Always)]
    [InlineData(false, true, true, TraceEndOptions.Always)]
    [InlineData(true, true, true, TraceEndOptions.Always)]
    public void Legacy_flags_migrate_when_loading_without_losing_saved_flags(bool early, bool error, bool always, string expected)
    {
        var step = BlockCatalog.Default.Create(TraceBlocks.Point, "point", parameters: new Dictionary<string, string>
        {
            [TraceBlocks.ExitDutOnEarlyStopParameter] = early.ToString(),
            [BlockCatalog.ExitDutOnErrorParameter] = error.ToString(),
            [TraceBlocks.ExitDutOnEndParameter] = always.ToString()
        });
        step.Parameters.Remove(TraceEndOptions.Parameter);
        var copy = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(step))));
        var loaded = Assert.IsType<StepDefinition>(copy.Definition.Loop.Children.Single());
        Assert.Equal(expected, loaded.Parameters[TraceEndOptions.Parameter]);
        Assert.Equal(step.Parameters[TraceBlocks.ExitDutOnEndParameter], loaded.Parameters[TraceBlocks.ExitDutOnEndParameter]);
    }

    public static IEnumerable<object[]> Outcomes()
    {
        var modes = new[] { TraceEndOptions.Continue, TraceEndOptions.Early, TraceEndOptions.Error, TraceEndOptions.EarlyOrError, TraceEndOptions.Always };
        bool[][] exit = [[false, false, false, false], [false, false, false, true], [false, false, true, false], [false, false, true, true], [true, true, true, true]];
        for (var mode = 0; mode < modes.Length; mode++)
            for (var outcome = 0; outcome < 4; outcome++) yield return [modes[mode], outcome, exit[mode][outcome]];
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Choice_controls_actual_flow_for_pass_fail_error_and_early_end(string mode, int outcome, bool exits)
    {
        var capture = BlockCatalog.Default.Create(TraceBlocks.Record, "capture", parameters: new Dictionary<string, string>
        {
            [TraceEndOptions.Parameter] = mode, [TraceBlocks.EvaluationParameter] = TraceBlocks.FormulaBand,
            [TraceBlocks.LowerFormulaParameter] = "0", [TraceBlocks.UpperFormulaParameter] = "10",
            ["Dauer"] = "0.1", ["Intervall"] = "0.1", ["Werte"] = outcome switch { 0 => "5", 2 => "missing", _ => "99" },
            [TraceBlocks.StopEnabledParameter] = outcome == 3 ? "true" : "false",
            [TraceBlocks.StopMarginModeParameter] = TraceBlocks.AbsoluteMargin, [TraceBlocks.AbsoluteMarginParameter] = "0"
        });
        var dut = Group("dut").Var("Kennung", VarType.String, "PSU");
        dut.IsDutRun = true;
        dut.Add(capture, BlockCatalog.Default.Create(BlockCatalog.SetRating, "following", parameters: new Dictionary<string, string> { ["Bewertung"] = "NRESULT" }));
        dut.ExitGroup = Group("exit").Add(BlockCatalog.Default.Create(BlockCatalog.SetRating, "cleanup", parameters: new Dictionary<string, string> { ["Bewertung"] = "NRESULT" }));
        var run = new TestRun(Test(Group("loop").Add(dut)), new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();
        Assert.Equal(exits ? 0 : 1, run.Steps("following").Count());
        Assert.Single(run.Steps("cleanup"));
        Assert.Equal(outcome == 0 ? ResultRating.Pass : outcome == 2 ? ResultRating.Error : ResultRating.Fail, Assert.Single(run.Steps("capture")).Rating);
    }

    [Fact]
    public async Task Invalid_selection_produces_a_regular_error_without_breaking_error_handling()
    {
        var step = BlockCatalog.Default.Create(TraceBlocks.Point, "invalid", parameters: new Dictionary<string, string>
            { [TraceEndOptions.Parameter] = "invalid" });
        var run = new TestRun(Test(Group("loop").Add(step,
            BlockCatalog.Default.Create(BlockCatalog.SetRating, "following", parameters: new Dictionary<string, string> { ["Bewertung"] = "NRESULT" }))));
        Assert.Equal(ResultRating.Error, (await run.RunAsync().Within()).Rating);
        Assert.Single(run.Steps("following"));
    }

    [Fact]
    public void Invalid_choice_loads_with_a_diagnostic_and_other_blocks_keep_error_switch()
    {
        var step = BlockCatalog.Default.Create(TraceBlocks.Point, "invalid", parameters: new Dictionary<string, string> { [TraceEndOptions.Parameter] = "invalid" });
        Assert.Contains(BlockCatalog.Default.Rebuild(step), message => message.Contains("Nach Aufnahmeende"));
        Assert.Contains(BlockCatalog.Default.Get(BlockCatalog.Measure).Parameters, field => field.Name == BlockCatalog.ExitDutOnErrorParameter && field.VisibleWhen is null);
    }
}
