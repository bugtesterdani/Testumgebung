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
        nameof(ShowToolbar), typeof(bool), typeof(WaveformPlot), new PropertyMetadata(true, (definition, entry) =>
            ((WaveformPlot)definition).Toolbar.Visibility = (bool)entry.NewValue ? Visibility.Visible : Visibility.Collapsed));

    private Point? _start;

    public WaveformPlot() => InitializeComponent();

    public bool ShowToolbar
    {
        get => (bool)GetValue(ShowToolbarProperty);
        set => SetValue(ShowToolbarProperty, value);
    }

    private WaveformPlotViewModel? Vm => DataContext as WaveformPlotViewModel;

    private void OnMouseDown(object sender, MouseButtonEventArgs eventArgs)
        => BeginGesture(eventArgs.GetPosition(PlotSurface), eventArgs.ClickCount);

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

    private void OnMouseMove(object sender, MouseEventArgs eventArgs)
        => MoveGesture(eventArgs.GetPosition(PlotSurface).X, eventArgs.LeftButton == MouseButtonState.Pressed);

    internal void MoveGesture(double positionX, bool pressed)
    {
        if (_start is not { } point || !pressed) return;
        var xValue = Math.Clamp(positionX, 0, WaveformPlotViewModel.PlotWidth);
        if (Math.Abs(xValue - point.X) < 4) return;
        Canvas.SetLeft(DragRect, Math.Min(xValue, point.X));
        DragRect.Width = Math.Abs(xValue - point.X);
        DragRect.Visibility = Visibility.Visible;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs eventArgs)
        => EndGesture(eventArgs.GetPosition(PlotSurface).X);

    internal void EndGesture(double positionX)
    {
        PlotSurface.ReleaseMouseCapture();
        DragRect.Visibility = Visibility.Collapsed;
        if (_start is not { } point || Vm is not { } vm) return;
        _start = null;
        var xValue = Math.Clamp(positionX, 0, WaveformPlotViewModel.PlotWidth);
        if (Math.Abs(xValue - point.X) >= 4) vm.ZoomToX(point.X, xValue);
        else vm.SetCursorAtX(xValue);
    }

    private void OnWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (Vm is not { HasData: true } vm) return;
        vm.WheelZoom(eventArgs.GetPosition(PlotSurface).X, eventArgs.Delta > 0);
        eventArgs.Handled = true;
    }
}
