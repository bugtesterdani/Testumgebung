using System.Windows;
using System.Windows.Controls;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Desktop.Views;

internal sealed class CabContentWindow : Window
{
    private readonly ComboBox _group = new() { MinWidth = 180 };
    private readonly ComboBox _catalog = new() { MinWidth = 300, DisplayMemberPath = "Title" };
    private readonly StackPanel _fields = new();
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CabPrinterGeneration _generation;
    private readonly CabPreviewSettings _settings;
    private readonly IReadOnlySet<string> _names;
    private CabContentForm? _form;
    internal string? Insertion { get; private set; }

    internal CabContentWindow(CabPrinterGeneration generation, CabPreviewSettings? settings = null, IReadOnlySet<string>? previousNames = null)
    {
        _generation = generation;
        _settings = settings ?? new();
        _names = previousNames ?? new HashSet<string>(StringComparer.Ordinal);
        CabCatalogControls.ExplainGeneration(_catalog, generation);
        Title = "JScript-Inhaltsfeld einfügen"; Width = 760; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(12) };
        var top = new StackPanel(); top.Children.Add(_group); top.Children.Add(_catalog);
        top.Children.Add(new TextBlock { Text = "Eigene Beispielwerte · vor dem Einfügen an den Auftrag anpassen.", TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel(); bottom.Children.Add(_preview); bottom.Children.Add(_status);
        var insert = new Button { Content = "Inhaltsfeld einfügen", IsDefault = true };
        insert.Click += (_, _) => { if (_form is null) return; Insertion = _form.Text; DialogResult = true; };
        bottom.Children.Add(insert); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = root;
        _group.ItemsSource = CabContentParser.Definitions.Select(Group).Distinct().Order().Prepend("Alle");
        _group.SelectionChanged += (_, _) =>
        {
            _catalog.ItemsSource = CabContentParser.Definitions.Where(d => _group.SelectedItem as string == "Alle" || Group(d) == _group.SelectedItem as string).ToArray();
            _catalog.SelectedIndex = 0;
        };
        _catalog.SelectionChanged += (_, _) =>
        {
            if (_catalog.SelectedItem is not CabCatalogEntry entry) return;
            insert.IsEnabled = entry.Availability(_generation) != CabAvailability.Unsupported;
            _form = new(entry.Id, _generation, _names); ShowForm();
        };
        _group.SelectedIndex = 0;
    }

    internal static string Group(CabCatalogEntry entry)
    {
        var section = int.Parse(entry.Id.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture);
        return Groups.FirstOrDefault(g => g.Sections.Contains(section)).Name ?? "Text und Daten";
    }

    private static readonly (string Name, int[] Sections)[] Groups =
    [
        ("Zeit", Enumerable.Range(1, 9).ToArray()),
        ("Datum", Enumerable.Range(10, 20).Append(38).ToArray()),
        ("Jalali", Enumerable.Range(30, 8).ToArray()),
        ("Mathematik", Enumerable.Range(39, 11).Concat([53, 54]).ToArray()),
        ("Prüfsummen", [50, 51, 52, 98]), ("RFID", Enumerable.Range(55, 11).ToArray()),
        ("Daten und Datenbanken", [79, 90, 91, 92, 94, 100, 101]), ("System", [84, 105, 106, 107, 108]),
    ];

    private void ShowForm()
    {
        _fields.Children.Clear(); if (_form is null) return;
        foreach (var property in _form.Properties)
        {
            var row = new StackPanel { Margin = new(0, 6, 0, 0), ToolTip = property.Definition.Help };
            row.Children.Add(new TextBlock { Text = property.Definition.Name + " · " + property.Definition.Unit + " · " + property.Definition.AvailabilityText + " · " + property.Definition.Rule.Parameter.Description, TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = property.Value ?? "", IsReadOnly = !property.IsTextEditable };
            row.Children.Add(box);
            if (property.Definition.Choices.Count > 0) CabPropertiesWindow.Choices(row, box, property.Definition);
            if (property.Definition.Range is not null) row.Children.Add(new TextBlock { Text = "Bereich: " + property.Definition.RangeText });
            var apply = new Button { Content = "Wert übernehmen", IsEnabled = property.IsTextEditable, HorizontalAlignment = HorizontalAlignment.Left };
            apply.Click += (_, _) => Change(property, box.Text); row.Children.Add(apply); _fields.Children.Add(row);
        }
        var preview = _form.Preview(_settings);
        _preview.Text = "Syntax: " + _form.Text + "\nOffline-Vorschau mit Beispielzeit " + _settings.ExampleTime.ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            + " und eigenen Beispieldaten:\n" + preview.Text + "\n" + string.Join("\n", preview.Issues.Select(i => i.Message));
    }

    private void Change(CabBoundProperty property, string value)
    {
        if (_form is null) return;
        try { _form = _form.Change(property, value); _status.Text = ""; ShowForm(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        { _status.Text = error.Message; }
    }
}
