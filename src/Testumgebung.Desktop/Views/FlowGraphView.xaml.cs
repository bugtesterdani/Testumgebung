using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Zoombare Arbeitsfläche. Kartenpositionen sind Ansichtszustand; Verbindungen werden als normale Ablaufänderung gespeichert.</summary>
public partial class FlowGraphView : UserControl
{
    private ShellViewModel? Shell => DataContext as ShellViewModel;
    private EditorViewModel? Editor => Shell?.Editor;
    internal GraphViewport Camera { get; } = new();
    internal FlowGraph? Scene { get; private set; }
    private readonly Dictionary<string, Border> _cards = [];
    private readonly Dictionary<string, StackPanel> _ports = [];
    private readonly Dictionary<string, InlineBlockEditor> _fields = [];
    private readonly Dictionary<string, TextBlock> _junctionLabels = [];
    private readonly Dictionary<string, Point> _positions = [];
    private readonly Dictionary<string, double> _heights = [];
    private readonly List<INotifyPropertyChanged> _observed = [];
    private NodeViewModel[] _nodes = [];
    private EditorViewModel? _boundEditor;
    private object? _project;
    private bool _initialView;
    private bool _scheduled, _reflowing;
    private Point? _pan;
    private GraphSocket? _connecting;
    private Point _wireStart;
    private string? _lastPause;
    private Point _nodeDragPoint;
    public FlowGraphView()
    {
        InitializeComponent();
        Viewport.SizeChanged += (_, _) => { if (_initialView) Schedule(); };
        Loaded += (_, _) => { if (IsVisible) Attach(); }; Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) Attach(); else Detach(); };
        DataContextChanged += (_, _) => { if (IsLoaded && IsVisible) Attach(); };
    }
    private void Attach()
    {
        Detach();
        if (Shell is not { } shell) return;
        Observe(shell); Observe(shell.Editor);
        foreach (var station in shell.Stations) { Observe(station); Observe(station.Debug); }
        _boundEditor = shell.Editor; _boundEditor.Nodes.CollectionChanged += OnNodesChanged; _boundEditor.Changed += OnEditorChanged;
        Schedule();
    }
    private void Observe(INotifyPropertyChanged source) { _observed.Add(source); source.PropertyChanged += OnStateChanged; }
    private void Detach()
    {
        foreach (var source in _observed) source.PropertyChanged -= OnStateChanged;
        _observed.Clear();
        if (_boundEditor is not null) { _boundEditor.Nodes.CollectionChanged -= OnNodesChanged; _boundEditor.Changed -= OnEditorChanged; }
        _boundEditor = null;
    }
    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Schedule();
    private void OnEditorChanged() => Schedule();
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        var relevant = sender switch
        {
            ShellViewModel => e.PropertyName == nameof(ShellViewModel.SelectedStation),
            EditorViewModel => e.PropertyName == nameof(EditorViewModel.GraphWorkspaceId),
            StationViewModel => e.PropertyName == nameof(StationViewModel.IsRunning),
            DebugViewModel => e.PropertyName is nameof(DebugViewModel.Pause) or nameof(DebugViewModel.IsCodePaused) or nameof(DebugViewModel.CodeLine),
            _ => false
        };
        if (sender is ShellViewModel && relevant) _lastPause = null;
        if (relevant) Schedule();
    }
    private void Schedule()
    {
        if (_scheduled || !IsLoaded || !IsVisible) return;
        _scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _scheduled = false; if (IsLoaded && IsVisible) RefreshScene(); }));
    }
    internal void RefreshScene()
    {
        if (Editor is not { } editor) return;
        var projectKey = editor.GraphWorkspaceId;
        if (!Equals(_project, projectKey))
        { _project = projectKey; _positions.Clear(); _heights.Clear(); Camera.Reset(); _lastPause = null; _initialView = true; }
        var nodes = editor.AllNodes().ToArray();
        if (!_nodes.SequenceEqual(nodes))
        { _nodes = nodes; foreach (var card in _cards.Values) World.Children.Remove(card); _cards.Clear(); _ports.Clear(); _fields.Clear(); _junctionLabels.Clear(); _highlighted = null; _lastPause = null; }
        Reflow();
        if (_initialView && Scene?.Cards.FirstOrDefault() is { } first && Viewport.ActualWidth > 0)
        {
            Camera.Reset(); Camera.Pan(Viewport.ActualWidth / 2 - (first.X + 40 + first.Width / 2) - Camera.X, 0);
            _initialView = false; ApplyCamera();
        }
        UpdateDebug();
    }
    private bool IsRunning => Shell?.Stations.Any(s => s.IsRunning) == true;
    private void Reflow()
    {
        if (_reflowing || Editor is null) return;
        _reflowing = true;
        try
        {
            Scene = new FlowGraphLayout(_heights).Build(Editor.Nodes);
            foreach (var item in Scene.Cards)
            {
                if (!_cards.TryGetValue(item.Id, out var border))
                { border = MakeCard(item); _cards.Add(item.Id, border); World.Children.Add(border); }
                if (_junctionLabels.TryGetValue(item.Id, out var label)) label.Text = item.Label;
                var position = _positions.GetValueOrDefault(item.Id, new Point(item.X + 40, item.Y + 40));
                Canvas.SetLeft(border, position.X); Canvas.SetTop(border, position.Y);
                border.IsEnabled = true;
                border.Background = (Brush)new BrushConverter().ConvertFromString(item.Owner.Group?.Parallel == true ? "#EBDFFF" : item.Owner.BlockColor)!;
                if (_fields.TryGetValue(item.Id, out var fields)) fields.IsEnabled = !IsRunning;
                RenderPorts(item);
            }
            World.Width = Scene.Width + 100; World.Height = Scene.Height + 100;
            DrawLinks(); ApplyCamera();
        }
        finally { _reflowing = false; }
    }
    private Border MakeCard(GraphCard item)
    {
        var panel = new StackPanel();
        panel.Children.Add(MakeHeading(item));
        if (item.Owner.Note is { } note) panel.Children.Add(new TextBlock { Text = "Definition · " + note, FontSize = 11, Margin = new Thickness(10, 4, 10, 4), TextWrapping = TextWrapping.Wrap });
        if (item.Junction)
        {
            var label = new TextBlock { Text = item.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap };
            _junctionLabels[item.Id] = label; panel.Children.Add(label);
        }
        else
        {
            var fields = new InlineBlockEditor { DataContext = item.Owner, Margin = new Thickness(10, 4, 10, 0) }; _fields[item.Id] = fields; panel.Children.Add(fields);
            var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 0) };
            error.SetBinding(TextBlock.TextProperty, new Binding("Diagnostic") { Source = item.Owner }); panel.Children.Add(error);
        }
        var ports = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, -8) };
        panel.Children.Add(ports); _ports[item.Id] = ports;
        var border = new Border { Width = item.Width, Background = (Brush)new BrushConverter().ConvertFromString(item.Owner.Group?.Parallel == true ? "#EBDFFF" : item.Owner.BlockColor)!, BorderBrush = Brushes.SlateGray,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), Child = panel, Tag = item, DataContext = item.Owner };
        var appearance = new Style(typeof(Border));
        var inactive = new DataTrigger { Binding = new Binding("IsInactive"), Value = true };
        inactive.Setters.Add(new Setter(OpacityProperty, .5)); appearance.Triggers.Add(inactive); border.Style = appearance;
        border.PreviewMouseLeftButtonDown += (_, _) => { if (Editor is { } editor) editor.SelectedNode = item.Owner; };
        border.SetBinding(Border.BorderBrushProperty, new Binding("RunMarker") { Converter = new GraphMarkerBrush() });
        border.SizeChanged += (_, _) =>
        {
            var key = item.Junction ? item.Id : item.Owner.Id;
            if (Math.Abs(_heights.GetValueOrDefault(key, item.Height) - border.ActualHeight) < 1) return;
            _heights[key] = border.ActualHeight; Schedule();
        };
        if (!item.Junction)
        {
            border.Child = null;
            var input = SocketCircle(item.Owner, "Eingang: einen Ausgang hierher ziehen");
            var host = new Grid(); host.Children.Add(panel); input.HorizontalAlignment = HorizontalAlignment.Center; input.VerticalAlignment = VerticalAlignment.Top; input.Margin = new Thickness(0, -8, 0, 0); host.Children.Add(input);
            border.Child = host;
        }
        border.Measure(new Size(item.Width, double.PositiveInfinity));
        _heights[item.Junction ? item.Id : item.Owner.Id] = border.DesiredSize.Height;
        return border;
    }
    private UIElement MakeHeading(GraphCard item)
    {
        var grip = new Thumb { Height = 25, Cursor = Cursors.SizeAll, ToolTip = "Knoten verschieben (ändert keine Reihenfolge)", DataContext = item.Owner };
        grip.Template = GripTemplate();
        grip.DragStarted += (_, _) => _nodeDragPoint = Mouse.GetPosition(Viewport);
        grip.DragDelta += (_, _) =>
        {
            var point = Mouse.GetPosition(Viewport);
            MoveCard(item.Id, (point.X - _nodeDragPoint.X) / Camera.Zoom, (point.Y - _nodeDragPoint.Y) / Camera.Zoom);
            _nodeDragPoint = point;
        };
        grip.PreviewMouseLeftButtonDown += (_, _) => { if (Editor is { } editor) editor.SelectedNode = item.Owner; };
        var heading = new DockPanel();
        var breakpoint = new Button { Command = Editor!.ToggleBreakpointCommand, CommandParameter = item.Owner, DataContext = item.Owner,
            ToolTip = "Nach diesem Block halten, vor Ergebnisweitergabe (F9)", Padding = new Thickness(7, 1, 7, 1), Height = 25 };
        var style = new Style(typeof(Button), (Style)FindResource("ToolButton")); style.Setters.Add(new Setter(ContentControl.ContentProperty, "○"));
        var active = new DataTrigger { Binding = new Binding("HasBreakpoint"), Value = true };
        active.Setters.Add(new Setter(ContentControl.ContentProperty, "●")); active.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Firebrick)); style.Triggers.Add(active);
        breakpoint.Style = style;
        DockPanel.SetDock(breakpoint, Dock.Right); heading.Children.Add(breakpoint); heading.Children.Add(grip); return heading;
    }
    private ControlTemplate GripTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border)); root.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(18, 30, 60, 100)));
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetValue(TextBlock.TextProperty, "⋮⋮   Ziehen zum Verschieben"); label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center); label.SetValue(TextBlock.FontSizeProperty, 11d); root.AppendChild(label);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = root };
    }
    internal void MoveCard(string id, double dx, double dy)
    {
        if (!_cards.TryGetValue(id, out var card)) return;
        var next = new Point(Canvas.GetLeft(card) + dx, Canvas.GetTop(card) + dy); _positions[id] = next;
        Canvas.SetLeft(card, next.X); Canvas.SetTop(card, next.Y); DrawLinks();
    }
    private void RenderPorts(GraphCard item)
    {
        var panel = _ports[item.Id];
        var kinds = SocketKinds(item).ToArray();
        var existing = panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Ellipse>()).Select(e => ((GraphSocket)e.Tag).Kind);
        if (existing.SequenceEqual(kinds)) return;
        panel.Children.Clear();
        foreach (var kind in kinds)
        {
            var socket = new GraphSocket(item.Owner, kind);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
            row.Children.Add(new TextBlock { Text = SocketLabel(kind), FontSize = 11, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var circle = SocketCircle(socket, "Ausgang zum Eingang eines Blocks ziehen");
            circle.MouseLeftButtonDown += OnOutputDown; row.Children.Add(circle); panel.Children.Add(row);
        }
    }
    private static IEnumerable<GraphSocketKind> SocketKinds(GraphCard item)
    {
        if (item.Junction) return CompletionSockets(item.Owner);
        if (!item.Owner.IsGroup) return item.Owner.Parent?.Group?.Parallel == true ? [] : [GraphSocketKind.Next];
        if (item.Owner.Children.FirstOrDefault()?.IsTemplateDecision == true) return [GraphSocketKind.Then, GraphSocketKind.Else];
        return [item.Owner.Group!.Parallel ? GraphSocketKind.Parallel : GraphSocketKind.Inside];
    }
    private static IEnumerable<GraphSocketKind> CompletionSockets(NodeViewModel owner)
    {
        if (owner.IsExit || owner.Parent?.Children.FirstOrDefault()?.IsTemplateDecision == true || owner.Parent?.Group?.Parallel == true) return [];
        return [GraphSocketKind.Next];
    }
    private static string SocketLabel(GraphSocketKind kind) => kind switch
    { GraphSocketKind.Inside => "In Gruppe", GraphSocketKind.Then => "Dann", GraphSocketKind.Else => "Sonst", GraphSocketKind.Parallel => "Parallelzweig", _ => "Weiter" };
    private static Ellipse SocketCircle(object tag, string hint) => new() { Width = 16, Height = 16, Fill = Brushes.White, Stroke = Brushes.RoyalBlue, StrokeThickness = 2, Tag = tag, ToolTip = hint, Cursor = Cursors.Cross };
    private void ApplyCamera()
    { World.RenderTransform = new MatrixTransform(Camera.Zoom, 0, 0, Camera.Zoom, Camera.X, Camera.Y); ZoomText.Text = $"{Camera.Zoom * 100:0} %"; }
    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(Camera.Zoom * 1.2, new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(Camera.Zoom / 1.2, new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void OnReset(object sender, RoutedEventArgs e) => Zoom(1, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));
    private void OnFit(object sender, RoutedEventArgs e)
    {
        if (Scene is null) return;
        World.UpdateLayout();
        var bounds = VisualTreeHelper.GetDescendantBounds(World);
        if (bounds.IsEmpty) return;
        Camera.Fit(bounds.Width, bounds.Height, Viewport.ActualWidth, Viewport.ActualHeight);
        Camera.Pan(-bounds.X * Camera.Zoom, -bounds.Y * Camera.Zoom); ApplyCamera();
    }
    private void OnArrange(object sender, RoutedEventArgs e) { _positions.Clear(); Reflow(); }
    internal void Zoom(double zoom, Point anchor) { Camera.ZoomAt(zoom, anchor.X, anchor.Y); ApplyCamera(); }
    private void OnWheel(object sender, MouseWheelEventArgs e)
    { if (IsInput(e.OriginalSource as DependencyObject) && Keyboard.Modifiers != ModifierKeys.Control) return; Zoom(Camera.Zoom * Math.Pow(1.15, e.Delta / 120d), e.GetPosition(Viewport)); e.Handled = true; }
    private static bool IsInput(DependencyObject? element)
    {
        for (var p = element; p is not null && p is not FlowGraphView; p = VisualTreeHelper.GetParent(p))
            if (p is TextBoxBase or ComboBox or ScrollBar) return true;
        return false;
    }
    private void OnGraphKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F9 || IsInput(e.OriginalSource as DependencyObject) || Editor?.SelectedNode is not { } node) return;
        Editor.ToggleBreakpointCommand.Execute(node); e.Handled = true;
    }
    private void OnProjectExpanded(object sender, RoutedEventArgs e)
    { if (ReferenceEquals(e.OriginalSource, sender) && Editor is { } editor) ProjectOptions.Content = new ProjectPropertiesViewModel(editor); }
}

internal sealed class GraphMarkerBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value switch
    { RunMarker.Paused => Brushes.Firebrick, RunMarker.Running => Brushes.DodgerBlue, RunMarker.Done => Brushes.SeaGreen, _ => Brushes.SlateGray };
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Binding.DoNothing;
}
