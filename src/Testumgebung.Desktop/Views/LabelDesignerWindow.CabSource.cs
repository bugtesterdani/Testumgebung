using System.Windows;
using System.Windows.Controls;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class LabelDesignerWindow
{
    private bool _refreshingSource;

    internal void ReportCabSourceError(string message) => Status.Text = message;

    internal void RefreshCabSource()
    {
        var selected = Elements.SelectedItem as LabelElement;
        var visuals = Sheet.Children.OfType<Border>().Where(v => v.Tag is LabelElement)
            .ToDictionary(v => (LabelElement)v.Tag);
        _refreshingSource = true;
        try
        {
            DataContext = null; DataContext = _layout;
            Elements.Items.Refresh();
            Elements.SelectedItem = _layout.Elements.Contains(selected!) ? selected : _layout.Elements.FirstOrDefault();
            Properties.DataContext = Elements.SelectedItem;
            ReconcileVisuals(visuals);
        }
        finally { _refreshingSource = false; }
    }

    private void ReconcileVisuals(Dictionary<LabelElement, Border> visuals)
    {
        Sheet.Width = _layout.WidthDots; Sheet.Height = _layout.HeightDots;
        foreach (var removed in visuals.Where(p => !_layout.Elements.Contains(p.Key))) Sheet.Children.Remove(removed.Value);
        for (var index = 0; index < _layout.Elements.Count; index++)
        {
            var element = _layout.Elements[index];
            if (!visuals.TryGetValue(element, out var visual))
            { AddPreview(element); visual = (Border)Sheet.Children[^1]; }
            if (Sheet.Children.IndexOf(visual) != index)
            { Sheet.Children.Remove(visual); Sheet.Children.Insert(index, visual); }
            Canvas.SetLeft(visual, Math.Clamp(element.X, 0, _layout.WidthDots - 1));
            Canvas.SetTop(visual, Math.Clamp(element.Y, 0, _layout.HeightDots - 1));
            visual.BorderThickness = ReferenceEquals(element, Elements.SelectedItem) ? new Thickness(1) : new Thickness(0);
        }
    }
}
