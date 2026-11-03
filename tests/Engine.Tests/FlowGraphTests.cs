using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class FlowGraphTests
{
    private static EditorViewModel Editor()
    {
        var loop = new GroupDefinition("loop", "Loop");
        loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "a"));
        loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "b"));
        loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "c"));
        return new(new TestProject(new TestDefinition("Graph", "1", new("start", "Start"), loop, new("end", "End"))));
    }
    [Fact]
    public void Viewport_zooms_around_pointer_and_pans_in_all_directions_without_bounds()
    {
        var camera = new GraphViewport();
        var x = (200 - camera.X) / camera.Zoom; var y = (150 - camera.Y) / camera.Zoom;
        camera.ZoomAt(2, 200, 150);
        Assert.Equal(200, x * camera.Zoom + camera.X); Assert.Equal(150, y * camera.Zoom + camera.Y);
        camera.Pan(-2000, 1000); Assert.True(camera.X < 0); Assert.True(camera.Y > 0);
        camera.Pan(5000, -4000); Assert.True(camera.X > 0); Assert.True(camera.Y < 0);
        camera.ZoomAt(100, 0, 0); Assert.Equal(2.5, camera.Zoom);
        camera.ZoomAt(.001, 0, 0); Assert.Equal(.15, camera.Zoom);
        camera.Fit(1200, 800, 800, 600); Assert.InRange(camera.Zoom, .15, 1);
        camera.Fit(0, 0, 0, 0); Assert.Equal(.15, camera.Zoom);
        camera.Reset(); Assert.Equal(1, camera.Zoom); Assert.Equal(30, camera.X); Assert.Equal(30, camera.Y);
    }
    [Fact]
    public void Sequential_projection_uses_real_node_ids_and_valid_edges_without_changing_project()
    {
        var editor = Editor(); var serializer = new ProjectSerializer(); var before = serializer.Serialize(editor.Project);
        var graph = new FlowGraphLayout(new Dictionary<string, double> { ["a"] = 400 }).Build(editor.Nodes);
        Assert.Equal(before, serializer.Serialize(editor.Project));
        Assert.Equal(400, graph.Cards.Single(n => n.Id == "node:a").Height);
        Assert.Contains(graph.Links, l => l.From == "node:a" && l.To == "node:b");
        Assert.Contains(graph.Links, l => l.From == "end:start" && l.To == "node:loop");
        Assert.Equal(graph.Cards.Count, graph.Cards.Select(n => n.Id).Distinct().Count());
        Assert.All(graph.Links, l => { Assert.Contains(graph.Cards, n => n.Id == l.From); Assert.Contains(graph.Cards, n => n.Id == l.To); });
        Assert.All(graph.Cards, n => { Assert.True(n.X >= 0); Assert.True(n.Y >= 0); Assert.True(n.Height > 0); });
        Assert.Empty(new FlowGraphLayout().Build([]).Cards);
    }
    [Theory]
    [InlineData("ifelse")][InlineData("parallel")][InlineData("while")][InlineData("until")][InlineData("repeat")][InlineData("when")]
    public void Projection_explains_control_flow_including_split_join_repeat_and_exit(string preset)
    {
        var editor = Editor(); var owner = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == preset), editor.FindNode("loop")!, 0);
        var ownerId = owner.Id;
        if (preset == "parallel")
        {
            owner.Group!.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "p1"));
            owner.Group.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "p2"));
        }
        owner.Group!.ExitGroup = new GroupDefinition("exit", "Aufräumen"); editor.RebuildTree();
        var graph = new FlowGraphLayout().Build(editor.Nodes);
        Assert.Contains(graph.Links, l => l.Kind == GraphLinkKind.Exit);
        Assert.Contains(graph.Cards, c => c.Id == "end:" + ownerId);
        if (preset == "ifelse")
        {
            Assert.DoesNotContain(graph.Cards, c => c.Owner.IsTemplateDecision);
            Assert.Contains(graph.Links, l => l.Kind == GraphLinkKind.Then);
            Assert.Contains(graph.Links, l => l.Kind == GraphLinkKind.Else);
        }
        if (preset == "parallel") Assert.Equal(2, graph.Links.Count(l => l.From == "node:" + ownerId && l.Kind == GraphLinkKind.Parallel));
        if (preset == "while") Assert.Contains(graph.Links, l => l.Kind == GraphLinkKind.Else && l.Label.Contains("Solange"));
        if (preset is "while" or "until" or "repeat") Assert.Contains(graph.Links, l => l.Kind == GraphLinkKind.Repeat);
        Assert.DoesNotContain(graph.Links, l => l.From.StartsWith('@') || l.To.StartsWith('@'));
    }
    [Fact]
    public void Library_definitions_are_disconnected_from_the_main_execution_path()
    {
        var editor = Editor();
        var body = new GroupDefinition("lib-body", "Bibliothek");
        body.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "lib-step"));
        editor.Project.Definition.GroupLibrary.Add(new("lib", "Bibliothek", body));
        editor.RebuildTree();
        var graph = new FlowGraphLayout().Build(editor.Nodes);
        Assert.Contains(graph.Cards, c => c.Id == "node:lib-step");
        Assert.DoesNotContain(graph.Links, l => l.From == "end:end" && l.To == "node:lib-body");
        Assert.Contains(graph.Links, l => l.From == "node:lib-body" && l.To == "node:lib-step");
        var isolated = new FlowGraphLayout().Build([editor.FindNode("lib-body")!]);
        Assert.All(isolated.Cards, c => Assert.True(c.X >= 0));
    }
    [Fact]
    public void Manual_connection_reorders_existing_steps_and_is_one_undo_action()
    {
        var editor = Editor(); var serializer = new ProjectSerializer(); var before = serializer.Serialize(editor.Project);
        var workspace = editor.GraphWorkspaceId;
        var socket = new GraphSocket(editor.FindNode("a")!, GraphSocketKind.Next);
        Assert.True(editor.ConnectGraph(socket, editor.FindNode("c")!));
        Assert.Equal(new[] { "a", "c", "b" }, editor.Project.Definition.Loop.Children.Select(n => n.Id));
        editor.UndoCommand.Execute(null); Assert.Equal(before, serializer.Serialize(editor.Project));
        editor.RedoCommand.Execute(null); Assert.Equal("c", editor.Project.Definition.Loop.Children[1].Id);
        Assert.Equal(workspace, editor.GraphWorkspaceId);
        Assert.False(editor.ConnectGraph(socket, editor.FindNode("b")!)); // obsolete source after rebuilding
        editor.Load(editor.Project); Assert.NotEqual(workspace, editor.GraphWorkspaceId);
    }
    [Theory]
    [InlineData(GraphSocketKind.Then, 1)][InlineData(GraphSocketKind.Else, 2)]
    public void Decision_outputs_insert_into_the_correct_branch(GraphSocketKind kind, int branch)
    {
        var editor = Editor(); var choice = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == "ifelse"), editor.FindNode("loop")!, 0);
        Assert.True(editor.ConnectGraph(new(choice, kind), editor.FindNode("b")!));
        Assert.Equal("b", ((GroupDefinition)editor.Project.Definition.Find(choice.Id)!).Children.OfType<GroupDefinition>().ElementAt(branch - 1).Children[0].Id);
    }
    [Theory]
    [InlineData(GraphSocketKind.Inside)][InlineData(GraphSocketKind.Next)]
    public void Fixed_section_sockets_insert_steps_without_moving_the_section(GraphSocketKind kind)
    {
        var editor = Editor();
        Assert.True(editor.ConnectGraph(new(editor.FindNode("start")!, kind), editor.FindNode("b")!));
        Assert.Equal("b", Assert.Single(editor.Project.Definition.Init.Children).Id);
        Assert.False(editor.CanConnectGraph(new(editor.FindNode("a")!, (GraphSocketKind)99), editor.FindNode("c")!));
        Assert.False(editor.CanConnectGraph(new(editor.FindNode("a")!, GraphSocketKind.Next), editor.FindNode("c")!)); // already follows a
    }
    [Fact]
    public void Protected_decision_parts_and_exit_completion_cannot_be_rewired_as_steps()
    {
        var editor = Editor();
        var decision = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == "ifelse"), editor.FindNode("loop")!, 0);
        decision.Group!.ExitGroup = new GroupDefinition("exit", "Exit"); editor.RebuildTree();
        decision = editor.FindNode(decision.Id)!;
        Assert.False(editor.ConnectGraph(new(editor.FindNode("a")!, GraphSocketKind.Next), decision.Children[0]));
        Assert.False(editor.ConnectGraph(new(editor.FindNode("a")!, GraphSocketKind.Next), decision.Children[1]));
        Assert.False(editor.ConnectGraph(new(decision.Children[1], GraphSocketKind.Next), editor.FindNode("a")!));
        Assert.False(editor.ConnectGraph(new(editor.FindNode("exit")!, GraphSocketKind.Next), editor.FindNode("a")!));
        Assert.True(editor.ConnectGraph(new(decision, GraphSocketKind.Next), editor.FindNode("c")!));
    }
    [Fact]
    public void Invalid_and_cyclic_connections_leave_the_project_unchanged()
    {
        var editor = Editor(); var group = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == "parallel"), editor.FindNode("loop")!, 0);
        Assert.True(editor.ConnectGraph(new(group, GraphSocketKind.Parallel), editor.FindNode("b")!));
        group = editor.FindNode(group.Id)!;
        var child = editor.FindNode("b")!;
        var before = new ProjectSerializer().Serialize(editor.Project);
        Assert.False(editor.CanConnectGraph(new(child, GraphSocketKind.Next), editor.FindNode("a")!));
        Assert.False(editor.ConnectGraph(new(group, GraphSocketKind.Inside), group));
        Assert.False(editor.ConnectGraph(new(group, GraphSocketKind.Inside), editor.FindNode("loop")!));
        Assert.False(editor.ConnectGraph(new(editor.FindNode("a")!, GraphSocketKind.Then), child));
        Assert.False(editor.ConnectGraph(new(editor.FindNode("a")!, GraphSocketKind.Parallel), child));
        Assert.False(editor.ConnectGraph(new(editor.FindNode("a")!, GraphSocketKind.Next), editor.FindNode("a")!));
        Assert.False(editor.ConnectGraph(new(editor.FindNode("start")!, GraphSocketKind.Inside), editor.FindNode("end")!));
        Assert.Equal(before, new ProjectSerializer().Serialize(editor.Project));
    }
}
