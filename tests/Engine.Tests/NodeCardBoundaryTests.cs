using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class NodeCardBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("future")]
    public void PaletteUsesTheDocumentFallbackForMissingAndUnknownTypes(string? type)
    {
        var method = typeof(EditorViewModel).GetMethod("IconFor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(NodeIcon.Doc, method.Invoke(null, [type]));
    }

    [Theory]
    [InlineData(BlockCatalog.Measure, NodeIcon.Measure)]
    [InlineData(BlockCatalog.Output, NodeIcon.Arrow)]
    [InlineData(BlockCatalog.SetValue, NodeIcon.Arrow)]
    [InlineData(BlockCatalog.DigitalRead, NodeIcon.Digital)]
    [InlineData(BlockCatalog.DigitalWrite, NodeIcon.Digital)]
    [InlineData(BlockCatalog.Evaluate, NodeIcon.Check)]
    [InlineData(BlockCatalog.SetRating, NodeIcon.Check)]
    [InlineData(BlockCatalog.Operator, NodeIcon.Operator)]
    [InlineData(BlockCatalog.Wait, NodeIcon.Wait)]
    [InlineData(BlockCatalog.RaiseError, NodeIcon.Error)]
    [InlineData(BlockCatalog.Code, NodeIcon.Code)]
    [InlineData(BlockCatalog.LibraryBlock, NodeIcon.LibraryBlock)]
    [InlineData(BlockCatalog.GroupBlock, NodeIcon.LibraryBlock)]
    [InlineData(null, NodeIcon.Code)]
    [InlineData("future", NodeIcon.Doc)]
    public void Step_cards_use_the_correct_symbol(string? type, NodeIcon expected)
    {
        var step = Steps.Rate("step", ResultRating.Pass); step.BlockType = type;
        var card = new NodeViewModel(step, null);
        Assert.Equal(expected, card.Icon); Assert.Same(step, card.Step); Assert.Null(card.Group);
        Assert.Equal("", card.Annotation); Assert.True(card.CanMove); Assert.False(card.IsGroup);
    }

    [Theory]
    [InlineData(false, false, false, NodeIcon.Group)]
    [InlineData(false, false, true, NodeIcon.DutGroup)]
    [InlineData(true, false, true, NodeIcon.ExitGroup)]
    [InlineData(true, true, true, NodeIcon.Section)]
    [InlineData(false, true, false, NodeIcon.Section)]
    public void Group_card_roles_have_defined_priority(bool exit, bool section, bool dut, NodeIcon expected)
    {
        var group = new GroupDefinition("g", "Group") { IsDutRun = dut };
        var card = new NodeViewModel(group, null, exit, section);
        Assert.Equal(expected, card.Icon); Assert.Equal(!exit && !section, card.CanMove);
        Assert.True(card.IsGroup); Assert.Same(group, card.Group); Assert.Null(card.Step);
        Assert.Equal(exit, card.Annotation.Contains("Exit")); Assert.Equal(dut, card.Annotation.Contains("Prüfling-Run"));
    }

    [Theory]
    [InlineData(RepeatMode.Once, "")]
    [InlineData(RepeatMode.Count, "×3")]
    [InlineData(RepeatMode.Duration, "2 s")]
    [InlineData(RepeatMode.While, "solange ready")]
    [InlineData(RepeatMode.Until, "bis ready")]
    [InlineData(RepeatMode.Endless, "wiederholt bis Werkerabbruch")]
    public void Group_annotations_describe_repetition_and_scope(RepeatMode mode, string expected)
    {
        var group = new GroupDefinition("g", "Group") { Repetition = new Repetition(mode, 3, TimeSpan.FromSeconds(2), "ready") };
        var plain = new NodeViewModel(group, null);
        Assert.Equal(expected.Length == 0 ? "" : "(" + expected + ")", plain.Annotation);
        group.EntryCondition = "true"; group.Resources.Add("device"); group.Variables.Add(new VariableDefinition("v", VarType.String, "initial"));
        var annotated = new NodeViewModel(group, plain) { Note = "contract" };
        Assert.Contains("contract", annotated.Annotation); Assert.Contains("wenn true", annotated.Annotation);
        Assert.Contains("device", annotated.Annotation); Assert.Contains("1 Var.", annotated.Annotation);
        Assert.Equal(1, annotated.Depth);
    }

    [Fact]
    public void Card_changes_notify_and_tree_enumeration_preserves_order()
    {
        var root = new NodeViewModel(new GroupDefinition("g", "Group"), null);
        var child = new NodeViewModel(Steps.Rate("s", ResultRating.Pass), root);
        root.Children.Add(child);
        var notifications = new List<string?>(); root.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        root.Name = " "; root.Name = "Group"; Assert.Empty(notifications);
        root.Name = "renamed"; root.Number = "1"; root.IsSelected = true; root.IsExpanded = false; root.HasBreakpoint = true;
        root.Diagnostic = "error"; root.Diagnostic = "error"; root.RunMarker = RunMarker.Paused; root.Refresh();
        Assert.True(root.HasDiagnostic); Assert.Contains("HasDiagnostic", notifications);
        root.Diagnostic = null; Assert.False(root.HasDiagnostic);
        Assert.Equal(new[] { root, child }, root.SelfAndDescendants());
        Assert.Equal("1 renamed", root.ToString());
        Assert.Equal("g", root.Id);
        Assert.Equal(NodeIcon.Doc, new NodeViewModel(new FutureNode(), null).Icon);
    }

    private sealed class FutureNode() : NodeDefinition("future", "future");
}
