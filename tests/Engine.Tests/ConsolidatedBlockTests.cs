using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ConsolidatedBlockTests
{
    [Theory]
    [InlineData("true", ResultRating.Pass)]
    [InlineData("false", ResultRating.Fail)]
    public async Task Old_condition_blocks_without_mode_keep_their_behavior(string condition, ResultRating expected)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Evaluate, "evaluation", parameters: new Dictionary<string,string>
            { ["Bedingung"] = condition, ["Meldung"] = "Grenze verletzt", ["Wert"] = "42" });
        step.Parameters.Remove("Art"); step.Parameters.Remove("Bewertung");
        var project = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(step))));
        var run = new TestRun(project.Definition); await run.RunAsync().Within();
        var result = Assert.Single(run.Steps("evaluation"));
        Assert.Equal(expected, result.Rating);
        Assert.Equal(expected == ResultRating.Fail ? "Grenze verletzt" : null, result.Message);
    }

    [Theory]
    [InlineData("NRESULT", ResultRating.NResult)]
    [InlineData("PASS", ResultRating.Pass)]
    [InlineData("FAIL", ResultRating.Fail)]
    [InlineData("ERROR", ResultRating.Error)]
    public async Task Fixed_results_ignore_inactive_condition_and_keep_message(string rating, ResultRating expected)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Evaluate, "evaluation", parameters: new Dictionary<string,string>
            { ["Art"] = "Festes Ergebnis", ["Bewertung"] = rating, ["Bedingung"] = "broken (", ["Meldung"] = "Gezieltes Ergebnis" });
        Assert.Empty(BlockCatalog.Default.Rebuild(step));
        var project = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(step))));
        var run = new TestRun(project.Definition); await run.RunAsync().Within();
        var result = Assert.Single(run.Steps("evaluation"));
        Assert.Equal(expected, result.Rating); Assert.Equal("Gezieltes Ergebnis", result.Message);
    }

    [Fact]
    public void Evaluation_fields_switch_visibility_and_keep_values()
    {
        var editor = new EditorViewModel(new TestProject(Test(Group("loop"))));
        var node = editor.InsertAt(editor.Palette.SelectMany(c => c.Items).Single(i => i.BlockType == BlockCatalog.Evaluate), editor.FindNode("loop")!, 0);
        var properties = (StepPropertiesViewModel)editor.Properties!;
        var mode = properties.Parameters.Single(p => p.Descriptor.Name == "Art");
        var condition = properties.Parameters.Single(p => p.Descriptor.Name == "Bedingung");
        var rating = properties.Parameters.Single(p => p.Descriptor.Name == "Bewertung");
        Assert.False(condition.IsHidden); Assert.True(rating.IsHidden);
        condition.Value = "false";
        mode.Value = "Festes Ergebnis";
        Assert.True(condition.IsHidden); Assert.False(rating.IsHidden);
        rating.Value = "ERROR";
        mode.Value = "Bedingung prüfen";
        Assert.False(condition.IsHidden); Assert.True(rating.IsHidden); Assert.Equal("false", condition.Value);
        Assert.Equal("ERROR", rating.Value);
        Assert.DoesNotContain(editor.Palette.SelectMany(c => c.Items), i => i.BlockType is BlockCatalog.SetRating or BlockCatalog.RaiseError or NetworkBlocks.Download);
        Assert.True(BlockCatalog.Default.TryGet(BlockCatalog.SetRating, out _));
        Assert.True(BlockCatalog.Default.TryGet(BlockCatalog.RaiseError, out _));
        Assert.True(BlockCatalog.Default.TryGet(NetworkBlocks.Download, out _));
    }

    [Theory]
    [InlineData(BlockCatalog.SetRating, "NRESULT", ResultRating.NResult)]
    [InlineData(BlockCatalog.SetRating, "PASS", ResultRating.Pass)]
    [InlineData(BlockCatalog.SetRating, "FAIL", ResultRating.Fail)]
    [InlineData(BlockCatalog.SetRating, "ERROR", ResultRating.Error)]
    [InlineData(BlockCatalog.RaiseError, "", ResultRating.Error)]
    public async Task Hidden_legacy_blocks_still_roundtrip_and_execute(string type, string rating, ResultRating expected)
    {
        var step = BlockCatalog.Default.Create(type, "legacy", parameters: new Dictionary<string,string> { ["Bewertung"] = rating, ["Meldung"] = "Altbestand" });
        var project = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(step))));
        var run = new TestRun(project.Definition); await run.RunAsync().Within();
        var result = Assert.Single(run.Steps("legacy"));
        Assert.Equal(expected, result.Rating);
        if (type == BlockCatalog.SetRating)
        {
            var migrated = Assert.IsType<StepDefinition>(project.Definition.Find("legacy"));
            Assert.Equal(BlockCatalog.Evaluate, migrated.BlockType);
            Assert.Equal(BlockCatalog.FixedRatingMode, migrated.Parameters["Art"]);
            Assert.Null(result.Message);
        }
    }
}
