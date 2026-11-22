using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabSourceWindow : Window
{
    private readonly LabelLayout _layout;
    private readonly Action _refresh;
    private readonly StackPanel _source = new();
    private readonly ListBox _diagnostics = new() { MaxHeight = 160 };
    private readonly ListBox _completion = new() { MinWidth = 290 };
    private readonly TextBox _search = new() { Margin = new(0, 4, 0, 6) };
    private readonly TextBlock _help = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _undo = new() { Content = "Rückgängig", ToolTip = "Strg+Z" };
    private readonly Button _redo = new() { Content = "Wiederholen", ToolTip = "Strg+Y", Margin = new(6, 0, 0, 0) };
    private readonly Dictionary<int, RichTextBox> _editors = [];
    private CabSourceEditor _editor;
    private readonly CabSourceHistory _history;
    private RichTextBox? _active;
    private bool _updating;

    internal static void Attach(Panel panel, LabelDesignerWindow owner, LabelLayout layout, bool cab)
    {
        if (!cab) return;
        var button = new Button { Content = "Quelltext …", Margin = new(12, 0, 0, 0) };
        button.Click += (_, _) =>
        {
            var window = TryCreate(layout, owner.RefreshCabSource, owner.ReportCabSourceError);
            if (window is null) return;
            window.Owner = owner; window.ShowDialog();
        };
        panel.Children.Add(button);
    }

    internal static CabSourceWindow? TryCreate(LabelLayout layout, Action refresh, Action<string> report)
    {
        try { return new(layout, refresh); }
        catch (InvalidDataException error) { report(error.Message); return null; }
    }

    internal CabSourceWindow(LabelLayout layout, Action? refresh = null)
    {
        _layout = layout; _refresh = refresh ?? (() => { });
        var bytes = CabLabelLayout.Render(layout);
        _editor = new(bytes, layout.CabSource?.SetupCodePage ?? "UTF-8", layout.CabSettings);
        _history = new(_editor);
        if (layout.CabSource is { } source)
            CabSourceReconcile.Apply(layout, new(source.Data, source.SetupCodePage, layout.CabSettings), _editor);
        else CabSourceReconcile.Apply(layout, _editor, _editor);
        _refresh();
        Title = "cab-Quelltext"; Width = 1150; Height = 760; MinWidth = 800; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = CreateContent();
        _search.TextChanged += (_, _) => Complete();
        _completion.SelectionChanged += (_, _) => _help.Text = (_completion.SelectedItem as CabCompletionItem)?.Tooltip ?? "";
        _completion.MouseDoubleClick += (_, _) => InsertCompletion();
        _diagnostics.MouseDoubleClick += (_, _) => Navigate();
        PreviewKeyDown += OnKey;
        _undo.Click += (_, _) => TravelHistory(false);
        _redo.Click += (_, _) => TravelHistory(true);
        Complete(); ShowSource();
    }

    private UIElement CreateContent()
    {
        var root = new DockPanel { Margin = new(12) };
        var bottom = new StackPanel(); bottom.Children.Add(_status); bottom.Children.Add(_diagnostics);
        var close = new Button { Content = "Zurück zur Grafik", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 6, 0, 0) };
        bottom.Children.Add(close); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var side = new DockPanel { Width = 360, Margin = new(12, 0, 0, 0) };
        var top = new StackPanel();
        var history = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 6) };
        history.Children.Add(_undo); history.Children.Add(_redo); top.Children.Add(history);
        top.Children.Add(new TextBlock { Text = "Katalogvervollständigung · " + _editor.Settings.DefaultGeneration + " · Strg+Leertaste" });
        top.Children.Add(_search); top.Children.Add(new ScrollViewer { Content = _help, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var insert = new Button { Content = "Auswahl einfügen", Margin = new(0, 6, 0, 6) };
        insert.Click += (_, _) => InsertCompletion(); top.Children.Add(insert);
        DockPanel.SetDock(top, Dock.Top); side.Children.Add(top); side.Children.Add(_completion);
        DockPanel.SetDock(side, Dock.Right); root.Children.Add(side);
        root.Children.Add(new ScrollViewer { Content = _source, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private void Complete()
    {
        _completion.ItemsSource = CabSourceCatalog.Complete(_editor.Settings.DefaultGeneration, _search.Text);
        _completion.SelectedIndex = 0;
    }

    private void ShowSource()
    {
        _updating = true;
        try
        {
            _source.Children.Clear(); _editors.Clear();
            for (var index = 0; index < _editor.Sections.Count; index++) AddSection(index);
            _active = _editors.Values.FirstOrDefault();
            _undo.IsEnabled = _history.CanUndo; _redo.IsEnabled = _history.CanRedo;
            ShowDiagnostics();
        }
        finally { _updating = false; }
    }

    private void AddSection(int index)
    {
        var section = _editor.Sections[index];
        if (section.IsProtected)
        {
            _source.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new(1), Padding = new(8),
                Child = new TextBlock { Text = "▸ " + section.Summary, ToolTip = "Originalbytes bleiben erhalten; kein Textzugriff auf diesen Block." } });
            return;
        }
        var row = new DockPanel();
        var marker = Gutter(index);
        DockPanel.SetDock(marker, Dock.Left); row.Children.Add(marker);
        var box = new RichTextBox { FontFamily = new("Consolas"), FontSize = 14, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, BorderThickness = new(0), Padding = new(3) };
        System.Windows.Automation.AutomationProperties.SetName(box, "JScript-Quelltext, Abschnitt " + (index + 1));
        box.Document = new FlowDocument(new Paragraph(new Run(section.Editable!.Text)) { Margin = new(0), LineHeight = 18 }) { PagePadding = new(0) };
        box.GotKeyboardFocus += (_, _) => _active = box;
        box.TextChanged += (_, _) => Changed(index, box);
        _editors[index] = box; row.Children.Add(box); _source.Children.Add(row);
        Format(index, box);
    }

    private void ShowDiagnostics()
    {
        _diagnostics.ItemsSource = _editor.Validation.Diagnostics.Select(d => new DiagnosticRow(d));
        _status.Text = $"{_editor.Validation.Diagnostics.Count} Diagnosen · ␛e = ESC, ␛u = wörtliches ␛. Änderungen sind sofort in der Grafik verfügbar. Geschützte Blöcke bleiben unverändert.";
    }

    private void Changed(int index, RichTextBox box)
    {
        if (_updating) return;
        var caret = new TextRange(box.Document.ContentStart, box.Selection.Start).Text.ReplaceLineEndings("\n").Length;
        try
        {
            var text = new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;
            if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
            ApplyText(index, text);
            RestoreCaret(index, caret);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or FormatException or System.Text.EncoderFallbackException)
        { _status.Text = error.Message; }
    }

    private void RestoreCaret(int index, int character)
    {
        if (!_editors.TryGetValue(index, out var box)) return;
        var position = At(box, character);
        box.Selection.Select(position, position); _active = box; box.Focus();
    }

    internal void ApplyText(int index, string text)
    {
        var next = _editor.Edit(index, text);
        if (!_history.Record(next)) return;
        CabSourceReconcile.Apply(_layout, _editor, next); _editor = next;
        _refresh(); ShowSource();
    }

    private void TravelHistory(bool redo)
    {
        var next = redo ? _history.Redo() : _history.Undo();
        if (ReferenceEquals(next, _editor)) return;
        CabSourceReconcile.Apply(_layout, _editor, next); _editor = next;
        _refresh(); ShowSource(); RestoreCaret(0, 0);
    }

    internal CabSourceEditor Editor => _editor;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (!_search.IsKeyboardFocusWithin && e.Key is Key.Z or Key.Y)
        { TravelHistory(e.Key == Key.Y); e.Handled = true; return; }
        if (e.Key != Key.Space) return;
        _search.Focus(); _search.SelectAll(); e.Handled = true;
    }

    private void InsertCompletion()
    {
        if (_completion.SelectedItem is not CabCompletionItem item || _active is null) return;
        _active.Selection.Text = item.Insertion;
        _active?.Focus();
    }
}
