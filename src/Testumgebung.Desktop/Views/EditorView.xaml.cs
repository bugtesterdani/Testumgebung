/**
 * @file
 * @brief WPF-Anbindung für Ablaufeditor. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>
/// Ablaufeditor. Drag-and-drop mit festen Einfügepositionen:
/// oberes Viertel = davor, unteres Viertel = danach, Mitte einer Gruppe = hinein (ans Ende).
/// </summary>
public partial class EditorView : UserControl
{
    private Point _dragStart;
    private NodeViewModel? _dragNode;

    public EditorView()
    {
        InitializeComponent();
        // Strg+F: Suche (auch aus der Kartenliste)
        InputBindings.Add(new KeyBinding(new FocusSearchCommand(this), Key.F, ModifierKeys.Control));
    }

    private sealed class FocusSearchCommand(EditorView view) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            view.SearchBox.Focus();
            view.SearchBox.SelectAll();
        }
    }

    private EditorViewModel? Editor => (DataContext as ShellViewModel)?.Editor;

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Editor is { } editor && e.NewValue is NodeViewModel node) editor.SelectedNode = node;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (TryHandleTreeKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    private bool TryHandleTreeKey(Key key, ModifierKeys modifiers)
    {
        if (Editor is not { } editor) return false;
        if (key == Key.Delete && editor.DeleteCommand.CanExecute(null))
        {
            editor.DeleteCommand.Execute(null);
            return true;
        }
        else if (key == Key.F9)
        {
            editor.ToggleBreakpoint(editor.SelectedNode);
            return true;
        }
        else if (modifiers == ModifierKeys.Control && key is Key.C or Key.X or Key.V or Key.D)
        {
            var command = key switch
            {
                Key.C => editor.CopyCommand,
                Key.X => editor.CutCommand,
                Key.V => editor.PasteCommand,
                _ => editor.DuplicateCommand,
            };
            if (command.CanExecute(null)) command.Execute(null);
            return true;
        }
        return false;
    }

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FlowTree);
        _dragNode = NodeFrom(e.OriginalSource as DependencyObject);
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (TryBeginTreeDrag(e.LeftButton, e.GetPosition(FlowTree)) is { } node)
            DragDrop.DoDragDrop(FlowTree, node, DragDropEffects.Move);
    }

    internal NodeViewModel? TryBeginTreeDrag(MouseButtonState button, Point position)
    {
        if (button != MouseButtonState.Pressed || _dragNode is not { CanMove: true } node) return null;
        var delta = position - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return null;
        _dragNode = null;
        return node;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetTarget(e, out _, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        if (TryGetTarget(e, out var node, out var parent, out var index)) Editor!.Move(node, parent, index);
        e.Handled = true;
    }

    private bool TryGetTarget(DragEventArgs e, out NodeViewModel node, out NodeViewModel parent, out int index)
    {
        node = null!;
        parent = null!;
        index = 0;
        if (Editor is not { } editor || e.Data.GetData(typeof(NodeViewModel)) is not NodeViewModel dragged) return false;
        node = dragged;

        var card = CardFrom(e.OriginalSource as DependencyObject);
        if (card?.DataContext is not NodeViewModel target) return false;

        var y = e.GetPosition(card).Y / Math.Max(1, card.ActualHeight);
        var into = target.IsSection || target.IsExit || (target.IsGroup && y is > 0.25 and < 0.75);
        if (into)
        {
            parent = target;
            index = target.Group!.Children.Count;
        }
        else
        {
            if (target.Parent is null) return false;
            parent = target.Parent;
            var siblings = parent.Group!.Children;
            index = siblings.IndexOf(target.Definition);
            if (index < 0) return false; // Ziel ist Exit-Gruppe
            if (y >= 0.5) index++;
        }
        return editor.CanMove(node, parent, index);
    }

    /// <summary>Die Karte (Border „Card“ aus FlowNodeTemplate) unter dem Mauszeiger.</summary>
    private static FrameworkElement? CardFrom(DependencyObject? source)
    {
        for (var d = source; d is not null; d = ParentOf(d))
        {
            if (d is Border { Name: "Card" } border) return border;
            if (d is TreeViewItem) return null;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject d)
        => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static NodeViewModel? NodeFrom(DependencyObject? source)
        => CardFrom(source)?.DataContext as NodeViewModel;
}
