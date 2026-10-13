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
        => BeginGesture(e.GetPosition(PlotSurface), e.ClickCount);

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
        => MoveGesture(e.GetPosition(PlotSurface).X, e.LeftButton == MouseButtonState.Pressed);

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
        => EndGesture(e.GetPosition(PlotSurface).X);

    internal void EndGesture(double positionX)
    {
        PlotSurface.ReleaseMouseCapture();
        DragRect.Visibility = Visibility.Collapsed;
        if (_start is not { } s || Vm is not { } vm) return;
        _start = null;
        var x = Math.Clamp(positionX, 0, WaveformPlotViewModel.PlotWidth);
        if (Math.Abs(x - s.X) >= 4) vm.ZoomToX(s.X, x);
        else vm.SetCursorAtX(x);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { HasData: true } vm) return;
        vm.WheelZoom(e.GetPosition(PlotSurface).X, e.Delta > 0);
        e.Handled = true;
    }
}
