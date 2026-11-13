using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class FormulaTraceTests
{
    private static StepDefinition Capture(string type, string lower, string upper, params (string Key, string Value)[] extra)
    {
        var parameters = extra.ToDictionary(entry => entry.Key, entry => entry.Value);
        parameters[TraceBlocks.EvaluationParameter] = TraceBlocks.FormulaBand;
        parameters[TraceBlocks.LowerFormulaParameter] = lower;
        parameters[TraceBlocks.UpperFormulaParameter] = upper;
        parameters["Grenzkurven"] = "unbenutzt@1";
        return BlockCatalog.Default.Create(type, "capture", parameters: parameters);
    }

    [Theory]
    [InlineData("5", ResultRating.Pass, 0)]
    [InlineData("6.5", ResultRating.Fail, 3)]
    public async Task Timed_recording_uses_formula_bounds_at_each_sample(string value, ResultRating rating, int violations)
    {
        var capture = Capture(TraceBlocks.Record, "4 + 0.2 * t", "6 + 0.2 * t",
            ("Dauer", "1"), ("Intervall", "0.5"), ("Werte", value));
        var project = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(capture))));
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();

        var result = run.Steps("capture").Single();
        Assert.Equal(rating, result.Rating);
        var data = Assert.IsType<WaveformSet>(result.Value);
        Assert.Equal(new double[] { 4, 4.1, 4.2 }, data.Channels[1].Samples);
        Assert.Equal(new double[] { 6, 6.1, 6.2 }, data.Channels[2].Samples);
        Assert.Equal(violations, EnvelopeTraceMetadata.Decode(data.Channels[0].Quality)!.ViolationCount);
    }

    [Fact]
    public async Task Manual_recording_can_stop_at_a_formula_limit()
    {
        var start = Capture(TraceBlocks.Start, "0", "4 + t", ("FailAbbruch", "true"), ("AbweichungProzent", "10"));
        var first = Point("first", "0", "4");
        var next = Point("next", "1", "6");
        var run = new TestRun(Test(Group("loop").Add(start, first, next)));
        await run.RunAsync().Within();

        var result = run.Steps("next").Single();
        Assert.Equal(ResultRating.Fail, result.Rating);
        var data = Assert.IsType<WaveformSet>(result.Value);
        Assert.Equal(5, EnvelopeTraceMetadata.Decode(data.Channels[0].Quality)!.EarlyStop!.Upper);
    }

    [Theory]
    [InlineData("", "6", "Untergrenze")]
    [InlineData("4 +", "6", "Untergrenze")]
    [InlineData("x", "6", "nur t")]
    [InlineData("false", "6", "endliche Zahl")]
    [InlineData("1e400", "6", "endliche Zahl")]
    [InlineData("0", "1 / t", "Obergrenze")]
    [InlineData("4", "3", "über der Obergrenze")]
    public async Task Invalid_formula_bounds_report_an_error(string lower, string upper, string message)
    {
        var capture = Capture(TraceBlocks.Record, lower, upper, ("Werte", "5"));
        var run = new TestRun(Test(Group("loop").Add(capture)), new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();
        var result = run.Steps("capture").Single();
        Assert.Equal(ResultRating.Error, result.Rating);
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public void Editor_shows_the_formula_fields_only_in_formula_mode()
    {
        var editor = new EditorViewModel(new TestProject(Test(Group("loop"))));
        editor.InsertAt(editor.Palette.SelectMany(category => category.Items).Single(item => item.BlockType == TraceBlocks.Start),
            editor.FindNode("loop")!, 0);
        var rows = Assert.IsType<StepPropertiesViewModel>(editor.Properties).Parameters.ToDictionary(row => row.Descriptor.Name);
        Assert.True(rows[TraceBlocks.LowerFormulaParameter].IsHidden);
        rows[TraceBlocks.EvaluationParameter].Value = TraceBlocks.FormulaBand;
        Assert.False(rows[TraceBlocks.LowerFormulaParameter].IsHidden);
        Assert.False(rows[TraceBlocks.UpperFormulaParameter].IsHidden);
        Assert.True(rows["Grenzkurven"].IsHidden);
        Assert.True(rows["SteigungMin"].IsHidden);
    }

    private static StepDefinition Point(string id, string time, string value) =>
        BlockCatalog.Default.Create(TraceBlocks.Point, id, parameters: new Dictionary<string, string> { ["Zeit"] = time, ["Werte"] = value });
}
