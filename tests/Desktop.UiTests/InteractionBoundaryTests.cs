using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Testumgebung.Desktop.Views;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed class InteractionBoundaryTests
{
    internal static void VerifyPlotGestures()
    {
        var view = new WaveformPlot();
        view.BeginGesture(new Point(10, 0), 1);
        view.MoveGesture(100, true);
        view.EndGesture(100);
        var vm = new WaveformPlotViewModel();
        view.DataContext = vm;
        view.BeginGesture(new Point(10, 0), 1);
        vm.SetData([new PlotSource("U", "V", Enumerable.Range(0, 100).Select(i => (double)i).ToArray())], 100, null);
        var rectangle = (FrameworkElement)view.FindName("DragRect");
        view.MoveGesture(100, true);
        Assert.Equal(Visibility.Collapsed, rectangle.Visibility);
        view.BeginGesture(new Point(10, 0), 1);
        view.MoveGesture(100, false);
        view.MoveGesture(12, true);
        Assert.Equal(Visibility.Collapsed, rectangle.Visibility);
        view.MoveGesture(100, true);
        Assert.Equal(Visibility.Visible, rectangle.Visibility);
        Assert.Equal(90, rectangle.Width);
        Assert.Equal(10, Canvas.GetLeft(rectangle));
        view.EndGesture(100);
        Assert.Equal(Visibility.Collapsed, rectangle.Visibility);
        Assert.True(vm.ViewEnd - vm.ViewStart < vm.Duration);
        view.BeginGesture(new Point(10, 0), 2);
        Assert.Equal(vm.Duration, vm.ViewEnd);
        view.BeginGesture(new Point(10, 0), 1);
        view.EndGesture(11);
        Assert.NotNull(vm.CursorTime);
        var cursor = vm.CursorTime;
        view.EndGesture(500);
        Assert.Equal(cursor, vm.CursorTime);
        foreach (var name in new[] { "OnMouseDown", "OnMouseUp" })
            typeof(WaveformPlot).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, [typeof(object), typeof(MouseButtonEventArgs)])!
                .Invoke(view, [view, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = name == "OnMouseDown" ? Mouse.MouseDownEvent : Mouse.MouseUpEvent }]);
        typeof(WaveformPlot).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(object), typeof(MouseEventArgs)])!
            .Invoke(view, [view, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
        VerifyPointGestures(view);
    }

    private static void VerifyPointGestures(WaveformPlot view)
    {
        var editor = new EditorViewModel(SampleProjects.AcPruefung());
        var curves = new CurvesViewModel(editor, null, () => "Tester", () => null, ImmediateDispatcher.Instance);
        view.DataContext = curves.Plot;
        var plotSurface = (FrameworkElement)view.FindName("PlotSurface");
        var noDataWheel = RaiseWheel(plotSurface);
        Assert.False(noDataWheel.Handled);
        curves.SelectedCurve = Assert.Single(curves.Curves);
        curves.NewFromSelectedCommand.Execute(null);
        var firstSample = curves.Plot.Series[0].Points[0];
        var firstPoint = new Point(firstSample.X, firstSample.Y);
        Assert.False(view.TogglePointPin(firstPoint));

        curves.PointEditor.IsEnabled = true;
        Assert.True(WaveformPlot.IsPinGesture(MouseButton.Middle, ModifierKeys.None));
        Assert.True(WaveformPlot.IsPinGesture(MouseButton.Left, ModifierKeys.Control));
        Assert.False(WaveformPlot.IsPinGesture(MouseButton.Left, ModifierKeys.None));
        Assert.False(WaveformPlot.IsPinGesture(MouseButton.Right, ModifierKeys.Control));

        var middleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        plotSurface.RaiseEvent(middleClick);
        Assert.True(middleClick.Handled);
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        plotSurface.RaiseEvent(rightClick);
        Assert.False(rightClick.Handled);

        // A routed event has the current mouse position. Use exact plot coordinates to test the pin and drag.
        foreach (var pin in curves.PointEditor.Pins) pin.RemoveCommand.Execute(null);
        Assert.True(view.TogglePointPin(firstPoint));
        Assert.Single(curves.PointEditor.Pins);
        Assert.True(view.BeginPointGesture(firstPoint, 1));
        Assert.False(curves.PointEditor.IsDragging);
        Assert.False(view.BeginPointGesture(firstPoint, 2));
        Assert.True(view.TogglePointPin(firstPoint));
        Assert.Empty(curves.PointEditor.Pins);
        Assert.True(view.BeginPointGesture(firstPoint, 1));

        // Capture can fail on a view without a window. Exercise cancellation against an active drag explicitly.
        curves.PointEditor.CancelDrag();
        Assert.True(curves.PointEditor.BeginDrag(firstPoint.X, firstPoint.Y));
        var beforeWheel = curves.Plot.ViewEnd - curves.Plot.ViewStart;
        Assert.True(RaiseWheel(plotSurface).Handled);
        Assert.Equal(beforeWheel, curves.Plot.ViewEnd - curves.Plot.ViewStart);
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Curve gestures", 1, 1));
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
        plotSurface.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.False(curves.PointEditor.IsDragging);
        Assert.False(view.CancelPointGesture());
        Assert.True(RaiseWheel(plotSurface).Handled);
        Assert.True(curves.Plot.ViewEnd - curves.Plot.ViewStart < beforeWheel);
    }

    private static MouseWheelEventArgs RaiseWheel(FrameworkElement surface)
    {
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent };
        surface.RaiseEvent(wheel);
        return wheel;
    }

    [Fact]
    public void EditorResolvesBothVisualAndLogicalParents()
        => Sta.Run(_ =>
        {
            var method = typeof(EditorView).GetMethod("ParentOf", BindingFlags.Static | BindingFlags.NonPublic)!;
            var panel = new StackPanel();
            var child = new TextBlock();
            panel.Children.Add(child);
            Assert.Same(panel, method.Invoke(null, [child]));
            var span = new Span();
            var run = new Run("text");
            span.Inlines.Add(run);
            Assert.Same(span, method.Invoke(null, [run]));
        });
}
