using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Testumgebung.Presentation.ViewModels;
using Path = System.Windows.Shapes.Path;

namespace Testumgebung.Desktop.Views;

public partial class FlowGraphView
{
    private void DrawLinks()
    {
        Wires.Children.Clear();
        if (Scene is null) return;
        var byId = Scene.Cards.ToDictionary(c => c.Id);
        var rails = new GraphRailLayout();
        foreach (var link in Scene.Links)
        {
            if (!byId.TryGetValue(link.From, out var from) || !byId.TryGetValue(link.To, out var to)) continue;
            var start = OutputPoint(from, link.Kind);
            var end = new Point(Canvas.GetLeft(_cards[to.Id]) + to.Width / 2, Canvas.GetTop(_cards[to.Id]) - (to.Junction ? 0 : 6));
            var repeat = link.Kind == GraphLinkKind.Repeat;
            var bypass = link.Kind == GraphLinkKind.Else && ReferenceEquals(from.Owner, to.Owner) && to.Junction;
            double? rail = repeat || bypass ? rails.Allocate(OutsideGroup(from.Owner), start.Y, end.Y) : null;
            if (link.Label == "Nächste Bedingung") rail = BeforeGroup(to.Owner);
            var label = link.Kind is GraphLinkKind.Then or GraphLinkKind.Parallel || link.Kind == GraphLinkKind.Else && !bypass ? "" : link.Label;
            DrawWire(start, end, LinkBrush(link.Kind), label, repeat, bypass, rail, to.Junction ? end.Y - 18 : null);
        }
    }
    private double BeforeGroup(NodeViewModel owner)
    {
        var descendants = owner.SelfAndDescendants().ToHashSet();
        return Scene!.Cards.Where(card => descendants.Contains(card.Owner)).Min(card => Canvas.GetLeft(_cards[card.Id])) - 28;
    }
    private double OutsideGroup(NodeViewModel owner)
    {
        var descendants = owner.SelfAndDescendants().ToHashSet();
        return Scene!.Cards.Where(c => descendants.Contains(c.Owner)).Max(c => Canvas.GetLeft(_cards[c.Id]) + c.Width) + 70;
    }
    private Point OutputPoint(GraphCard from, GraphLinkKind kind)
    {
        var expected = kind switch { GraphLinkKind.Then => GraphSocketKind.Then, GraphLinkKind.Else => GraphSocketKind.Else, GraphLinkKind.Parallel => GraphSocketKind.Parallel, _ => (GraphSocketKind?)null };
        var circles = _ports[from.Id].Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Ellipse>()).ToList();
        var circle = circles.FirstOrDefault(c => c.Tag is GraphSocket socket && socket.Kind == expected) ?? circles.FirstOrDefault();
        if (circle is not null && circle.IsArrangeValid)
        {
            var local = circle.TranslatePoint(new Point(8, 16), _cards[from.Id]);
            return new Point(Canvas.GetLeft(_cards[from.Id]) + local.X, Canvas.GetTop(_cards[from.Id]) + local.Y);
        }
        var card = _cards[from.Id];
        return new Point(Canvas.GetLeft(card) + from.Width / 2, Canvas.GetTop(card) + Math.Max(card.ActualHeight, from.Height));
    }
    private void DrawWire(Point start, Point end, Brush color, string label, bool repeat = false, bool bypass = false, double? rail = null, double? bendY = null)
    {
        // Join at a shared rail below the longest branch, never through a neighbouring branch's cards.
        var mid = bendY ?? (start.Y + end.Y) / 2;
        var outside = rail ?? Math.Max(start.X, end.X) + 360;
        var nextCondition = label == "Nächste Bedingung";
        var points = nextCondition
            ? new[] { start, new Point(start.X, start.Y + 18), new Point(outside, start.Y + 18), new Point(outside, end.Y - 24), new Point(end.X, end.Y - 24), end }
            : repeat || bypass
            ? new[] { start, new Point(outside, start.Y), new Point(outside, end.Y - 30), new Point(end.X, end.Y - 30), end }
            : new[] { start, new Point(start.X, mid), new Point(end.X, mid), end };
        var geometry = new StreamGeometry();
        using (var context = geometry.Open()) { context.BeginFigure(points[0], false, false); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        Wires.Children.Add(new Path { Data = geometry, Stroke = color, StrokeThickness = 3, StrokeDashArray = repeat ? new DoubleCollection([5, 3]) : null });
        var direction = end - points[^2];
        if (direction.Length > 0) direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        Wires.Children.Add(new Polygon { Fill = color, Points = new PointCollection([end, end - direction * 12 + normal * 5, end - direction * 12 - normal * 5]) });
        if (label.Length == 0) return;
        var text = new TextBlock { Text = label, Foreground = color, Background = Brushes.White, FontSize = 12, MaxWidth = 260, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(4, 2, 4, 2) };
        Canvas.SetLeft(text, repeat || bypass ? outside + 8 : (start.X + end.X) / 2 + 8); Canvas.SetTop(text, mid - 22); Wires.Children.Add(text);
    }
    private static Brush LinkBrush(GraphLinkKind kind) => kind switch
    { GraphLinkKind.Then => Brushes.SeaGreen, GraphLinkKind.Else => Brushes.DarkOrange, GraphLinkKind.Parallel => Brushes.BlueViolet, GraphLinkKind.Repeat => Brushes.Teal, GraphLinkKind.Exit => Brushes.Sienna, _ => Brushes.RoyalBlue };
    private void OnOutputDown(object sender, MouseButtonEventArgs e)
    {
        if (IsRunning || sender is not Ellipse { Tag: GraphSocket socket } circle) return;
        _connecting = socket; _wireStart = circle.TranslatePoint(new Point(8, 8), World);
        Mouse.Capture(Viewport, CaptureMode.SubTree); e.Handled = true;
        Status.Text = "Zum Eingang eines Blocks ziehen. Bei Dann/Sonst wird der Zielblock in den passenden Zweig eingefügt.";
    }
    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        var empty = ReferenceEquals(e.OriginalSource, Viewport) || ReferenceEquals(e.OriginalSource, World) || ReferenceEquals(e.OriginalSource, Wires);
        if (e.ChangedButton != MouseButton.Middle && !(e.ChangedButton == MouseButton.Left && empty)) return;
        _pan = e.GetPosition(Viewport); Mouse.Capture(Viewport, CaptureMode.SubTree); Viewport.Cursor = Cursors.ScrollAll; e.Handled = true;
    }
    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Viewport);
        if (_pan is { } previous)
        { Camera.Pan(point.X - previous.X, point.Y - previous.Y); _pan = point; ApplyCamera(); e.Handled = true; }
        if (_connecting is null) return;
        DrawLinks(); DrawWire(_wireStart, e.GetPosition(World), Brushes.DeepPink, ""); e.Handled = true;
    }
    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_connecting is not null) { FinishConnection(HitInput(e.GetPosition(World))); e.Handled = true; }
        _pan = null; Viewport.Cursor = Cursors.Arrow; Viewport.ReleaseMouseCapture();
    }
    internal bool FinishConnection(NodeViewModel? target)
    {
        var connected = _connecting is { } source && !IsRunning && target is not null && Editor?.ConnectGraph(source, target) == true;
        Status.Text = connected ? "Verbindung übernommen · Reihenfolge geändert. Rückgängig ist möglich." : "Verbindung nicht möglich: Eingang wählen; Zyklen, feste Abschnitte und parallele Geschwister sind geschützt.";
        _connecting = null; DrawLinks(); return connected;
    }
    private NodeViewModel? HitInput(Point point)
    {
        for (var element = World.InputHitTest(point) as DependencyObject; element is not null && element != World; element = VisualTreeHelper.GetParent(element))
            if (element is Ellipse { Tag: NodeViewModel node }) return node;
        return null;
    }
    private void OnCaptureLost(object sender, MouseEventArgs e)
    { _pan = null; _connecting = null; Viewport.Cursor = Cursors.Arrow; DrawLinks(); }
    private Border? _highlighted;
    private void UpdateDebug()
    {
        if (Shell?.SelectedStation.Debug is not { } debug) return;
        var id = debug.IsCodePaused ? debug.Controller.CurrentCodePause?.NodeId : debug.Pause?.NodeId;
        RestoreHighlight(id);
        if (id is null)
        {
            if (_lastPause is not null) Status.Text = "Mausrad: Zoom · freie Fläche / mittlere Maustaste: schieben · Ausgang zum Eingang ziehen: verbinden";
            _lastPause = null; return;
        }
        if (id == _lastPause || Scene is null) return;
        _lastPause = id;
        Status.Text = "Haltepunkt: " + (debug.IsCodePaused ? debug.CodeNodeName : debug.NodeName) + " · Werte des aktuellen Haltekontexts";
        var card = PausedCard(id, debug.IsGroupPause);
        if (card is null) { Status.Text = "Der pausierte Block gehört zu einem anderen Programmstand. Passendes Projekt öffnen."; return; }
        FocusCard(card);
    }
    private void RestoreHighlight(string? id)
    {
        if (id == _lastPause || _highlighted is not { } old) return;
        old.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("RunMarker") { Converter = new GraphMarkerBrush() });
        old.BorderThickness = new Thickness(2); _highlighted = null;
    }
    private GraphCard? PausedCard(string id, bool group)
    {
        var direct = Scene!.Cards.FirstOrDefault(c => c.Owner.Id == id && c.Junction == group)
            ?? Scene.Cards.FirstOrDefault(c => c.Owner.Id == id)
            ?? Scene.Cards.FirstOrDefault(c => c.Owner.Children.Any(n => n.Id == id && n.IsTemplateDecision));
        if (direct is not null) return direct;
        var branch = Editor?.FindNode(id)?.SelfAndDescendants().ToHashSet();
        return Scene.Cards.LastOrDefault(c => branch?.Contains(c.Owner) == true);
    }
    private void FocusCard(GraphCard card)
    {
        Camera.ZoomAt(Math.Max(1, Camera.Zoom), Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);
        var border = _cards[card.Id]; _highlighted = border; border.BorderBrush = Brushes.Firebrick; border.BorderThickness = new Thickness(4);
        Camera.Pan(Viewport.ActualWidth / 2 - ((Canvas.GetLeft(border) + card.Width / 2) * Camera.Zoom + Camera.X),
            Viewport.ActualHeight / 2 - ((Canvas.GetTop(border) + border.ActualHeight / 2) * Camera.Zoom + Camera.Y));
        ApplyCamera();
    }
}
