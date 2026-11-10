using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class FlowGraphView
{
    private EditorView? FindEditorHost()
    {
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is EditorView editor) return editor;
        return null;
    }

    private static Brush CardBrush(NodeViewModel node)
    {
        var color = node.Group?.Parallel == true || node.Parent?.Group?.Parallel == true ? "#EBDFFF" : node.BlockColor;
        if (node.Parent?.Children.FirstOrDefault()?.IsTemplateDecision == true)
            color = ReferenceEquals(node.Parent.Children.ElementAtOrDefault(1), node) ? "#DEF3DF" : "#FFE5D0";
        return (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void AddHeadingButton(DockPanel heading, string label, string hint, Action action)
    {
        var button = new Button { Content = label, ToolTip = hint, Padding = new Thickness(4, 0, 4, 0), Height = 24, Margin = new Thickness(1, 0, 1, 0) };
        button.SetResourceReference(StyleProperty, "ToolButton");
        System.Windows.Automation.AutomationProperties.SetName(button, hint);
        button.Click += (_, _) => action();
        DockPanel.SetDock(button, Dock.Right); heading.Children.Add(button);
    }

    private void OnAddBlock(object sender, RoutedEventArgs eventArgs)
    {
        if (IsRunning) return;
        var key = "node:" + Editor?.SelectedNode?.Id;
        if (!_fields.TryGetValue(key, out var fields)) fields = _fields.Values.FirstOrDefault();
        fields?.AddGraphBlock(Editor?.SelectedNode);
    }

    private void OnNormalView(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor is not { } editor) return;
        var selected = editor.SelectedNode ?? editor.Nodes.FirstOrDefault();
        if (selected is null) return;
        FindEditorHost()?.ShowGraphDetails(selected);
    }

    private void OnViewMenu(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu();
        var fit = new MenuItem { Header = "Alles anzeigen" }; fit.Click += OnFit; menu.Items.Add(fit);
        var arrange = new MenuItem { Header = "Automatisch anordnen" }; arrange.Click += OnArrange; menu.Items.Add(arrange);
        button.ContextMenu = menu; menu.PlacementTarget = button; menu.IsOpen = true;
    }
}
