using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal sealed class CabContentPreviewWindow : Window
{
    private readonly LabelLayout _layout;
    private readonly CabPreviewSettings _settings;
    private readonly TextBox _time = new();
    private readonly TextBox _culture = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    internal static void Attach(Panel panel, Window owner, LabelLayout layout, bool cab)
    {
        if (!cab) return;
        var button = new Button { Content = "Inhaltsfeld-Vorschau …", Margin = new Thickness(12, 0, 0, 0) };
        button.Click += (_, _) => new CabContentPreviewWindow(layout) { Owner = owner }.ShowDialog();
        panel.Children.Add(button);
    }

    internal CabContentPreviewWindow(LabelLayout layout)
    {
        _layout = layout; _settings = (layout.CabSettings ??= new()).Preview ??= new();
        Title = "cab-Inhaltsfelder: Offline-Vorschau";
        Width = 900; Height = 650; MinWidth = 650; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Beispielwerte für die Vorschau. Druckerabhängige Inhalte sind gekennzeichnete Platzhalter.", TextWrapping = TextWrapping.Wrap });
        _time.Text = _settings.ExampleTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _culture.Text = _settings.Culture;
        Input(top, "Beispielzeit (JJJJ-MM-TT hh:mm:ss)", _time);
        Input(top, "Vorschaukultur / Setup-Region", _culture);
        Choice(top, "Monats-Offsets", _settings.MonthPolicy, value => _settings.MonthPolicy = value);
        Choice(top, "Erste Kalenderwoche", _settings.WeekRule, value => _settings.WeekRule = value);
        Choice(top, "Erster Wochentag", _settings.FirstDayOfWeek, value => _settings.FirstDayOfWeek = value);
        Choice(top, "Bedingte Unsichtbarkeit (Handbuchwiderspruch)", _settings.VisibilityPolicy, value => _settings.VisibilityPolicy = value);
        var refresh = new Button { Content = "Vorschau aktualisieren", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        refresh.Click += (_, _) => Refresh(); top.Children.Add(refresh); top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var close = new Button { Content = "Schließen", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel; Refresh();
    }

    private static void Input(Panel panel, string label, TextBox input)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 300 }); input.MinWidth = 200;
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        row.Children.Add(input); panel.Children.Add(row);
    }

    private static void Choice<T>(Panel panel, string label, T selected, Action<T> save) where T : struct, Enum
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = label, Width = 300 });
        var choices = new ComboBox
        {
            ItemsSource = Enum.GetValues<T>().Select(value => new KeyValuePair<T, string>(value, ChoiceLabel(value))),
            DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = selected, MinWidth = 200
        };
        System.Windows.Automation.AutomationProperties.SetName(choices, label);
        choices.SelectionChanged += (_, _) => save((T)choices.SelectedValue);
        row.Children.Add(choices); panel.Children.Add(row);
    }

    private static string ChoiceLabel<T>(T value) where T : struct, Enum
    {
        if (value is DayOfWeek day) return CultureInfo.GetCultureInfo("de-DE").DateTimeFormat.GetDayName(day);
        return value.ToString() switch
        {
            "ThirtyDays" => "30 Tage je Monat", "CalendarMonths" => "Kalendermonate",
            "FirstDay" => "Woche mit 1. Januar", "FirstFullWeek" => "Erste volle Woche",
            "FirstFourDayWeek" => "Mindestens vier Tage",
            "ExampleHidesWhenTrue" => "Beispiel: wahr blendet aus", "TablePrintsWhenTrue" => "Tabelle: wahr druckt",
            _ => value.ToString()
        };
    }

    internal void Refresh()
    {
        try
        {
            _settings.ExampleTime = DateTime.ParseExact(_time.Text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            _ = CultureInfo.GetCultureInfo(_culture.Text); _settings.Culture = _culture.Text;
            var document = CabParser.Parse(CabLabelLayout.Render(_layout), new(_layout.CabSource?.SetupCodePage ?? "UTF-8"));
            var rows = CabDocumentPreview.Evaluate(document, _settings, _layout.CabSettings);
            _rows.Children.Clear();
            foreach (var row in rows) AddRow(row);
            _status.Text = $"{rows.Count} Datenfelder · {CabContentParser.Definitions.Count} Inhaltsfelddefinitionen. Quelle bleibt unverändert.";
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException)
        { _status.Text = error.Message; }
    }

    private void AddRow(CabPreviewField row)
    {
        _rows.Children.Add(new TextBlock { Text = $"Auftrag {row.Content.Command.JobIndex + 1} · {row.Name ?? "ohne Namen"}", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 4) });
        _rows.Children.Add(Source(row.Content.Syntax));
        _rows.Children.Add(new TextBlock { Text = "Vorschau: " + row.Preview.Text + (row.Preview.Hidden ? " (unsichtbar)" : ""), TextWrapping = TextWrapping.Wrap });
        foreach (var issue in row.Preview.Issues)
            _rows.Children.Add(new TextBlock { Text = issue.Message + (issue.CatalogId is { } id ? " · " + id + ", S. " + CabCatalog.Default[id].FirstPage : ""), TextWrapping = TextWrapping.Wrap });
    }

    internal static RichTextBox Source(CabContentSyntax syntax)
    {
        var paragraph = new Paragraph();
        var offset = 0;
        foreach (var field in syntax.Fields)
        {
            if (field.Offset < offset) continue;
            paragraph.Inlines.Add(new Run(syntax.Text[offset..field.Offset]));
            paragraph.Inlines.Add(new Run(syntax.Text.Substring(field.Offset, field.Length))
            {
                Foreground = field.Nested ? Brushes.DarkRed : Brushes.DarkBlue,
                FontWeight = FontWeights.Bold,
                ToolTip = field.Definition.Title + "\n" + string.Join("\n", field.Definition.Syntax) + "\n"
                    + field.Definition.Id + ", S. " + field.Definition.FirstPage
            });
            offset = field.Offset + field.Length;
        }
        paragraph.Inlines.Add(new Run(syntax.Text[offset..]));
        return new RichTextBox(new FlowDocument(paragraph)) { IsReadOnly = true, IsDocumentEnabled = true, BorderThickness = new Thickness(0), MinHeight = 40 };
    }
}
