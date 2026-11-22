using System.IO;
using System.Windows;
using System.Windows.Controls;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabPropertiesWindow : Window
{
    private readonly LabelLayout _layout;
    private readonly Action _refresh;
    private readonly ListBox _jobs = new() { MinWidth = 280 };
    private readonly StackPanel _fields = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _optional = new() { Content = "Nicht gesetzte Parameter zeigen", Margin = new(6) };
    private readonly ComboBox _catalog = new() { MinWidth = 280, MaxWidth = 420, DisplayMemberPath = "Title" };
    private CabSourceEditor _editor;
    private readonly CabSourceHistory _history;

    internal static void Attach(Panel panel, LabelDesignerWindow owner, LabelLayout layout, bool cab)
    {
        if (!cab) return;
        var button = new Button { Content = "JScript-Eigenschaften …", Margin = new(12, 0, 0, 0) };
        button.Click += (_, _) =>
        {
            try { new CabPropertiesWindow(layout, owner.RefreshCabSource) { Owner = owner }.ShowDialog(); }
            catch (InvalidDataException error) { owner.ReportCabSourceError(error.Message); }
        };
        panel.Children.Add(button);
    }

    internal CabPropertiesWindow(LabelLayout layout, Action? refresh = null)
    {
        _layout = layout; _refresh = refresh ?? (() => { });
        _editor = new(CabLabelLayout.Render(layout), layout.CabSource?.SetupCodePage ?? "UTF-8", layout.CabSettings);
        _history = new(_editor);
        var before = layout.CabSource is { } source ? new CabSourceEditor(source.Data, source.SetupCodePage, layout.CabSettings) : _editor;
        CabSourceReconcile.Apply(layout, before, _editor); _refresh();
        Title = "cab-JScript-Eigenschaften"; Width = 1160; Height = 780; MinWidth = 850; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = CreateContent();
        _catalog.ItemsSource = CabCatalog.Default.Entries;
        CabCatalogControls.ExplainGeneration(_catalog, _editor.Settings.DefaultGeneration);
        _catalog.SelectedIndex = 0;
        _jobs.SelectionChanged += (_, _) => ShowFields();
        _optional.Checked += (_, _) => ShowFields(); _optional.Unchecked += (_, _) => ShowFields();
        ShowDocument(0);
    }

    internal CabSourceEditor Editor => _editor;
    internal CabJobItem? Selected => _jobs.SelectedItem as CabJobItem;
    internal void SelectSourceOffset(int offset)
        => _jobs.SelectedItem = _jobs.Items.Cast<CabJobItem>().FirstOrDefault(i => i.Range.Offset == offset);

    private UIElement CreateContent()
    {
        var root = new DockPanel { Margin = new(12) };
        var bottom = new StackPanel(); bottom.Children.Add(_status);
        var close = new Button { Content = "Zurück zur Grafik", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        bottom.Children.Add(close); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var tools = new WrapPanel();
        tools.Children.Add(_catalog);
        AddButton(tools, "Befehl hinzufügen", AddSelectedCommand);
        AddButton(tools, "Definition ansehen", ShowDefinition);
        var resourceType = new ComboBox { MinWidth = 90, MaxWidth = 220, ToolTip = "Downloadtyp · " + _editor.Settings.DefaultGeneration };
        foreach (var choice in CabResourceEditor.TypeChoices(_editor.Settings.DefaultGeneration))
            resourceType.Items.Add(new ComboBoxItem { Content = choice.Value, Tag = choice, IsEnabled = choice.IsEnabled, ToolTip = choice.Description + " · " + choice.Availability });
        resourceType.SelectedItem = resourceType.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
        tools.Children.Add(resourceType);
        AddButton(tools, "Ressource hinzufügen …", () =>
        {
            if (resourceType.SelectedItem is not ComboBoxItem { Tag: CabPropertyChoice { IsEnabled: true } type }) return;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Ressource " + type.Value + " hinzufügen" };
            if (dialog.ShowDialog(this) != true) return;
            var target = Selected is { IsEmbedded: false } item ? item : null;
            Apply(CabResourceEditor.Add(_editor, type.Value, Path.GetFileNameWithoutExtension(dialog.FileName), File.ReadAllBytes(dialog.FileName), target));
        });
        AddButton(tools, "Neuer Auftrag", () => Apply(CabJobEditor.Insert(_editor, System.Text.Encoding.ASCII.GetBytes("J\rA 1\r"))));
        AddButton(tools, "Kommentar hinzufügen", () => Apply(CabJobEditor.Insert(_editor, "; Kommentar\r"u8.ToArray(), Selected is { IsEmbedded: false } item ? item : null)));
        AddButton(tools, "Entfernen", () => { if (Selected is { } item) Apply(CabJobEditor.Remove(_editor, item)); });
        AddButton(tools, "Nach oben", () => Move(-1)); AddButton(tools, "Nach unten", () => Move(1));
        AddButton(tools, "Rückgängig", () => Apply(_history.Undo(), false)); AddButton(tools, "Wiederholen", () => Apply(_history.Redo(), false));
        tools.Children.Add(_optional); DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        DockPanel.SetDock(_jobs, Dock.Left); root.Children.Add(_jobs);
        root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(12, 0, 0, 0) });
        return root;
    }

    private void AddButton(Panel panel, string title, Action action)
    {
        var button = new Button { Content = title, Margin = new(3), Padding = new(6, 3, 6, 3) };
        button.Click += (_, _) => Run(action); panel.Children.Add(button);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or FormatException or NotSupportedException)
        { _status.Text = error.Message; }
    }

    internal void Apply(CabSourceEditor next, bool record = true)
    {
        var offset = Selected?.Range.Offset ?? 0;
        if (_editor.Document.Write().AsSpan().SequenceEqual(next.Document.Write())) return;
        CabSourceReconcile.Apply(_layout, _editor, next);
        if (record) _history.Record(next);
        _editor = next;
        _refresh(); ShowDocument(offset);
    }

    private void ShowDocument(int offset)
    {
        _jobs.ItemsSource = CabJobEditor.Items(_editor);
        _jobs.SelectedItem = _jobs.Items.Cast<CabJobItem>().FirstOrDefault(i => i.Range.Offset >= offset) ?? _jobs.Items.Cast<CabJobItem>().LastOrDefault();
        _status.Text = string.Join("\n", CabJobEditor.OrderIssues(_editor).Select(i => i.Message).Concat(_editor.Validation.Diagnostics.Select(d => d.Message)).Take(8));
        ShowFields();
    }

    private void AddSelectedCommand()
    {
        if (_catalog.SelectedItem is not CabCatalogEntry entry) return;
        var generation = Selected?.Command is { } command ? _editor.Settings.GenerationForJob(command.JobIndex) : _editor.Settings.DefaultGeneration;
        if (entry.Availability(generation) == CabAvailability.Unsupported) throw new InvalidOperationException("Dieser Befehl ist für " + generation + " nicht verfügbar. Seine Definition bleibt lesbar.");
        var target = Selected is { IsEmbedded: false } item ? item : null;
        Apply(CabJobEditor.AddCommand(_editor, entry.Id, target));
    }

    private void ShowDefinition()
    {
        if (_catalog.SelectedItem is not CabCatalogEntry entry) return;
        var panel = new StackPanel { Margin = new(12) };
        panel.Children.Add(new TextBlock { Text = CabSourceCatalog.Tooltip(entry), TextWrapping = TextWrapping.Wrap });
        foreach (var property in CabPropertySchema.ForEntry(entry.Id, _editor.Settings.DefaultGeneration))
        {
            var text = property.Name + " · " + property.ControlText + " · " + property.Unit + " · " + property.AvailabilityText + "\n" + property.Help;
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
            foreach (var choice in property.Choices)
                panel.Children.Add(new TextBlock { Text = choice.ToString() + " · " + choice.Availability, TextWrapping = TextWrapping.Wrap, IsEnabled = choice.IsEnabled });
        }
        new Window { Owner = this, Title = entry.Title, Width = 760, Height = 650,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
    }

    private void Move(int direction)
    {
        if (Selected is not { IsEmbedded: false } item) return;
        var rows = CabJobEditor.Items(_editor).Where(i => !i.IsEmbedded).ToArray();
        var index = Array.FindIndex(rows, i => i.Range == item.Range);
        if (index < 0 || index + direction < 0 || index + direction >= rows.Length) return;
        var targetIndex = direction < 0 ? index - 1 : index + 2;
        Apply(CabJobEditor.MoveBefore(_editor, item, targetIndex < rows.Length ? rows[targetIndex] : null));
    }
}
