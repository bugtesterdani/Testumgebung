using System.IO;
using System.Windows;
using System.Windows.Controls;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Generation choices belong to the project, never to the printer source.</summary>
internal sealed class CabValidationWindow : Window
{
    private readonly LabelLayout _layout;
    private readonly StackPanel _generations = new();
    private readonly ListBox _diagnostics = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    internal static void Attach(Panel panel, Window owner, LabelLayout layout, bool cab)
    {
        if (!cab) return;
        var button = new Button { Content = "Generation und Prüfung …", Margin = new Thickness(12, 0, 0, 0) };
        button.Click += (_, _) => new CabValidationWindow(layout) { Owner = owner }.ShowDialog();
        panel.Children.Add(button);
    }

    internal CabValidationWindow(LabelLayout layout)
    {
        _layout = layout;
        Title = "cab-Generation und Prüfung";
        Width = 760; Height = 520; MinWidth = 560; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Die Druckergeneration wird je Etikettenauftrag im Projekt gespeichert.", TextWrapping = TextWrapping.Wrap });
        top.Children.Add(_generations);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        var close = new Button { Content = "Schließen", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        _diagnostics.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(_diagnostics); Content = panel;
        InitializeChoices();
    }

    private CabDocument ReadDocument() => CabParser.Parse(CabLabelLayout.Render(_layout), new(_layout.CabSource?.SetupCodePage ?? "UTF-8"));

    private void InitializeChoices()
    {
        try
        {
            var settings = _layout.CabSettings ??= new();
            AddChoice("Standard / Vorspann", settings.DefaultGeneration, value => settings.DefaultGeneration = value);
            var result = CabValidator.Validate(ReadDocument(), settings);
            foreach (var index in result.Commands.Select(c => c.JobIndex).Where(i => i >= 0).Distinct())
                AddChoice("Etikettenauftrag " + (index + 1), settings.GenerationForJob(index), value => settings.JobGenerations[index] = value);
            Refresh();
        }
        catch (InvalidDataException error) { _status.Text = error.Message; }
    }

    private void AddChoice(string title, CabPrinterGeneration selected, Action<CabPrinterGeneration> save)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new TextBlock { Text = title, Width = 190, VerticalAlignment = VerticalAlignment.Center });
        var choices = new ComboBox { ItemsSource = Enum.GetValues<CabPrinterGeneration>(), SelectedItem = selected, MinWidth = 90 };
        System.Windows.Automation.AutomationProperties.SetName(choices, title + ": Druckergeneration");
        choices.SelectionChanged += (_, _) => { save((CabPrinterGeneration)choices.SelectedItem); Refresh(); };
        row.Children.Add(choices); _generations.Children.Add(row);
    }

    private void Refresh()
    {
        try
        {
            var result = CabValidator.Validate(ReadDocument(), _layout.CabSettings);
            _diagnostics.ItemsSource = result.Diagnostics.Select(d => new TextBlock
            {
                Text = $"{d.Severity} · Zeile {d.Line}, Spalte {d.Column} · {d.Message} ({d.ManualReference})",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
            });
            _status.Text = result.Diagnostics.Count == 0 ? "Keine Diagnosen aus der statischen Prüfung." : $"{result.Diagnostics.Count} Diagnosen. Quelldaten bleiben unverändert.";
        }
        catch (InvalidDataException error) { _status.Text = error.Message; }
    }
}
