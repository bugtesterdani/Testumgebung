using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public sealed partial class InlineBlockEditor
{
    // A quick-edit surface, not a second property inspector. The same rows retain Undo and validation.
    private static readonly IReadOnlyDictionary<string, string[]> QuickFields = new Dictionary<string, string[]>
    {
        [BlockCatalog.Measure] = ["Ressource", "Ziel"],
        [BlockCatalog.Output] = ["Ressource", "Wert"],
        [BlockCatalog.DigitalRead] = ["Ressource", "Ziel"],
        [BlockCatalog.DigitalWrite] = ["Ressource", "Wert"],
        [BlockCatalog.SetValue] = ["Ziel", "Ausdruck"],
        [BlockCatalog.Evaluate] = ["Art", "Bedingung", "Bewertung"],
        [BlockCatalog.SetRating] = ["Bewertung"],
        [NetworkBlocks.Upload] = ["Richtung", "Ressource"],
        [BlockCatalog.Wait] = ["Dauer"],
        [BlockCatalog.RaiseError] = ["Meldung"],
        [BlockCatalog.Operator] = ["Text", "Ziel"],
        [BlockCatalog.Code] = ["Sprache"],
    };

    private void BuildCompact(PropertiesViewModel properties)
    {
        var panel = new StackPanel();
        if (properties is StepPropertiesViewModel step) CompactStep(panel, step);
        if (properties is GroupPropertiesViewModel group) CompactGroup(panel, group);
        Content = panel;
    }

    private void CompactStep(Panel panel, StepPropertiesViewModel step)
    {
        var names = QuickFieldNames(step);
        foreach (var name in names)
        {
            var row = step.Parameters.FirstOrDefault(parameterRowViewModel => parameterRowViewModel.Descriptor.Name == name);
            if (row is null || !UsefulQuickField(step, row)) continue;
            var field = new StackPanel();
            BindParameterVisibility(field, row);
            CompactRow(field, row.Label, CompactInput(row), row.Hint);
            panel.Children.Add(field);
        }
        if (step.Bindings is not { } bindings) return;
        var choice = new ComboBox { ItemsSource = bindings.Blocks, DisplayMemberPath = "Name" };
        choice.SetBinding(Selector.SelectedItemProperty, Bind(bindings, "SelectedBlock"));
        CompactRow(panel, "Baustein", choice);
    }

    private static IEnumerable<string> QuickFieldNames(StepPropertiesViewModel step)
        => QuickFields.GetValueOrDefault(step.Step.BlockType ?? "")
            ?? step.Parameters.Where(parameterRowViewModel => parameterRowViewModel.Descriptor.Required && !parameterRowViewModel.IsCode && !parameterRowViewModel.IsHidden).Take(2).Select(parameterRowViewModel => parameterRowViewModel.Descriptor.Name).ToArray();

    private static bool UsefulQuickField(StepPropertiesViewModel step, ParameterRowViewModel row)
        => step.Step.BlockType != BlockCatalog.Operator || row.Descriptor.Name != "Ziel"
            || step.Step.Parameters.GetValueOrDefault("Art") is "Eingabe" or "Auswahl";

    private FrameworkElement CompactInput(ParameterRowViewModel row)
    {
        FrameworkElement input;
        if (row.IsChoice)
        {
            var choice = new ComboBox { ItemsSource = row.Choices };
            choice.SetBinding(Selector.SelectedItemProperty, Bind(row, "Value")); input = choice;
        }
        else if (row.IsVariable || row.Kind == ParameterKind.Resource) input = CompactChoice(row);
        else input = CompactText(row, "Value");
        input.DataContext = row;
        AutomationProperties.SetName(input, row.Label);
        return row.HasVisualBuilder ? WithExpressionButton(input, row, null) : input;
    }

    private ComboBox CompactChoice(ParameterRowViewModel row)
    {
        IReadOnlyList<string> Choices() => row.IsVariable ? Variables(true) : ResourceChoices();
        var choice = new ComboBox { IsEditable = true, ItemsSource = Choices() };
        choice.SetBinding(ComboBox.TextProperty, Bind(row, "Value"));
        choice.DropDownOpened += (_, _) => { var current = row.Value; choice.ItemsSource = Choices(); choice.SetCurrentValue(ComboBox.TextProperty, current); };
        return choice;
    }

    private IReadOnlyList<string> ResourceChoices()
    {
        var project = Editor!.Project;
        var configured = Host?.DataContext is ShellViewModel shell
            ? shell.StationsPage.Draft.Station(shell.SelectedStation.Name)?.Resources.Select(assignment => assignment.Name) ?? [] : [];
        return configured.Concat(project.Simulation.Select(resource => resource.Name)).Concat(project.SimulationOutputs.Select(output => output.Name))
            .Concat(project.SimulationComm.Select(communication => communication.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private void CompactGroup(Panel panel, GroupPropertiesViewModel group)
    {
        var node = Node!;
        if (node.Children.FirstOrDefault() is { IsTemplateDecision: true, InlineProperties: StepPropertiesViewModel decision })
        {
            var row = decision.Parameters.Single(parameterRowViewModel => parameterRowViewModel.Descriptor.Name == "Ausdruck");
            CompactRow(panel, "Wenn", CompactInput(row)); return;
        }
        if (node.IsSection || node.Parent?.Children.FirstOrDefault()?.IsTemplateDecision == true) return;
        CompactRepetition(panel, group, node.Group!.Repetition.Mode);
    }

    private void CompactRepetition(Panel panel, GroupPropertiesViewModel group, RepeatMode mode)
    {
        if (mode is RepeatMode.While or RepeatMode.Until)
            CompactRow(panel, "Bedingung", WithExpressionButton(CompactText(group, "RepeatExpression"), group, "repeat"));
        else if (mode == RepeatMode.Count)
            CompactRow(panel, "Anzahl", CompactText(group, "Count"));
        else if (mode == RepeatMode.Duration)
            CompactRow(panel, "Sekunden", CompactText(group, "DurationSeconds"));
        else if (!string.IsNullOrWhiteSpace(group.EntryCondition))
            CompactRow(panel, "Nur wenn", WithExpressionButton(CompactText(group, "EntryCondition"), group, "entry"));
    }

    private FrameworkElement WithExpressionButton(FrameworkElement input, object source, string? kind)
    {
        var panel = new DockPanel();
        var edit = Button("…", (_, _) => { }, "Im Bedingungs- oder Ausdruckseditor bearbeiten");
        edit.DataContext = source; edit.Tag = kind;
        AutomationProperties.SetName(edit, "Ausdruck im Editor bearbeiten");
        edit.Click += (_, _) => Host?.OpenGraphExpression(edit);
        edit.Padding = new Thickness(5, 0, 5, 0); DockPanel.SetDock(edit, Dock.Right);
        panel.Children.Add(edit); panel.Children.Add(input); return panel;
    }

    private static TextBox CompactText(object source, string path)
    {
        var text = new TextBox { MinWidth = 40, MinHeight = 26, MaxHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        var binding = Bind(source, path); binding.UpdateSourceTrigger = UpdateSourceTrigger.LostFocus;
        text.SetBinding(TextBox.TextProperty, binding); return text;
    }

    private static void CompactRow(Panel panel, string label, FrameworkElement input, string? hint = null)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = hint };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label });
        Grid.SetColumn(input, 1); row.Children.Add(input); panel.Children.Add(row);
    }

    internal void AddGraphBlock(NodeViewModel? target = null) => ChooseBlock(target);
    internal void ShowGraphActions() => ShowActions();
}
