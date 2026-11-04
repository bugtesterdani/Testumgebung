using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Testumgebung.Contracts;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyGraphWorkspace(Dispatcher dispatcher)
    {
        var loop = new GroupDefinition("graph-loop", "Prüfen") { Repetition = Repetition.Times(2) };
        loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.SetValue, "graph-set", "Spannung setzen", new Dictionary<string, string> { ["Ziel"] = "Spannung", ["Ausdruck"] = "12.5" }));
        loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "graph-a"));
        loop.Children.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "graph-b"));
        var definition = new TestDefinition("Knotenbeispiel", "1", new("graph-start", "Start"), loop, new("graph-end", "Ende"));
        definition.Root.Variables.Add(new("Spannung", VarType.Double, 0d));
        var shell = new ShellViewModel(new SynchronizationContextDispatcher(SynchronizationContext.Current!), project: new TestProject(definition));
        shell.Login();
        var editor = shell.Editor;
        var conditional = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == "ifelse"), editor.FindNode("graph-loop")!, 3);
        var parallel = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == "parallel"), editor.FindNode("graph-loop")!, 4);
        var parallelId = parallel.Id;
        editor.InsertAt(editor.Palette.SelectMany(p => p.Items).First(p => p.BlockType == BlockCatalog.Wait), parallel, 0);
        editor.InsertAt(editor.Palette.SelectMany(p => p.Items).First(p => p.BlockType == BlockCatalog.Wait), editor.FindNode(parallelId)!, 1);
        var view = new EditorView { DataContext = shell, Background = Brushes.White };
        var host = new Window { Content = view, Width = 1600, Height = 1000, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            var before = new ProjectSerializer().Serialize(editor.Project);
            editor.GraphView = true; Sta.Flush(dispatcher); view.UpdateLayout(); Sta.Flush(dispatcher);
            var graph = Descendants(view).OfType<FlowGraphView>().Single(); graph.RefreshScene(); Sta.Flush(dispatcher);
            Assert.NotNull(graph.Scene); Assert.True(graph.Scene!.Cards.Count > 8);
            Assert.Contains(graph.Scene.Links, l => l.Kind == GraphLinkKind.Then);
            Assert.Contains(graph.Scene.Links, l => l.Kind == GraphLinkKind.Parallel);
            Assert.False(((FrameworkElement)view.FindName("PalettePanel")).IsVisible);
            var oldZoom = graph.Camera.Zoom; Click(graph, "−"); Assert.True(graph.Camera.Zoom < oldZoom);
            var zoomIn = Descendants(graph).OfType<Button>().Single(b => Equals(b.ToolTip, "Hineinzoomen"));
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(zoomIn, []);
            Click(graph, "100 %"); Assert.Equal(1, graph.Camera.Zoom);
            graph.Camera.Pan(-200, 120); graph.Zoom(.7, new Point(300, 200));
            var canvas = (Canvas)graph.FindName("World");
            var card = canvas.Children.OfType<Border>().Single(b => b.Tag is GraphCard c && c.Id == "node:graph-set");
            var oldX = Canvas.GetLeft(card); graph.MoveCard("node:graph-set", -50, 40);
            Assert.Equal(oldX - 50, Canvas.GetLeft(card));
            Assert.Equal(before, new ProjectSerializer().Serialize(editor.Project));
            GraphViewOption(graph, "Automatisch anordnen"); GraphViewOption(graph, "Alles anzeigen");
            VerifyCompactGraph(view, graph, editor, dispatcher);
            VerifyGraphPointers(graph, editor, dispatcher);
            CaptureVisual(host, "graph-overview.png");
            var breakpoint = Descendants(graph).OfType<Button>().Single(b => b.DataContext is NodeViewModel { Id: "graph-set" } && b.ToolTip?.ToString()?.StartsWith("Nach diesem Block") == true);
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(breakpoint, []); Sta.Flush(dispatcher);
            Assert.True(shell.SelectedStation.Debug.Controller.HasBreakpoint("graph-set"));
            typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(breakpoint, []); Sta.Flush(dispatcher);
            Assert.False(shell.SelectedStation.Debug.Controller.HasBreakpoint("graph-set"));
            VerifyGraphKeys(graph, editor);
            // A real station run stops at the existing POST-execution breakpoint.
            editor.ToggleBreakpoint(editor.FindNode("graph-set"));
            var station = shell.SelectedStation;
            shell.WorkspaceToolTab = 0;
            var task = station.StartSimulationAsync();
            PumpGraph(dispatcher, () => station.Debug.IsPaused);
            graph.RefreshScene(); Sta.Flush(dispatcher); view.UpdateLayout();
            Assert.Equal("graph-set", station.Debug.Pause!.NodeId);
            Assert.Equal(0, shell.WorkspaceToolTab);
            Assert.True(graph.Camera.Zoom >= .75);
            Assert.Equal(12.5d, station.Debug.Variables.Single(v => v.Name == "Spannung").Variable.Value);
            var values = Descendants(graph).OfType<DataGrid>().Single(g => ReferenceEquals(g.ItemsSource, station.Debug.Variables));
            Assert.True(values.IsReadOnly); Assert.True(values.ActualWidth > 100);
            Assert.Contains(Descendants(graph).OfType<TextBlock>(), t => t.Text.Contains("vor Ergebnisweitergabe"));
            CaptureVisual(host, "graph-debug.png");
            editor.ToggleBreakpoint(editor.FindNode("graph-set"));
            Click(graph, "Fortsetzen · F5"); PumpGraph(dispatcher, () => task.IsCompleted);
            Assert.True(task.IsCompletedSuccessfully); graph.RefreshScene(); Sta.Flush(dispatcher);
            Assert.Empty(station.Debug.Variables);
            VerifyGraphDetails(view, graph, editor, dispatcher);
        }
        finally { shell.SelectedStation.RequestAbort(); host.Close(); Sta.Flush(dispatcher); }
        VerifyCompactReference(dispatcher);
    }
    private static void VerifyGraphKeys(FlowGraphView graph, EditorViewModel editor)
    {
        editor.SelectedNode = editor.FindNode("graph-set");
        void Key(Key key, object source)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(graph)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent, Source = source };
            typeof(FlowGraphView).GetMethod("OnGraphKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(graph, [graph, args]);
        }
        Key(System.Windows.Input.Key.F9, graph); Assert.True(editor.Breakpoints!.HasBreakpoint("graph-set"));
        Key(System.Windows.Input.Key.F9, graph); Assert.False(editor.Breakpoints!.HasBreakpoint("graph-set"));
        Key(System.Windows.Input.Key.Enter, graph); Assert.False(editor.Breakpoints!.HasBreakpoint("graph-set"));
        var input = Descendants(graph).OfType<TextBox>().First();
        Key(System.Windows.Input.Key.F9, input); Assert.False(editor.Breakpoints!.HasBreakpoint("graph-set"));
    }
    private static void PumpGraph(Dispatcher dispatcher, Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Sta.Flush(dispatcher); Thread.Sleep(5); }
        Assert.True(completed(), "Graphen-Debugger hat den erwarteten Zustand nicht erreicht.");
    }
    private static void VerifyGraphPointers(FlowGraphView graph, EditorViewModel editor, Dispatcher dispatcher)
    {
        void Invoke(string method, object sender, object args) => typeof(FlowGraphView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(graph, [sender, args]);
        var viewport = (Border)graph.FindName("Viewport");
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = Mouse.MouseWheelEvent, Source = viewport };
        var zoom = graph.Camera.Zoom; Invoke("OnWheel", viewport, wheel); Assert.True(graph.Camera.Zoom > zoom);
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.MouseDownEvent, Source = viewport };
        Invoke("OnPointerDown", viewport, down);
        Invoke("OnPointerMove", viewport, new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        Invoke("OnPointerUp", viewport, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle) { RoutedEvent = Mouse.MouseUpEvent });
        var canvas = (Canvas)graph.FindName("World");
        var input = Descendants(canvas).OfType<Ellipse>().First(e => e.Tag is NodeViewModel n && n.Id == "graph-b");
        var hit = typeof(FlowGraphView).GetMethod("HitInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(graph, [input.TranslatePoint(new Point(8, 8), canvas)]);
        Assert.Same(editor.FindNode("graph-b"), hit);
        var output = Descendants(canvas).OfType<Ellipse>().First(e => e.Tag is GraphSocket socket && socket.Node.Id == "graph-set");
        Invoke("OnOutputDown", output, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        Invoke("OnPointerMove", viewport, new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        Assert.True(graph.FinishConnection(editor.FindNode("graph-b")));
        viewport.ReleaseMouseCapture(); Sta.Flush(dispatcher); graph.RefreshScene();
        Assert.Equal("graph-b", editor.Project.Definition.Loop.Children[1].Id);
        var cameraZoom = graph.Camera.Zoom; var cameraX = graph.Camera.X;
        editor.UndoCommand.Execute(null); Sta.Flush(dispatcher); graph.RefreshScene();
        Assert.Equal(cameraZoom, graph.Camera.Zoom); Assert.Equal(cameraX, graph.Camera.X);
        output = Descendants(canvas).OfType<Ellipse>().First(e => e.Tag is GraphSocket socket && socket.Node.Id == "graph-set");
        Invoke("OnOutputDown", output, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        Assert.False(graph.FinishConnection(editor.FindNode("graph-start"))); viewport.ReleaseMouseCapture();
    }
}
