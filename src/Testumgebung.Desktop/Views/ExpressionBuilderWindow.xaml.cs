using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Transaktionaler Ausdrucksdialog: Abbruch verändert das Projekt nicht, Übernehmen prüft veraltete Eingaben.</summary>
public partial class ExpressionBuilderWindow : Window
{
    private readonly Func<string> _read;
    private readonly Action<string> _write;
    private readonly Func<bool> _isCurrent;
    private Point _start;
    private object? _drag;
    public ExpressionBuilderWindow(ExpressionBuilderViewModel model, Func<string> read, Action<string> write, string title, Func<bool>? isCurrent = null)
    {
        InitializeComponent(); DataContext = model; _read = read; _write = write; _isCurrent = isCurrent ?? (() => true); Title = title;
    }
    private ExpressionBuilderViewModel Model => (ExpressionBuilderViewModel)DataContext;
    private void OnApply(object sender, RoutedEventArgs eventArgs) { if (Model.CanApply) Apply(Model.Source); }
    private void OnClear(object sender, RoutedEventArgs eventArgs) { if (Model.AllowEmpty) Apply(""); }
    private void Apply(string source)
    {
        if (!_isCurrent() || _read() != Model.Original) { DialogError.SetCurrentValue(TextBlock.TextProperty, "Der Projektwert wurde zwischenzeitlich geändert. Dialog schließen und erneut öffnen."); return; }
        if (source != Model.Original) _write(source);
        DialogResult = true;
    }
    private void OnSelectNode(object sender, RoutedEventArgs eventArgs) { if (sender is FrameworkElement { DataContext: VisualExpressionNode node }) Model.Selected = node; }
    private void OnNodeMouseDown(object sender, MouseButtonEventArgs eventArgs) => Begin(sender, eventArgs);
    private void OnTileMouseDown(object sender, MouseButtonEventArgs eventArgs) => Begin(sender, eventArgs);
    private void Begin(object sender, MouseButtonEventArgs eventArgs)
    { _start = eventArgs.GetPosition(this); _drag = (sender as FrameworkElement)?.DataContext; }
    private void OnNodeMouseMove(object sender, MouseEventArgs eventArgs) => Drag(sender, eventArgs);
    private void OnTileMouseMove(object sender, MouseEventArgs eventArgs) => Drag(sender, eventArgs);
    private void Drag(object sender, MouseEventArgs eventArgs)
    {
        if (!VisualDragGesture.CanStart(eventArgs.LeftButton, _drag, _start, eventArgs.GetPosition(this))) return;
        var data = _drag; _drag = null;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
    }
    private void OnBlockDragOver(object sender, DragEventArgs eventArgs)
    {
        bool copy = (eventArgs.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (eventArgs.Data.GetData(typeof(ExpressionTile)) is ExpressionTile tile)
            eventArgs.Effects = !Model.VariablesOnly || tile.Kind == VisualExpressionKind.Variable ? DragDropEffects.Copy : DragDropEffects.None;
        else if (sender is FrameworkElement { DataContext: VisualExpressionNode target } && eventArgs.Data.GetData(typeof(VisualExpressionNode)) is VisualExpressionNode node && Model.CanConnect(node, target, copy))
            eventArgs.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        else eventArgs.Effects = DragDropEffects.None;
        eventArgs.Handled = true;
    }
    private void OnBlockDrop(object sender, DragEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: VisualExpressionNode target }) return;
        if (eventArgs.Data.GetData(typeof(ExpressionTile)) is ExpressionTile tile) { Model.Selected = target; Model.Insert(tile); }
        else if (eventArgs.Data.GetData(typeof(VisualExpressionNode)) is VisualExpressionNode node)
            Model.Connect(node, target, (eventArgs.KeyStates & DragDropKeyStates.ControlKey) != 0);
        eventArgs.Handled = true;
    }
}