using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Testumgebung.Desktop.Controls;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Direkte Eingaben an genau einer Ablaufkarte; alle Änderungen laufen über die vorhandenen Undo-Modelle.</summary>
public sealed partial class InlineBlockEditor : UserControl
{
    public bool Compact { get; init; }
    private NodeViewModel? Node => DataContext as NodeViewModel;
    private EditorViewModel? Editor => Host?.DataContext is ShellViewModel shell ? shell.Editor : null;
    private EditorView? Host
    {
        get { for (DependencyObject? element = this; element is not null; element = VisualTreeHelper.GetParent(element)) if (element is EditorView view) return view; return null; }
    }
    public InlineBlockEditor()
    {
        Loaded += (_, _) => Build();
        DataContextChanged += (_, _) => { if (IsLoaded) Build(); };
    }
    private void Build()
    {
        if (Node is not { InlineProperties: { } properties } node || Editor is not { } editor) return;
        if (Compact) { BuildCompact(properties); return; }
        var panel = new StackPanel();
        var heading = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(Button("+ Schritt", (_, _) => ChooseBlock(), "Schritt in diese Gruppe oder hinter diesen Block einfügen"));
        if (node.CanMove) actions.Children.Add(Button("⋯", (_, _) => ShowActions(), "Kopieren, verschieben oder löschen"));
        heading.Children.Add(actions);
        var title = Text(properties, "Name", 240); title.FontWeight = FontWeights.SemiBold; title.FontSize = 16;
        title.Background = Brushes.Transparent; title.BorderThickness = new Thickness(0);
        AutomationProperties.SetName(title, "Blockname"); heading.Children.Add(title); panel.Children.Add(heading);
        var fields = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (properties is StepPropertiesViewModel step) BuildStep(fields, step);
        if (properties is GroupPropertiesViewModel group) BuildGroup(fields, group);
        panel.Children.Add(fields);
        var advanced = new Expander { Header = "Kommentar, Haltepunkte & weitere Optionen", Margin = new Thickness(0, 8, 0, 0) };
        // Die umfangreichen Spezialoptionen erst auf Wunsch erzeugen. Sie bleiben
        // im selben Block, statt einen zweiten Einstellungsbereich zu öffnen.
        advanced.Expanded += (_, _) => advanced.Content ??= new ContentControl { Content = properties, Margin = new Thickness(4, 10, 4, 4) };
        panel.Children.Add(advanced);
        Content = panel;
    }
    private void BuildStep(Panel fields, StepPropertiesViewModel step)
    {
        foreach (var row in step.Parameters)
        {
            if (row.IsVariable && row.Value.StartsWith("_BlockZweig_", StringComparison.Ordinal)) continue;
            var field = new StackPanel();
            BindParameterVisibility(field, row);
            AddField(field, row.Label, ParameterInput(row), row.Hint);
            fields.Children.Add(field);
        }
        if (step.Bindings is not { } bindings) return;
        var chooser = new ComboBox { ItemsSource = bindings.Blocks, DisplayMemberPath = "Name", MinWidth = 220 };
        chooser.SetBinding(Selector.SelectedItemProperty, Bind(bindings, "SelectedBlock"));
        AddField(fields, "Baustein", chooser);
        var ports = new ItemsControl { ItemsSource = bindings.Rows };
        ports.ItemTemplate = (DataTemplate)FindResource("InlinePortTemplate");
        fields.Children.Add(ports);
    }
    private void BindParameterVisibility(FrameworkElement field, ParameterRowViewModel row)
        => field.SetBinding(VisibilityProperty, new Binding(nameof(row.IsHidden))
        { Source = row, Converter = (IValueConverter)FindResource("BoolVis"), ConverterParameter = "invert" });

