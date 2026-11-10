/**
 * @file
 * @brief WPF-Anbindung für Kurveninteraktion. Ereignisse und Bindings verbinden diese Ansicht mit ihrem Datenkontext.
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Kurvenanzeige: Klick setzt den Cursor, Aufziehen zoomt, Mausrad zoomt um die Mausposition, Doppelklick zeigt alles.</summary>
public partial class WaveformPlot : UserControl
{
    public static readonly DependencyProperty ShowToolbarProperty = DependencyProperty.Register(
        nameof(ShowToolbar), typeof(bool), typeof(WaveformPlot), new PropertyMetadata(true, (d, e) =>
            ((WaveformPlot)d).Toolbar.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    private Point? _start;

    public WaveformPlot() => InitializeComponent();

    public bool ShowToolbar
    {
        get => (bool)GetValue(ShowToolbarProperty);
        set => SetValue(ShowToolbarProperty, value);
    }

    private WaveformPlotViewModel? Vm => DataContext as WaveformPlotViewModel;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(PlotSurface);
        if (Vm?.PointEditor is not { IsEnabled: true } editor || e.ClickCount == 2)
        {
            BeginGesture(position, e.ClickCount);
            return;
        }

        if (editor.BeginDrag(position.X, position.Y))
        {
            PlotSurface.Focus();
            if (!PlotSurface.CaptureMouse()) editor.CancelDrag();
        }
        else Vm?.SetCursorAtX(position.X);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var isPinGesture = e.ChangedButton == MouseButton.Middle
            || (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        if (!isPinGesture || Vm?.PointEditor is not { IsEnabled: true } editor) return;
        var position = e.GetPosition(PlotSurface);
        editor.TogglePin(position.X, position.Y);
        e.Handled = true;
    }

    private void OnPlotKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm?.PointEditor is not { IsDragging: true } editor) return;
        editor.CancelDrag();
        PlotSurface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        Vm?.PointEditor?.CancelDrag();
        _start = null;
        DragRect.Visibility = Visibility.Collapsed;
    }

    internal void BeginGesture(Point position, int clickCount)
    {
        if (Vm is not { HasData: true } vm) return;
        if (clickCount == 2)
        {
            vm.ZoomAllCommand.Execute(null);
            return;
        }
        _start = position;
        PlotSurface.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(PlotSurface);
        if (Vm?.PointEditor is { IsDragging: true } editor)
        {
            editor.MoveDrag(position.Y);
            return;
        }
        MoveGesture(position.X, e.LeftButton == MouseButtonState.Pressed);
    }

    internal void MoveGesture(double positionX, bool pressed)
    {
        if (_start is not { } s || !pressed) return;
        var x = Math.Clamp(positionX, 0, WaveformPlotViewModel.PlotWidth);
        if (Math.Abs(x - s.X) < 4) return;
        Canvas.SetLeft(DragRect, Math.Min(x, s.X));
        DragRect.Width = Math.Abs(x - s.X);
        DragRect.Visibility = Visibility.Visible;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(PlotSurface);
        if (Vm?.PointEditor is { IsDragging: true } editor)
        {
            editor.EndDrag(position.Y);
            PlotSurface.ReleaseMouseCapture();
            return;
        }
        EndGesture(position.X);
    }

    internal void EndGesture(double positionX)
    {
        var start = _start;
        _start = null;
        PlotSurface.ReleaseMouseCapture();
        DragRect.Visibility = Visibility.Collapsed;
        if (start is not { } s || Vm is not { } vm) return;
        var x = Math.Clamp(positionX, 0, WaveformPlotViewModel.PlotWidth);
        if (Math.Abs(x - s.X) >= 4) vm.ZoomToX(s.X, x);
        else vm.SetCursorAtX(x);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { HasData: true } vm) return;
        if (vm.PointEditor is not { IsDragging: true }) vm.WheelZoom(e.GetPosition(PlotSurface).X, e.Delta > 0);
        e.Handled = true;
    }
}
