using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabGraphicWindow : Window
{
    private readonly LabelLayout _layout;
    private readonly Action _refresh;
    private CabSourceEditor _editor;
    private readonly CabSourceHistory _history;
    private CabGraphicScene? _scene;
    private readonly ComboBox _jobs = new() { MinWidth = 130 };
    private readonly ComboBox _catalog = new() { MinWidth = 280, MaxWidth = 440, DisplayMemberPath = "Title" };
    private readonly ListBox _items = new() { Height = 170 };
    private readonly Canvas _canvas = new() { Background = Brushes.LightGray, ClipToBounds = true };
    private readonly Polygon _selection = new() { Stroke = Brushes.DodgerBlue, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Dictionary<string, TextBox> _inputs = [];
    private readonly TextBox _examples = new() { AcceptsReturn = true, Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _updating;
    private CabGraphicItem? _dragItem;
    private FrameworkElement? _dragVisual;
    private Point _dragStart;
    private Matrix _dragMatrix;

    internal static void Attach(Panel panel, LabelDesignerWindow owner, LabelLayout layout, bool cab)
    {
        if (!cab) return;
        var button = new Button { Content = "Vollständige cab-Grafik …", Margin = new(12, 0, 0, 0) };
        button.Click += (_, _) =>
        {
            try { new CabGraphicWindow(layout, owner.RefreshCabSource) { Owner = owner }.ShowDialog(); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { owner.ReportCabSourceError(error.Message); }
        };
        panel.Children.Add(button);
    }

    internal CabGraphicWindow(LabelLayout layout, Action? refresh = null)
    {
        _layout = layout; _refresh = refresh ?? (() => { });
        _editor = Read(); _history = new(_editor);
        var before = layout.CabSource is { } source ? new CabSourceEditor(source.Data, source.SetupCodePage, layout.CabSettings) : _editor;
        CabSourceReconcile.Apply(layout, before, _editor); _refresh();
        Title = "Vollständige cab-Grafik"; Width = 1220; Height = 820; MinWidth = 900; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = CreateContent();
        _catalog.ItemsSource = CabGraphicCatalog.Definitions; _catalog.SelectedIndex = 0;
        CabCatalogControls.ExplainGeneration(_catalog, _editor.Settings.DefaultGeneration);
        _jobs.SelectionChanged += (_, _) => { if (!_updating) Draw(); };
        _items.SelectionChanged += (_, _) => Select();
        _examples.Text = layout.CabGraphicExampleValues ?? "Kennung=SN-123";
        _canvas.MouseMove += DragMove; _canvas.MouseLeftButtonUp += DragEnd;
        Reload(0);
    }

    internal CabSourceEditor Editor => _editor;
    internal CabGraphicScene? Scene => _scene;
    internal CabGraphicItem? Selected => _items.SelectedItem as CabGraphicItem;
    private int JobIndex => (_jobs.SelectedItem as CabGraphicJob)?.Index ?? 0;
    private CabSourceEditor Read() => new(CabLabelLayout.Render(_layout), _layout.CabSource?.SetupCodePage ?? "UTF-8", _layout.CabSettings);

    private UIElement CreateContent()
    {
        var root = new DockPanel { Margin = new(12) };
        var bottom = new StackPanel(); bottom.Children.Add(_status);
        var close = new Button { Content = "Zurück zur Vorlage", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        bottom.Children.Add(close); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var tools = new WrapPanel(); tools.Children.Add(new TextBlock { Text = "Auftrag", VerticalAlignment = VerticalAlignment.Center }); tools.Children.Add(_jobs); tools.Children.Add(_catalog);
        Button(tools, "Einfügen", Add); Button(tools, "Löschen", () => { if (Selected is { } item) Apply(CabGraphicEditor.Remove(_editor, item)); });
        Button(tools, "90° drehen", () => { if (Selected is { } item) Apply(CabGraphicEditor.Rotate(_editor, item, CabGraphicValues.Number(item.Command, "r") + 90)); });
        Button(tools, "Eigenschaften …", Properties); Button(tools, "Etikett …", LabelProperties);
        Button(tools, "Rückgängig", () => Apply(_history.Undo(), false)); Button(tools, "Wiederholen", () => Apply(_history.Redo(), false));
        DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        var side = new StackPanel { Width = 330, Margin = new(12, 0, 0, 0) }; side.Children.Add(_items);
        foreach (var name in new[] { "X", "Y", "Drehung", "Breite", "Höhe" })
        {
            side.Children.Add(new TextBlock { Text = name }); var box = new TextBox();
            System.Windows.Automation.AutomationProperties.SetName(box, "cab-Grafik " + name); _inputs[name] = box; side.Children.Add(box);
        }
        Button(side, "Position und Drehung übernehmen", ApplyPosition); Button(side, "Größe übernehmen", ApplySize);
        side.Children.Add(new TextBlock { Text = "Beispielwerte für Platzhalter", Margin = new(0, 12, 0, 0) }); side.Children.Add(_examples);
        Button(side, "Beispiele anzeigen", () => { _layout.CabGraphicExampleValues = _examples.Text; Draw(); });
        side.Children.Add(new TextBlock { Text = "Maße in der Einheit des gewählten Befehls; Schriften sind Näherungen. Barcode-Ersatzflächen sind beschriftet.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        side.Children.Add(_notes);
        var sidebar = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 350 };
        DockPanel.SetDock(sidebar, Dock.Right); root.Children.Add(sidebar);
        var view = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = _canvas };
        root.Children.Add(new Border { Background = Brushes.DarkGray, Padding = new(16), Child = view });
        return root;
    }

    private void Button(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new(3), Padding = new(6, 3, 6, 3) };
        button.Click += (_, _) => Run(action); panel.Children.Add(button);
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or FormatException or NotSupportedException)
        { _status.Text = error.Message; }
    }

    internal void Apply(CabSourceEditor next, bool record = true)
    {
        if (record && _editor.Document.Write().AsSpan().SequenceEqual(next.Document.Write())) return;
        CabSourceReconcile.Apply(_layout, _editor, next);
        if (record) _history.Record(next);
        _editor = next; _refresh(); Reload(JobIndex);
    }

    private void Reload(int job)
    {
        _updating = true; _jobs.ItemsSource = CabGraphicCatalog.Jobs(_editor);
        _jobs.SelectedItem = _jobs.Items.OfType<CabGraphicJob>().FirstOrDefault(j => j.Index == job) ?? _jobs.Items.OfType<CabGraphicJob>().FirstOrDefault();
        _updating = false; Draw();
    }

    private void Draw()
    {
        var selected = Selected?.Command.Syntax.Range.Offset;
        _canvas.Children.Clear(); _scene = null;
        if (_jobs.SelectedItem is not CabGraphicJob) { _items.ItemsSource = null; _status.Text = "Kein expliziter Auftrag vorhanden. Im Auftragseditor J und A ergänzen."; return; }
        Run(() =>
        {
            _scene = CabGraphicFactory.Build(_editor, _layout, JobIndex);
            _canvas.Width = Math.Max(1, _scene.Width); _canvas.Height = Math.Max(1, _scene.Height + _scene.Gap);
            Material(_scene);
            foreach (var item in _scene.Items) AddVisual(item);
            _canvas.Children.Add(_selection);
            _items.ItemsSource = _scene.Items.Where(i => i.Column == 0).ToArray();
            _items.SelectedItem = _items.Items.OfType<CabGraphicItem>().FirstOrDefault(i => i.Command.Syntax.Range.Offset == selected) ?? _items.Items.OfType<CabGraphicItem>().FirstOrDefault();
            _status.Text = "Auftrag " + (JobIndex + 1) + " · Sensor " + _scene.Sensor + " · " + _scene.Columns + " Spalte(n)\n" + string.Join("\n", _scene.Notes);
            Select();
        });
    }

    private void Material(CabGraphicScene scene)
    {
        var width = scene.Width - (scene.Columns - 1) * scene.ColumnPitch;
        for (var column = 0; column < scene.Columns; column++)
        {
            var paper = new Rectangle { Width = Math.Max(1, width), Height = Math.Max(1, scene.Height), Fill = Brushes.White, Stroke = Brushes.LightGray, StrokeThickness = .5, IsHitTestVisible = false };
            Canvas.SetLeft(paper, column * scene.ColumnPitch); _canvas.Children.Add(paper);
        }
    }
    private void AddVisual(CabGraphicItem item)
    {
        var visual = new Grid { Width = Math.Max(1, item.LocalBounds.Width), Height = Math.Max(1, item.LocalBounds.Height), Background = Brushes.Transparent, Tag = item };
        visual.Children.Add(new CabGraphicVisual(item, _layout.Dpi));
        var transform = CabGraphicTransform.Placement(item.LocalBounds.X, item.LocalBounds.Y, 0).Then(item.Transform);
        visual.RenderTransform = new MatrixTransform(transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX, transform.OffsetY);
        visual.MouseLeftButtonDown += (_, args) => DragStart(item, visual, args);
        _canvas.Children.Add(visual);
    }

    private void Select()
    {
        _selection.Points.Clear(); if (Selected is not { } item) { _notes.Text = ""; return; }
        foreach (var point in item.LocalBounds.Corners.Select(item.Transform.Apply)) _selection.Points.Add(new(point.X, point.Y));
        _inputs["X"].Text = F(CabGraphicValues.Number(item.Command, "x")); _inputs["Y"].Text = F(CabGraphicValues.Number(item.Command, "y"));
        _inputs["Drehung"].Text = F(CabGraphicValues.Number(item.Command, "r"));
        var factor = item.Command.Syntax.Unit == CabUnit.Inches ? _layout.Dpi : _layout.Dpi / 25.4;
        _inputs["Breite"].Text = F(item.ContentBounds.Width / factor); _inputs["Höhe"].Text = F(item.ContentBounds.Height / factor);
        _notes.Text = item.Command.Definition.Catalog.Id + " · " + item.Command.Syntax.Unit + "\n" + string.Join("\n", item.Notes);
    }
    private static string F(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private double Input(string name) => double.Parse(_inputs[name].Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
