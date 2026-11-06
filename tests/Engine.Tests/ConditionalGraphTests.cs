using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ConditionalGraphTests
{
    private static GroupDefinition Alternative(string id, string article, params NodeDefinition[] children)
        => new GroupDefinition(id, id) { EntryCondition = $"Artikel == \"{article}\"" }.Add(children);
    private static StepDefinition Wait(string id) => BlockCatalog.Default.Create(BlockCatalog.Wait, id);
    private static EditorViewModel Editor(params NodeDefinition[] children)
        => new(new TestProject(Test(Group("loop").Add(children))));

    [Fact]
    public void Consecutive_conditions_share_a_row_with_sequential_links_and_continuation_below_all_groups()
    {
        var editor = Editor(Alternative("a", "A", Wait("a1"), Wait("a2")), Alternative("b", "B", Wait("b1")), Alternative("c", "C"), Wait("after"));
        var serializer = new ProjectSerializer(); var before = serializer.Serialize(editor.Project);
        var graph = new FlowGraphLayout(new Dictionary<string,double> { ["a1"] = 300 }).Build(editor.Nodes);
        GraphCard Card(string id) => graph.Cards.Single(card => card.Id == id);
        Assert.Equal(Card("node:a").Y, Card("node:b").Y); Assert.Equal(Card("node:b").Y, Card("node:c").Y);
        Assert.True(Card("node:a").X < Card("node:b").X); Assert.True(Card("node:b").X < Card("node:c").X);
        Assert.Contains(graph.Links, link => link.From == "end:a" && link.To == "node:b" && link.Kind == GraphLinkKind.Flow);
        Assert.Contains(graph.Links, link => link.From == "end:b" && link.To == "node:c" && link.Label == "Nächste Bedingung");
        Assert.DoesNotContain(graph.Links, link => link.Kind == GraphLinkKind.Parallel);
        Assert.Contains(graph.Links, link => link.From == "end:c" && link.To == "node:after");
        Assert.True(Card("node:after").Y > Card("end:a").Y + Card("end:a").Height);
        Assert.Equal(3, graph.Links.Count(link => link.Kind == GraphLinkKind.Else));
        foreach (var left in graph.Cards)
        foreach (var right in graph.Cards.Where(card => card.Id != left.Id))
            Assert.False(left.X < right.X + right.Width && right.X < left.X + left.Width && left.Y < right.Y + right.Height && right.Y < left.Y + left.Height);
        Assert.Equal(before, serializer.Serialize(editor.Project));
    }

    [Fact]
    public void Ordinary_steps_break_the_row_and_a_single_condition_stays_vertical()
    {
        var editor = Editor(Alternative("a", "A", Wait("a1")), Wait("middle"), Alternative("b", "B", Wait("b1")));
        var graph = new FlowGraphLayout().Build(editor.Nodes);
        var a = graph.Cards.Single(card => card.Id == "node:a");
        var b = graph.Cards.Single(card => card.Id == "node:b");
        Assert.Equal(a.X, b.X); Assert.True(b.Y > a.Y);
        Assert.DoesNotContain(graph.Links, link => link.Label == "Nächste Bedingung");
    }

    [Theory]
    [InlineData("A", false, 1, 0)]
    [InlineData("B", false, 0, 1)]
    [InlineData("C", false, 0, 0)]
    [InlineData("A", true, 1, 1)]
    public async Task Layout_does_not_change_condition_evaluation_or_infer_exclusivity(string article, bool changeArticle, int first, int second)
    {
        var set = BlockCatalog.Default.Create(BlockCatalog.SetValue, "change", parameters: new Dictionary<string,string> { ["Ziel"] = "Artikel", ["Ausdruck"] = "\"B\"" });
        var a = Alternative("a", "A", Wait("a1")); if (changeArticle) a.Add(set);
        var editor = Editor(a, Alternative("b", "B", Wait("b1")), Wait("after"));
        editor.Project.Definition.Root.Variables.Add(new("Artikel", VarType.String, article));
        var before = new ProjectSerializer().Serialize(editor.Project);
        _ = new FlowGraphLayout().Build(editor.Nodes);
        Assert.Equal(before, new ProjectSerializer().Serialize(editor.Project));
        var run = new TestRun(editor.Project.Definition, new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();
        Assert.Equal(first, run.Steps("a1").Count()); Assert.Equal(second, run.Steps("b1").Count());
        Assert.Single(run.Steps("after"));
    }

    [Fact]
    public void Overlapping_bypass_and_repeat_paths_have_separate_rails_while_disjoint_paths_reuse_space()
    {
        var rails = new GraphRailLayout();
        var first = rails.Allocate(100, 0, 500);
        var nested = rails.Allocate(100, 100, 400);
        var reverse = rails.Allocate(100, 300, 200);
        Assert.Equal(100, first); Assert.Equal(128, nested); Assert.Equal(156, reverse);
        Assert.Equal(100, rails.Allocate(100, 600, 700));
        Assert.Equal(129, rails.Allocate(101, 500, 550));
    }
}
