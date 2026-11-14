/**
 * @file
 * @brief WPF-Anbindung für Ablaufeditor. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
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
    private void OnParameterTable(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ParameterRowViewModel row } source)
            ParameterTableDialog.Edit(source, row);
    }

    private Point _dragStart;
    private NodeViewModel? _dragNode;

    public EditorView()
    {
        InitializeComponent();
        Loaded += (_, _) => TrackBlockLayout();
        DataContextChanged += (_, _) => TrackBlockLayout();
        Unloaded += (_, _) => { if (_layoutEditor is not null) _layoutEditor.PropertyChanged -= OnBlockLayoutChanged; _layoutEditor = null; };
        // Strg+F: Suche (auch aus der Kartenliste)
        InputBindings.Add(new KeyBinding(new FocusSearchCommand(this), Key.F, ModifierKeys.Control));
    }

    private EditorViewModel? _layoutEditor;
    private void TrackBlockLayout()
    {
        if (_layoutEditor is not null) _layoutEditor.PropertyChanged -= OnBlockLayoutChanged;
        _layoutEditor = Editor;
        if (_layoutEditor is not null) _layoutEditor.PropertyChanged += OnBlockLayoutChanged;
        ApplyBlockLayout();
    }
    private void OnBlockLayoutChanged(object? sender, PropertyChangedEventArgs eventArgs)
    { if (eventArgs.PropertyName is nameof(EditorViewModel.VisualBlocks) or nameof(EditorViewModel.GraphView) or nameof(EditorViewModel.Project)) ApplyBlockLayout(); }
    private void ApplyBlockLayout()
    {
        var visibility = Editor is { VisualBlocks: true } or { GraphView: true } ? Visibility.Collapsed : Visibility.Visible;
        foreach (var panel in new FrameworkElement[] { PalettePanel, PropertiesPanel, PaletteSplitter, PropertiesSplitter }) panel.Visibility = visibility;
        ProjectInline.Content = null;
    }
    // Texte markieren, Listen öffnen und Zahlen bearbeiten darf weder einen
    // Ablaufblock ziehen noch dessen Entf-/Strg+C-Befehle auslösen.
    private static bool IsBlockInput(DependencyObject? source)
    {
        for (var source2 = source; source2 is not null && source2 is not TreeViewItem; source2 = ParentOf(source2))
            if (source2 is TextBoxBase or ComboBox or ButtonBase or Testumgebung.Desktop.Controls.NumberedCodeEditor) return true;
        return false;
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

    private void OnNodeHistory(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not ShellViewModel shell || sender is not FrameworkElement { DataContext: NodeOptionsViewModel options }) return;
        var model = new NodeHistoryViewModel(shell.Releases?.Service, shell.Editor.Project.Definition.Name, options.NodeId, options.NodeName);
        var window = new NodeHistoryWindow(model) { Owner = Window.GetWindow(this) };
        window.Show();
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> eventArgs)
    {
        if (Editor is { } editor && eventArgs.NewValue is NodeViewModel node) editor.SelectedNode = node;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (IsBlockInput(eventArgs.OriginalSource as DependencyObject)) return;
        if (TryHandleTreeKey(eventArgs.Key, Keyboard.Modifiers)) eventArgs.Handled = true;
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

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs eventArgs)
    {
        _dragNode = null;
        if (IsBlockInput(eventArgs.OriginalSource as DependencyObject)) return;
        _dragStart = eventArgs.GetPosition(FlowTree);
        _dragNode = NodeFrom(eventArgs.OriginalSource as DependencyObject);
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (TryBeginTreeDrag(eventArgs.LeftButton, eventArgs.GetPosition(FlowTree)) is { } node)
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

    private void OnTreeDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = TryGetTarget(eventArgs, out _, out _, out _) ? DragDropEffects.Move
            : TryGetPaletteTarget(eventArgs, out _, out _, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnTreeDrop(object sender, DragEventArgs eventArgs)
    {
        if (TryGetTarget(eventArgs, out var node, out var parent, out var index)) Editor!.Move(node, parent, index);
        else if (TryGetPaletteTarget(eventArgs, out var palette, out parent, out index)) Editor!.InsertAt(palette, parent, index);
        eventArgs.Handled = true;
    }

    private bool TryGetTarget(DragEventArgs eventArgs, out NodeViewModel node, out NodeViewModel parent, out int index)
    {
        node = null!;
        parent = null!;
        index = 0;
        if (Editor is not { } editor || eventArgs.Data.GetData(typeof(NodeViewModel)) is not NodeViewModel dragged) return false;
        node = dragged;

        if (!ResolveDropPosition(eventArgs, out parent, out index)) return false;
        return editor.CanMove(node, parent, index);
    }

    /// <summary>Die Karte (Border „Card“ aus FlowNodeTemplate) unter dem Mauszeiger.</summary>
    private static FrameworkElement? CardFrom(DependencyObject? source)
    {
        for (var source2 = source; source2 is not null; source2 = ParentOf(source2))
        {
            if (source2 is Border { Name: "Card" or "EmptySlot" } border) return border;
            if (source2 is TreeViewItem) return null;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element)
        => element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private static NodeViewModel? NodeFrom(DependencyObject? source)
        => CardFrom(source)?.DataContext as NodeViewModel;
}
