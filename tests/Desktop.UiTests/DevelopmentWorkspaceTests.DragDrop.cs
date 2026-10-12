using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyEditorDragDrop()
    {
        var loop = new GroupDefinition("loop", "Loop"); var group = new GroupDefinition("group", "Group") { ExitGroup = new GroupDefinition("exit", "Exit") };
        loop.Children.Add(group); loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "first", "First")); loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "second", "Second"));
        var project = new TestProject(new TestDefinition("Drag", "1", new GroupDefinition("start", "Start"), loop, new GroupDefinition("end", "End")));
        var shell = new ShellViewModel(project: project); var view = new EditorView(); var editor = shell.Editor;
        var dragged = editor.FindNode("second")!;
        var dragField = typeof(EditorView).GetField("_dragNode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var startField = typeof(EditorView).GetField("_dragStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        startField.SetValue(view, new Point(10, 10));
        Assert.Null(view.TryBeginTreeDrag(MouseButtonState.Pressed, new Point(100, 100)));
        dragField.SetValue(view, editor.FindNode("loop"));
        Assert.Null(view.TryBeginTreeDrag(MouseButtonState.Pressed, new Point(100, 100)));
        dragField.SetValue(view, dragged);
        Assert.Null(view.TryBeginTreeDrag(MouseButtonState.Released, new Point(100, 100)));
        Assert.Null(view.TryBeginTreeDrag(MouseButtonState.Pressed, new Point(10, 10)));
        foreach (var delta in new[] { new Vector(SystemParameters.MinimumHorizontalDragDistance, 0), new Vector(-SystemParameters.MinimumHorizontalDragDistance, 0), new Vector(0, SystemParameters.MinimumVerticalDragDistance), new Vector(0, -SystemParameters.MinimumVerticalDragDistance) })
        {
            dragField.SetValue(view, dragged);
            Assert.Same(dragged, view.TryBeginTreeDrag(MouseButtonState.Pressed, new Point(10, 10) + delta));
            Assert.Null(dragField.GetValue(view));
        }
        var targetMethod = typeof(EditorView).GetMethod("TryGetTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var constructor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        DragEventArgs Event(DependencyObject source, bool hasData = true, double y = .5)
        {
            var data = new DataObject(); if (hasData) data.SetData(typeof(NodeViewModel), dragged);
            var e = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Move, source, new Point(0, 100 * y)]);
            e.RoutedEvent = DragDrop.DragOverEvent; e.Source = source; return e;
        }
        Border Card(object? target)
        {
            var card = new Border { Name = "Card", DataContext = target, Width = 100, Height = 100 };
            card.Measure(new Size(100, 100)); card.Arrange(new Rect(0, 0, 100, 100)); return card;
        }
        (bool Accepted, NodeViewModel? Parent, int Index) Target(DragEventArgs e)
        {
            object?[] args = [e, null, null, 0]; var accepted = (bool)targetMethod.Invoke(view, args)!; return (accepted, args[2] as NodeViewModel, (int)args[3]!);
        }
        Assert.False(Target(Event(Card(editor.FindNode("first")))).Accepted);
        view.DataContext = shell;
        Assert.False(Target(Event(Card(editor.FindNode("first")), false)).Accepted);
        Assert.False(Target(Event(new TreeViewItem())).Accepted);
        Assert.False(Target(Event(Card("unknown"))).Accepted);
        var first = editor.FindNode("first")!; var section = editor.FindNode("loop")!; var groupNode = editor.FindNode("group")!;
        Assert.Equal((true, section, 1), Target(Event(Card(first), y: .1)));
        Assert.Equal((true, section, 2), Target(Event(Card(first), y: .9)));
        Assert.Equal((true, groupNode, 0), Target(Event(Card(groupNode), y: .5)));
        Assert.Equal((true, section, 0), Target(Event(Card(groupNode), y: .1)));
        Assert.Equal((true, section, 1), Target(Event(Card(groupNode), y: .9)));
        Assert.Equal((true, section, 3), Target(Event(Card(section))));
        var exit = editor.FindNode("exit")!; Assert.Equal((true, exit, 0), Target(Event(Card(exit))));
        Assert.False(Target(Event(Card(new NodeViewModel(new GroupDefinition("orphan", "Orphan"), null)), y: .1)).Accepted);
        Assert.False(Target(Event(Card(new NodeViewModel(new GroupDefinition("absent", "Absent"), section)), y: .1)).Accepted);
        var child = new TextBlock(); var nested = Card(first); nested.Child = child;
        var nestedEvent = Event(nested, y: .1); nestedEvent.Source = child;
        Assert.True(Target(nestedEvent).Accepted);
        var over = Event(Card(first), y: .1); typeof(EditorView).GetMethod("OnTreeDragOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, over]);
        Assert.True(over.Handled); Assert.Equal(DragDropEffects.Move, over.Effects);
        var invalid = Event(new TreeViewItem()); typeof(EditorView).GetMethod("OnTreeDragOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, invalid]);
        Assert.True(invalid.Handled); Assert.Equal(DragDropEffects.None, invalid.Effects);
        var drop = Event(Card(groupNode)); typeof(EditorView).GetMethod("OnTreeDrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, drop]);
        Assert.True(drop.Handled); Assert.Equal("group", editor.FindNode("second")!.Parent!.Id);
        typeof(EditorView).GetMethod("OnTreeDrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, invalid]); Assert.True(invalid.Handled);
    }
}
