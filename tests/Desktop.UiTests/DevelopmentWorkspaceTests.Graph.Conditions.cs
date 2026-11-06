using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Testumgebung.Contracts;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyConditionalGroups(Dispatcher dispatcher)
    {
        StepDefinition Measure(string id, string low, string high) => BlockCatalog.Default.Create(BlockCatalog.Measure, id, "Spannung prüfen", new Dictionary<string,string>
            { ["Ressource"] = "DMM", ["Untergrenze"] = low, ["Obergrenze"] = high, ["Einheit"] = "V" });
        var loop = new GroupDefinition("article-loop", "Artikelabhängige Prüfung");
        loop.Add(new GroupDefinition("article-one", "Artikel 1: 4–6 V") { EntryCondition = "Artikel == \"1\"" }.Add(Measure("measure-one", "4", "6")),
            new GroupDefinition("article-two", "Artikel 2: 8–12 V") { EntryCondition = "Artikel == \"2\"" }.Add(Measure("measure-two", "8", "12")));
        var nested = new GroupDefinition("skip-outer", "Optionaler Abschnitt") { EntryCondition = "false" };
        nested.Add(new GroupDefinition("skip-middle", "Optionale Untergruppe") { EntryCondition = "false" }.Add(
            new GroupDefinition("skip-inner", "Optionale Detailprüfung") { EntryCondition = "false" }.Add(Measure("skip-measure", "0", "1"))));
        // An ordinary step ends the horizontal row; one conditional group remains vertical below it.
        loop.Add(BlockCatalog.Default.Create(BlockCatalog.Wait, "after-articles", "Danach fortsetzen"), nested);
        var definition = new TestDefinition("Artikelvarianten und Überspringpfade", "1", new("start", "Start"), loop, new("end", "Ende"));
        definition.Root.Variables.Add(new("Artikel", VarType.String, "1"));
        var project = new TestProject(definition); project.Simulation.Add(new("DMM", "dmm", "V", 5));
        var shell = new ShellViewModel(project: project); shell.Editor.GraphView = true;
        var view = new EditorView { DataContext = shell, Background = Brushes.White };
        var window = new Window { Content = view, Width = 1586, Height = 1080, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        window.Show();
        try
        {
            Sta.Flush(dispatcher); view.UpdateLayout();
            var graph = Descendants(view).OfType<FlowGraphView>().Single(); graph.RefreshScene(); Sta.Flush(dispatcher);
            GraphViewOption(graph, "Alles anzeigen");
            var scene = graph.Scene!;
            var first = scene.Cards.Single(card => card.Id == "node:article-one");
            var second = scene.Cards.Single(card => card.Id == "node:article-two");
            Assert.Equal(first.Y, second.Y); Assert.True(second.X > first.X);
            var wires = (Canvas)graph.FindName("Wires");
            var paths = wires.Children.OfType<System.Windows.Shapes.Path>().ToArray();
            var rails = new List<double>();
            for (var index = 0; index < scene.Links.Count; index++)
            {
                var link = scene.Links[index];
                if (link.Kind != GraphLinkKind.Else || !link.From.StartsWith("node:skip-", StringComparison.Ordinal)) continue;
                var segment = PathGeometry.CreateFromGeometry(paths[index].Data).Figures[0].Segments[0];
                rails.Add(segment switch { PolyLineSegment polyline => polyline.Points[0].X, LineSegment line => line.Point.X, _ => throw new InvalidOperationException("Unexpected wire geometry.") });
            }
            Assert.Equal(3, rails.Count); Assert.Equal(3, rails.Distinct().Count());
            CaptureVisual(window, "graph-article-conditions.png");
        }
        finally { window.Close(); Sta.Flush(dispatcher); }
    }
}