    private FrameworkElement ParameterInput(ParameterRowViewModel row)
    {
        FrameworkElement input;
        if (row.IsBool)
        {
            var check = new CheckBox { Content = "Ja", VerticalAlignment = VerticalAlignment.Center };
            check.SetBinding(ToggleButton.IsCheckedProperty, Bind(row, "BoolValue")); input = check;
        }
        else if (row.IsChoice)
        {
            var choice = new ComboBox { ItemsSource = row.Choices, MinWidth = 160 };
            choice.SetBinding(Selector.SelectedItemProperty, Bind(row, "Value")); input = choice;
        }
        else if (row.IsCode)
        {
            var code = new NumberedCodeEditor { MinWidth = 480, MinHeight = 180, MaxHeight = 320 };
            code.SetBinding(NumberedCodeEditor.TextProperty, Bind(row, "Value")); input = code;
        }
        else if (row.IsVariable)
        {
            var choice = new ComboBox { IsEditable = true, ItemsSource = Variables(writable: true), MinWidth = 180 };
            choice.SetBinding(ComboBox.TextProperty, Bind(row, "Value"));
            choice.DropDownOpened += (_, _) =>
            {
                var current = row.Value;
                choice.ItemsSource = Variables(writable: true);
                choice.SetCurrentValue(ComboBox.TextProperty, current);
            };
            input = choice;
        }
        else input = Text(row, "Value", row.IsMultiline ? 300 : 185, row.IsMultiline);
        input.DataContext = row;
        AutomationProperties.SetName(input, row.Label);
        if (!row.HasVisualBuilder) return input;
        var stack = new StackPanel(); stack.Children.Add(input);
        stack.Children.Add(ExpressionSlot(row, null)); return stack;
    }
    private void BuildGroup(Panel fields, GroupPropertiesViewModel group)
    {
        if (group.IsSection) { AddVariables(fields, group); return; }
        if (Node?.Children.FirstOrDefault() is { IsTemplateDecision: true, InlineProperties: StepPropertiesViewModel decision })
        {
            AddField(fields, "Wenn diese Bedingung stimmt", ParameterInput(decision.Parameters.Single(parameterRowViewModel => parameterRowViewModel.Descriptor.Name == "Ausdruck")));
            return;
        }
        if (Node?.Group?.EntryCondition?.TrimStart('!').StartsWith("_BlockZweig_", StringComparison.Ordinal) == true)
        { fields.Children.Add(new TextBlock { Text = "Auswahl über die Bedingung im Wenn-Block", Foreground = Brushes.DarkSlateGray, Margin = new Thickness(4) }); return; }
        var condition = new StackPanel(); condition.Children.Add(Text(group, "EntryCondition", 220));
        condition.Children.Add(ExpressionSlot(group, "entry"));
        AddField(fields, "Nur wenn (leer = immer)", condition);
        var repeat = new StackPanel();
        var modes = new ComboBox { ItemsSource = GroupPropertiesViewModel.RepeatModes, MinWidth = 170 };
        modes.ItemTemplate = EnumTemplate(); modes.SetBinding(Selector.SelectedItemProperty, Bind(group, "RepeatMode")); repeat.Children.Add(modes);
        repeat.Children.Add(Conditional(Text(group, "Count", 80), group, "ShowCount"));
        repeat.Children.Add(Conditional(Text(group, "DurationSeconds", 80), group, "ShowDuration"));
        var expression = new StackPanel(); expression.Children.Add(Text(group, "RepeatExpression", 220)); expression.Children.Add(ExpressionSlot(group, "repeat"));
        repeat.Children.Add(Conditional(expression, group, "ShowExpression"));
        AddField(fields, "Wiederholung", repeat);
        var parallel = new CheckBox { Content = "Gleichzeitig ausführen", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 10, 4, 0) };
        parallel.SetBinding(ToggleButton.IsCheckedProperty, Bind(group, "Parallel")); fields.Children.Add(parallel);
        AddVariables(fields, group);
    }
    private void AddVariables(Panel fields, GroupPropertiesViewModel group)
    {
        var variables = new Expander { Header = "Variablen in dieser Gruppe", Margin = new Thickness(4, 8, 4, 0), MinWidth = 280 };
        variables.Expanded += (_, _) => variables.Content ??= new ContentControl { Content = group.Variables, ContentTemplate = (DataTemplate)FindResource("VariableListTemplate") };
        fields.Children.Add(variables);
    }
    private FrameworkElement ExpressionSlot(object source, string? kind)
    {
        var expander = new Expander { Header = "◈ Wert aus Blöcken", Margin = new Thickness(0, 5, 0, 0), DataContext = source, Tag = kind };
        expander.Expanded += (_, _) => { if (expander.Content is null) Host?.OpenInlineExpression(this, expander); };
        return expander;
    }
    internal NodeViewModel? EditedNode => Node;
    private IReadOnlyList<string> Variables(bool writable) => ExpressionVariableChoices.ForNode(Editor!.Project.Definition, Node!.Id, writable).Select(variable => variable.Path).ToArray();
    private void ChooseBlock(NodeViewModel? target = null)
    {
        if ((target ?? Node) is not { } node || Editor is not { } editor) return;
        var menu = new ContextMenu();
        foreach (var category in editor.Palette)
        {
            var group = new MenuItem { Header = category.Name };
            foreach (var item in category.Items)
            {
                var entry = new MenuItem { Header = item.DisplayName, ToolTip = item.Description, Tag = item };
                entry.Click += (_, _) =>
                {
                    if (!ReferenceEquals(editor.FindNode(node.Id), node)) return;
                    var parent = node.IsGroup ? node : node.Parent!;
                    var index = node.IsGroup ? parent.Group!.Children.Count : parent.Group!.Children.IndexOf(node.Definition) + 1;
                    editor.InsertAt(item, parent, index);
                };
                group.Items.Add(entry);
            }
            menu.Items.Add(group);
        }
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private void ShowActions()
    {
        if (Editor is not { } editor || Node is not { } node) return;
        editor.SelectedNode = node;
        var menu = new ContextMenu();
        foreach (var (label, command) in new[] { ("Duplizieren", editor.DuplicateCommand), ("Nach oben", editor.MoveUpCommand), ("Nach unten", editor.MoveDownCommand), ("Aktiv / inaktiv", editor.ToggleEnabledCommand), ("Löschen", editor.DeleteCommand) })
            menu.Items.Add(new MenuItem { Header = label, Command = command });
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private static Binding Bind(object source, string path) => new(path) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true };
    private static TextBox Text(object source, string path, double width, bool multiline = false)
    {
        var text = new TextBox { MinWidth = width, MaxWidth = width + 100, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var binding = Bind(source, path); binding.UpdateSourceTrigger = UpdateSourceTrigger.LostFocus;
        text.SetBinding(TextBox.TextProperty, binding); return text;
    }
    private static void AddField(Panel panel, string label, FrameworkElement input, string? hint = null)
    {
        var field = new StackPanel { Margin = new Thickness(4, 2, 12, 8), ToolTip = hint };
        field.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.DarkSlateGray, Margin = new Thickness(0, 0, 0, 4) });
        field.Children.Add(input); panel.Children.Add(field);
    }
    private static Button Button(string label, RoutedEventHandler click, string hint)
    {
        var button = new Button { Content = label, ToolTip = hint, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(4, 0, 0, 0) };
        button.SetResourceReference(StyleProperty, "ToolButton"); button.Click += click; return button;
    }
    private DataTemplate EnumTemplate() => (DataTemplate)FindResource("InlineEnumTemplate");
    private FrameworkElement Conditional(FrameworkElement element, object source, string path)
    { element.SetBinding(VisibilityProperty, new Binding(path) { Source = source, Converter = (System.Windows.Data.IValueConverter)FindResource("BoolVis") }); return element; }
}
