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
    private void OnApply(object sender, RoutedEventArgs e) { if (Model.CanApply) Apply(Model.Source); }
    private void OnClear(object sender, RoutedEventArgs e) { if (Model.AllowEmpty) Apply(""); }
    private void Apply(string source)
    {
        if (!_isCurrent() || _read() != Model.Original) { DialogError.SetCurrentValue(TextBlock.TextProperty, "Der Projektwert wurde zwischenzeitlich geändert. Dialog schließen und erneut öffnen."); return; }
        if (source != Model.Original) _write(source);
        DialogResult = true;
    }
    private void OnSelectNode(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: VisualExpressionNode node }) Model.Selected = node; }
    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e) => Begin(sender, e);
    private void OnTileMouseDown(object sender, MouseButtonEventArgs e) => Begin(sender, e);
    private void Begin(object sender, MouseButtonEventArgs e)
    { _start = e.GetPosition(this); _drag = (sender as FrameworkElement)?.DataContext; }
    private void OnNodeMouseMove(object sender, MouseEventArgs e) => Drag(sender, e);
    private void OnTileMouseMove(object sender, MouseEventArgs e) => Drag(sender, e);
    private void Drag(object sender, MouseEventArgs e)
    {
        if (!VisualDragGesture.CanStart(e.LeftButton, _drag, _start, e.GetPosition(this))) return;
        var data = _drag; _drag = null;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
    }
    private void OnBlockDragOver(object sender, DragEventArgs e)
    {
        bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (e.Data.GetData(typeof(ExpressionTile)) is ExpressionTile tile)
            e.Effects = !Model.VariablesOnly || tile.Kind == VisualExpressionKind.Variable ? DragDropEffects.Copy : DragDropEffects.None;
        else if (sender is FrameworkElement { DataContext: VisualExpressionNode target } && e.Data.GetData(typeof(VisualExpressionNode)) is VisualExpressionNode node && Model.CanConnect(node, target, copy))
            e.Effects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }
    private void OnBlockDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VisualExpressionNode target }) return;
        if (e.Data.GetData(typeof(ExpressionTile)) is ExpressionTile tile) { Model.Selected = target; Model.Insert(tile); }
        else if (e.Data.GetData(typeof(VisualExpressionNode)) is VisualExpressionNode node)
            Model.Connect(node, target, (e.KeyStates & DragDropKeyStates.ControlKey) != 0);
        e.Handled = true;
    }
}