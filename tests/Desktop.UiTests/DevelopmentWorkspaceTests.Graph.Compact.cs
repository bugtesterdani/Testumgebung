using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Engine.Blocks;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCompactReference(Dispatcher dispatcher)
    {
        StepDefinition Step(string type, string id, string name, params (string Key, string Value)[] values)
            => BlockCatalog.Default.Create(type, id, name, values.ToDictionary(v => v.Key, v => v.Value));
        var loop = new GroupDefinition("compact-loop", "Prüfen");
        var definition = new TestDefinition("Kompakte Prüfung", "1", new("compact-start", "Start"), loop, new("compact-end", "Ende"));
        foreach (var variable in new[] { new VariableDefinition("Seriennummer", VarType.String), new VariableDefinition("Spannung", VarType.Double, 12d),
                     new VariableDefinition("Strom", VarType.Double), new VariableDefinition("Temperatur", VarType.Double) }) definition.Root.Variables.Add(variable);
        loop.Children.Add(Step(BlockCatalog.Operator, "serial", "Seriennummer erfassen", ("Art", "Eingabe"), ("Datentyp", "Text"), ("Text", "Seriennummer scannen"), ("Ziel", "Seriennummer")));
        var project = new TestProject(definition);
        project.Simulation.Add(new("Netzteil A", "ai0", "A", 1.2));
        project.Simulation.Add(new("Sensor 1", "ai1", "°C", 25));
        var shell = new ShellViewModel(new SynchronizationContextDispatcher(SynchronizationContext.Current!), project: project); shell.Login();
        var editor = shell.Editor;
        var conditional = editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == "ifelse"), editor.FindNode(loop.Id)!, 1);
        conditional.Children[0].Step!.Parameters["Ausdruck"] = "Spannung >= 10";
        var parallel = new GroupDefinition("compact-parallel", "Parallel prüfen") { Parallel = true };
        parallel.Children.Add(Step(BlockCatalog.Measure, "current", "Strom messen", ("Ressource", "Netzteil A"), ("Ziel", "Strom")));
        parallel.Children.Add(Step(BlockCatalog.Measure, "temperature", "Temperatur lesen", ("Ressource", "Sensor 1"), ("Ziel", "Temperatur")));
        conditional.Children[1].Group!.Children.Add(parallel);
        conditional.Children[2].Group!.Children.Add(Step(BlockCatalog.RaiseError, "fault", "Fehler anzeigen", ("Meldung", "Spannung zu niedrig")));
        loop.Children.Add(Step(BlockCatalog.SetRating, "result", "Prüfergebnis", ("Bewertung", "PASS")));
        editor.RebuildTree(); editor.GraphView = true;
        var view = new EditorView { DataContext = shell, Background = System.Windows.Media.Brushes.White };
        var window = new Window { Content = view, Width = 1586, Height = 1050, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        window.Show();
        try
        {
            Sta.Flush(dispatcher); view.UpdateLayout(); Sta.Flush(dispatcher);
            var graph = Descendants(view).OfType<FlowGraphView>().Single(); graph.RefreshScene(); Sta.Flush(dispatcher);
            GraphViewOption(graph, "Alles anzeigen");
            CaptureVisual(window, "graph-compact-reference.png");
            var resource = Descendants(graph).OfType<ComboBox>().Single(c => c.DataContext is ParameterRowViewModel r && r.NodeId == "current" && r.Kind == ParameterKind.Resource);
            Assert.Contains("Netzteil A", resource.ItemsSource.Cast<string>());
            resource.IsDropDownOpen = true; Sta.Flush(dispatcher); resource.IsDropDownOpen = false;
            var hiddenBranch = editor.FindNode(conditional.Id)!.Children[1];
            var paused = typeof(FlowGraphView).GetMethod("PausedCard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(graph, [hiddenBranch.Id, true]);
            Assert.NotNull(paused);
            Assert.Equal("Netzteil A", editor.FindNode("current")!.Step!.Parameters["Ressource"]);
            foreach (var preset in new[] { "repeat", "while", "until", "when" }) editor.InsertAt(EditorViewModel.ControlBlocks.Single(p => p.Preset == preset), editor.FindNode(loop.Id)!, loop.Children.Count);
            var duration = new GroupDefinition("compact-duration", "Zeitabschnitt") { Repetition = Repetition.For(TimeSpan.FromSeconds(2)) };
            loop.Children.Add(duration); editor.RebuildTree(); Sta.Flush(dispatcher); graph.RefreshScene();
            Assert.All(Descendants(graph).OfType<InlineBlockEditor>(), b => Assert.True(b.Compact));
        }
        finally { window.Close(); Sta.Flush(dispatcher); }
    }

    private static void GraphViewOption(FlowGraphView graph, string label)
    {
        Click(graph, "Ansicht ▾");
        var button = Descendants(graph).OfType<Button>().Single(b => Equals(b.Content, "Ansicht ▾"));
        var item = button.ContextMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Header, label));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        button.ContextMenu.IsOpen = false;
        if (label == "Alles anzeigen")
        {
            var canvas = (System.Windows.Controls.Canvas)graph.FindName("World");
            var viewport = (FrameworkElement)graph.FindName("Viewport");
            canvas.UpdateLayout();
            var bounds = System.Windows.Media.VisualTreeHelper.GetDescendantBounds(canvas);
            var visible = canvas.TransformToAncestor(viewport).TransformBounds(bounds);
            Assert.True(Math.Abs(visible.Left + visible.Width / 2 - viewport.ActualWidth / 2) <= 2,
                $"Viewport={viewport.ActualWidth}; bounds={bounds}; visible={visible}; camera={graph.Camera.X},{graph.Camera.Y},{graph.Camera.Zoom}; origin={canvas.TransformToAncestor(viewport).Transform(new Point())}; offset={System.Windows.Media.VisualTreeHelper.GetOffset(canvas)}");
        }
    }

    private static void VerifyCompactGraph(EditorView view, FlowGraphView graph, EditorViewModel editor, Dispatcher dispatcher)
    {
        var block = Descendants(graph).OfType<InlineBlockEditor>().Single(b => b.DataContext is NodeViewModel { Id: "graph-set" });
        Assert.True(block.Compact);
        Assert.DoesNotContain(Descendants(block), e => e is Expander or DataGrid or Testumgebung.Desktop.Controls.NumberedCodeEditor);
        var rows = Descendants(block).OfType<Control>().Select(c => c.DataContext).OfType<ParameterRowViewModel>()
            .Select(p => p.Descriptor.Name).Distinct().Order().ToArray();
        Assert.Equal(new[] { "Ausdruck", "Ziel" }, rows);
        Assert.All(graph.Scene!.Cards, card => Assert.InRange(card.Width, 200, 360));
        editor.SelectedNode = editor.FindNode("graph-set");
        var oldCount = editor.Project.Definition.Loop.Children.Count;
        Click(graph, "+ Block");
        var menu = block.ContextMenu!;
        Assert.True(menu.IsOpen);
        var wait = menu.Items.OfType<MenuItem>().SelectMany(i => i.Items.OfType<MenuItem>()).Single(i => i.Tag is PaletteItem { BlockType: BlockCatalog.Wait });
        wait.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
        Assert.Equal(oldCount + 1, editor.Project.Definition.Loop.Children.Count);
        editor.UndoCommand.Execute(null); Sta.Flush(dispatcher); graph.RefreshScene();
        Assert.Equal(oldCount, editor.Project.Definition.Loop.Children.Count);
        var add = Descendants(graph).OfType<Button>().Single(b => Equals(b.ToolTip, "Block hinzufügen") && b.DataContext is NodeViewModel { Id: "graph-set" });
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(add, []);
        var refreshed = Descendants(graph).OfType<InlineBlockEditor>().Single(b => b.DataContext is NodeViewModel { Id: "graph-set" });
        Assert.True(refreshed.ContextMenu!.IsOpen); refreshed.ContextMenu.IsOpen = false;

        // The compact condition opens the existing modal editor with the actual decision-step scope.
        var owner = editor.AllNodes().First(n => n.Children.FirstOrDefault()?.IsTemplateDecision == true);
        var decisionId = owner.Children[0].Id;
        var condition = Descendants(graph).OfType<Button>().Single(b => b.DataContext is ParameterRowViewModel r && r.NodeId == decisionId && r.Descriptor.Name == "Ausdruck");
        Exception? error = null;
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            var window = Application.Current.Windows.OfType<ExpressionBuilderWindow>().Single();
            try
            {
                var model = Assert.IsType<ExpressionBuilderViewModel>(window.DataContext);
                model.Root.Value = "false";
                window.UpdateLayout();
                CaptureVisual(window, "graph-condition-dialog.png");
                Click(window, "Übernehmen");
            }
            catch (Exception ex) { error = ex; window.Close(); }
        }));
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(condition, []);
        Assert.Null(error);
        Assert.Equal("false", editor.FindNode(decisionId)!.Step!.Parameters["Ausdruck"]);
        editor.UndoCommand.Execute(null); Sta.Flush(dispatcher); graph.RefreshScene();
        Assert.Equal("true", editor.FindNode(decisionId)!.Step!.Parameters["Ausdruck"]);
        CaptureVisual(Window.GetWindow(view), "graph-compact-overview.png");
    }

    private static void VerifyGraphDetails(EditorView view, FlowGraphView graph, EditorViewModel editor, Dispatcher dispatcher)
    {
        editor.SelectedNode = editor.FindNode("graph-set");
        var camera = (graph.Camera.X, graph.Camera.Y, graph.Camera.Zoom);
        var button = Descendants(graph).OfType<Button>().Single(b => b.ToolTip?.ToString() == "Details in normaler Ansicht öffnen"
            && b.DataContext is NodeViewModel { Id: "graph-set" });
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, []);
        Sta.Flush(dispatcher);
        Assert.False(editor.GraphView); Assert.False(editor.VisualBlocks);
        Assert.Equal("graph-set", editor.SelectedNode!.Id);
        Assert.Same(editor.SelectedNode.InlineProperties, editor.Properties);
        Assert.True(((FrameworkElement)view.FindName("PropertiesPanel")).IsVisible);
        editor.GraphView = true; Sta.Flush(dispatcher); graph.RefreshScene();
        Assert.Equal(camera, (graph.Camera.X, graph.Camera.Y, graph.Camera.Zoom));
        var normal = Descendants(graph).OfType<Button>().Single(b => b.Content?.ToString() == "Normale Ansicht");
        normal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Sta.Flush(dispatcher);
        Assert.False(editor.GraphView); Assert.Equal("graph-set", editor.SelectedNode!.Id);
        editor.GraphView = true; Sta.Flush(dispatcher); graph.RefreshScene();

    }
}
