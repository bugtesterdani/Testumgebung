using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Testumgebung.Desktop.Views;
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
                .Invoke(view, [view, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)]);
        typeof(WaveformPlot).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(object), typeof(MouseEventArgs)])!
            .Invoke(view, [view, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
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
