using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class VisualFlowTests
{
    private static EditorViewModel Editor()
        => new(new TestProject(Kit.Test(new GroupDefinition("loop", "Prüfen"))));
    [Theory]
    [InlineData("when")][InlineData("ifelse")][InlineData("repeat")][InlineData("while")][InlineData("until")][InlineData("parallel")]
    public void Control_presets_roundtrip_as_standard_projects_and_one_undo_action(string preset)
    {
        var editor = Editor(); var serializer = new ProjectSerializer(); var before = serializer.Serialize(editor.Project);
        var item = EditorViewModel.ControlBlocks.Single(p => p.Preset == preset);
        var added = editor.InsertAt(item, editor.FindNode("loop")!, 0);
        Assert.True(added.IsGroup); Assert.Empty(editor.Project.Definition.Validate());
        var after = serializer.Serialize(editor.Project); Assert.Equal(after, serializer.Serialize(serializer.Deserialize(after)));
        editor.VisualBlocks = true; Assert.Equal(after, serializer.Serialize(editor.Project));
        editor.UndoCommand.Execute(null); Assert.Equal(before, serializer.Serialize(editor.Project));
        editor.RedoCommand.Execute(null); Assert.Equal(after, serializer.Serialize(editor.Project)); Assert.True(editor.VisualBlocks);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task If_else_captures_condition_once_before_the_selected_branch_changes_its_input(bool input)
    {
        var editor = Editor(); editor.Project.Definition.Root.Variables.Add(new("Eingang", VarType.Bool, input));
        var node = editor.Insert(EditorViewModel.ControlBlocks.Single(p => p.Preset == "ifelse"));
        var group = node.Group!;
        var decision = (StepDefinition)group.Children[0]; decision.Parameters["Ausdruck"] = "Eingang"; BlockCatalog.Default.Rebuild(decision);
        var then = (GroupDefinition)group.Children[1];
        then.Children.Add(BlockCatalog.Default.Create(BlockCatalog.SetValue, "change", parameters: new Dictionary<string, string> { ["Ziel"] = "Eingang", ["Ausdruck"] = "false" }));
        then.Children.Add(BlockCatalog.Default.Create(BlockCatalog.SetRating, "pass"));
        var otherwise = (GroupDefinition)group.Children[2];
        otherwise.Children.Add(BlockCatalog.Default.Create(BlockCatalog.SetRating, "fail", parameters: new Dictionary<string, string> { ["Bewertung"] = "FAIL" }));
        var cloned = new ProjectSerializer().Clone(editor.Project);
        var run = new TestRun(cloned.Definition); var result = await run.RunAsync().Within();
        Assert.Equal(input ? ResultRating.Pass : ResultRating.Fail, result.Rating);
        Assert.Equal(input ? 1 : 0, run.Events.OfType<StepCompleted>().Count(e => e.NodeId == "pass"));
        Assert.Equal(input ? 0 : 1, run.Events.OfType<StepCompleted>().Count(e => e.NodeId == "fail"));
    }
    [Fact]
    public void Insertion_and_variable_declarations_guard_stale_targets_and_types()
    {
        var editor = Editor(); var parent = editor.FindNode("loop")!;
        Assert.False(editor.CanInsertAt(parent, -1)); Assert.False(editor.CanInsertAt(parent, 1));
        Assert.Throws<InvalidOperationException>(() => editor.InsertAt(EditorViewModel.ControlBlocks[0], parent, -1));
        Assert.Null(editor.DeclareVisualVariable("loop", "Spannung", VarType.Double, "12.5"));
        Assert.False(editor.CanInsertAt(parent, 0));
        Assert.NotNull(editor.DeclareVisualVariable("loop", "Spannung", VarType.Double, "1"));
        Assert.NotNull(editor.DeclareVisualVariable("loop", Scope.GroupResultVariable, VarType.String, ""));
        Assert.NotNull(editor.DeclareVisualVariable("loop", "true", VarType.Bool, ""));
        Assert.NotNull(editor.DeclareVisualVariable("loop", "_BlockZweig_name", VarType.Bool, ""));
        Assert.NotNull(editor.DeclareVisualVariable("loop", "Neu", VarType.Int, "abc"));
        Assert.NotNull(editor.DeclareVisualVariable("fehlend", "Neu", VarType.Int, "1"));
        Assert.Null(editor.DeclareVisualVariable(editor.Project.Definition.Root.Id, "Global", VarType.String, "abc"));
    }
    [Fact]
    public void Variable_choices_match_real_scope_resolution_and_hide_readonly_write_targets()
    {
        var editor = Editor(); var definition = editor.Project.Definition;
        definition.Root.Variables.Add(new("X", VarType.Double, 1d));
        definition.Loop.Variables.Add(new("X", VarType.Int, 2L));
        var child = new GroupDefinition("child", "Kind"); child.Variables.Add(new("X", VarType.String, "Text"));
        definition.Loop.Children.Add(child); child.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "leaf"));
        var scope = Scope.CreateFor(child, Scope.CreateFor(definition.Loop, Scope.CreateFor(definition.Root, null)));
        var choices = ExpressionVariableChoices.ForNode(definition, "leaf");
        Assert.All(choices, v => Assert.Equal(v.Type, scope.Lookup(v.Path).Type));
        Assert.Contains(choices, v => v.Path == "X" && v.Type == VarType.String);
        Assert.Contains(choices, v => v.Path == "../X" && v.Type == VarType.Int);
        Assert.DoesNotContain(ExpressionVariableChoices.ForNode(definition, "leaf", writable: true), v => v.Path.EndsWith(Scope.GroupResultVariable));
        Assert.All(ExpressionVariableChoices.ForNode(definition, "child"), v => Assert.Equal(v.Type, scope.Lookup(v.Path).Type));
        Assert.False(ExpressionVariableChoices.IsPath("1 + 2")); Assert.False(ExpressionVariableChoices.IsPath("X-Y"));
    }

    [Fact]
    public async Task Documented_example_is_a_normal_executable_project()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var path = Path.Combine(root.FullName, "docs", "beispiele", "blockprogrammierung", "bloecke.tproj.json");
        var project = new ProjectSerializer().Deserialize(File.ReadAllText(path));
        Assert.Empty(project.Definition.Validate());
        var run = new TestRun(project.Definition);
        Assert.Equal(ResultRating.Pass, (await run.RunAsync().Within()).Rating);
        Assert.Equal(3, run.Events.OfType<StepCompleted>().Count(e => e.NodeId == "wertpruefen"));
        Assert.DoesNotContain(run.Events.OfType<StepCompleted>(), e => e.NodeId == "fail");
        Assert.Contains(run.Events.OfType<StepCompleted>(), e => e.NodeId == "parallel-a");
        Assert.Contains(run.Events.OfType<StepCompleted>(), e => e.NodeId == "parallel-b");
    }

    [Fact]
    public void Boundaries_ports_invalid_names_and_implicit_status_follow_the_engine_scope()
    {
        var editor = Editor(); var definition = editor.Project.Definition;
        definition.Root.Variables.Add(new("Outer", VarType.String, "Außen"));
        var body = new GroupDefinition("body", "Bausteinkörper"); body.Resources.Add("Quelle");
        body.Variables.Add(new("Local", VarType.Int, 1L)); body.Variables.Add(new("A-B", VarType.Int, 1L));
        body.Variables.Add(new("_BlockZweig_demo", VarType.Bool, false));
        var block = new GroupBlockDefinition("library", "Bibliothek", body);
        block.Ports.Add(new("Input", VarType.Double, Testumgebung.Domain.Code.PortDirection.Input));
        definition.GroupLibrary.Add(block);
        var variables = ExpressionVariableChoices.ForNode(definition, body.Id);
        Assert.Contains(variables, v => v.Path == "Input"); Assert.Contains(variables, v => v.Path == "../Input");
        Assert.Contains(variables, v => v.Path == TestRun.ResourcesAcquiredVariable);
        Assert.Contains(variables, v => v.Path == "_BlockZweig_demo" && v.Label.Contains("Vorlagenentscheidung"));
        Assert.DoesNotContain(variables, v => v.Path == "Outer" || v.Path == "A-B");
        Assert.DoesNotContain(ExpressionVariableChoices.ForNode(definition, body.Id, writable: true), v => v.Path.StartsWith("_BlockZweig_"));
        body.IsInterfaceBoundary = true;
        Assert.DoesNotContain(ExpressionVariableChoices.ForNode(definition, body.Id), v => v.Path == "Input");
        Assert.False(ExpressionVariableChoices.IsPath(" Local"));
    }
}