using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Testumgebung.Desktop.Controls;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

public partial class EditorView
{
    private Point _paletteStart;
    private PaletteItem? _paletteDrag;
    private sealed record ExpressionField(string Label, Func<string> Read, Action<string> Write, VarType? Expected, bool VariableOnly, bool AllowEmpty);

    internal void OpenGraphExpression(FrameworkElement field) => OnExpressionBuilder(field, new RoutedEventArgs());

    internal void ShowGraphDetails(NodeViewModel node)
    {
        FlushPendingInputs(this);
        if (Editor is not { } editor || editor.FindNode(node.Id) is not { } current) return;
        editor.SelectedNode = current;
        editor.VisualBlocks = false;
        editor.GraphView = false;
    }

    internal void OpenInlineExpression(InlineBlockEditor block, Expander slot)
    {
        FlushPendingInputs(block);
        if (Editor is not { } editor || block.EditedNode is not { } node) return;
        node = ExpressionOwner(slot, node);
        var field = ExpressionFieldFor(slot, node);
        if (field is null) return;
        var project = editor.Project;
        var variables = ExpressionVariableChoices.ForNode(project.Definition, node.Id, field.VariableOnly);
        if (field.VariableOnly && field.Expected is { } expected) variables = variables.Where(variable => variable.Type == expected).ToList();
        var bindingIdentity = BindingIdentity(node);
        var model = new ExpressionBuilderViewModel(field.Read(), variables, field.Expected, field.VariableOnly, field.AllowEmpty);
        slot.Content = new InlineExpressionEditor(model, field.Read, source => editor.ApplyVisualEdit(() => field.Write(source)),
            () => ReferenceEquals(editor.Project, project) && ReferenceEquals(editor.Project.Definition.Find(node.Id), node.Definition)
                && BindingIdentity(node) == bindingIdentity && VariablesStillVisible(editor, node, field.VariableOnly, variables),
            () => { slot.Content = null; slot.IsExpanded = false; });
    }
    private static (string? Target, string? Library) BindingIdentity(NodeViewModel node)
        => (node.Step?.Parameters.GetValueOrDefault("Ziel"), node.Step?.Parameters.GetValueOrDefault("Baustein"));
    private static bool VariablesStillVisible(EditorViewModel editor, NodeViewModel node, bool writable, IReadOnlyList<ExpressionVariable> original)
    {
        var current = ExpressionVariableChoices.ForNode(editor.Project.Definition, node.Id, writable).ToHashSet();
        return original.All(current.Contains);
    }
    private static InlineBlockEditor? InlineAncestor(DependencyObject element)
    {
        for (DependencyObject? element2 = element; element2 is not null; element2 = VisualTreeHelper.GetParent(element2))
            if (element2 is InlineBlockEditor block) return block;
        return null;
    }
    private void OnInlinePortExpression(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Expander { Content: null } slot) return;
        if (InlineAncestor(slot) is { } block) OpenInlineExpression(block, slot);
    }
    private void OnInlineProject(object sender, RoutedEventArgs eventArgs)
    { if (ReferenceEquals(sender, eventArgs.OriginalSource) && Editor is { } editor) ProjectInline.Content = new ProjectPropertiesViewModel(editor); }

    private void OnExpressionBuilder(object sender, RoutedEventArgs eventArgs)
    {
        FlushPendingInputs(PropertyEditor);
        if (Editor is null || sender is not FrameworkElement element) return;
        var inline = InlineAncestor(element);
        if (inline is not null) FlushPendingInputs(inline);
        var node = inline?.EditedNode ?? Editor.SelectedNode;
        if (node is null) return;
        node = ExpressionOwner(element, node);
        var field = ExpressionFieldFor(element, node);
        if (field is null) return;
        var variables = ExpressionVariableChoices.ForNode(Editor.Project.Definition, node.Id, field.VariableOnly);
        if (field.VariableOnly && field.Expected is { } expected) variables = variables.Where(variable => variable.Type == expected).ToList();
        var original = field.Read();
        var model = new ExpressionBuilderViewModel(original, variables, field.Expected, field.VariableOnly, field.AllowEmpty);
        var project = Editor.Project;
        var window = new ExpressionBuilderWindow(model, field.Read,
            source => Editor.ApplyVisualEdit(() => field.Write(source)), field.Label,
            () => ReferenceEquals(Editor.Project, project) && ReferenceEquals(Editor.Project.Definition.Find(node.Id), node.Definition)) { Owner = Window.GetWindow(this) };
        if (window.Owner is { ActualWidth: > 0, ActualHeight: > 0 } owner)
        { window.Width = Math.Min(1100, Math.Max(760, owner.ActualWidth * .95)); window.Height = Math.Min(720, Math.Max(520, owner.ActualHeight * .95)); }
        window.ShowDialog();
    }

    // Verzögerte Texteingaben vor dem Dialog übernehmen. Nur geänderte Bindungen
    // aktualisieren, damit bloßes Öffnen und Abbrechen keine Projektänderung erzeugt.
    private static void FlushPendingInputs(DependencyObject root)
    {
        var children = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(root))
            .Select(index => VisualTreeHelper.GetChild(root, index)).ToArray();
        if (root is TextBox { IsReadOnly: false } text && text.GetBindingExpression(TextBox.TextProperty) is { IsDirty: true } binding)
            binding.UpdateSource();
        if (root is NumberedCodeEditor code && code.GetBindingExpression(NumberedCodeEditor.TextProperty) is { IsDirty: true } codeBinding)
            codeBinding.UpdateSource();
        foreach (var child in children) FlushPendingInputs(child);
    }

    // Eine Wenn/Sonst-Vorlage zeigt den Ausdruck ihres ausgeblendeten
    // Entscheidungsschritts direkt im Gruppenblock. Scope und Typ gehören weiter diesem Schritt.
    private NodeViewModel ExpressionOwner(FrameworkElement field, NodeViewModel fallback)
        => field.DataContext is ParameterRowViewModel row ? Editor?.FindNode(row.NodeId) ?? fallback : fallback;

    private ExpressionField? ExpressionFieldFor(FrameworkElement element, NodeViewModel node) => element.DataContext switch
    {
        ParameterRowViewModel row when row.HasVisualBuilder => new(row.Label, () => row.Value, value => row.Value = value,
            ParameterType(row, node), row.IsVariable, !row.Descriptor.Required),
        GroupPropertiesViewModel group => GroupField(group, element.Tag as string),
        PortBindingViewModel port => new(port.Name, () => port.Value, value => port.Value = value, port.Port.Type,
            port.Port.Direction != Testumgebung.Domain.Code.PortDirection.Input, port.Port.Direction == Testumgebung.Domain.Code.PortDirection.Output),
        _ => null
    };

    private static ExpressionField? GroupField(GroupPropertiesViewModel group, string? kind) => kind switch
    {
        "entry" => new("Eintrittsbedingung", () => group.EntryCondition, value => group.EntryCondition = value, VarType.Bool, false, true),
        "repeat" => new("Wiederholungsbedingung", () => group.RepeatExpression, value => group.RepeatExpression = value, VarType.Bool, false, false),
        _ => null
    };

    private VarType? ParameterType(ParameterRowViewModel row, NodeViewModel node)
    {
        if (row.IsVariable) return null;
        if (row.Descriptor.Name == "Bedingung") return VarType.Bool;
        if (node.Step?.BlockType == BlockCatalog.SetValue && row.Descriptor.Name == "Ausdruck")
        {
            var target = node.Step.Parameters.GetValueOrDefault("Ziel");
            return ExpressionVariableChoices.ForNode(Editor!.Project.Definition, node.Id).FirstOrDefault(variable => variable.Path == target)?.Type;
        }
        return row.Descriptor.Name is "Untergrenze" or "Obergrenze" ? VarType.Double : null;
    }

    private void OnDeclareVariable(object sender, RoutedEventArgs eventArgs)
    {
        if (Editor is not { } editor) return;
        var project = editor.Project;
        var scopes = editor.AllNodes().Where(node => node.Group is not null).Select(node => node.Group!).Prepend(editor.Project.Definition.Root).ToList();
        var group = new ComboBox { ItemsSource = scopes, DisplayMemberPath = "Name", SelectedItem = editor.SelectedNode?.Group ?? editor.SelectedNode?.Parent?.Group ?? editor.Project.Definition.Root };
        var name = new TextBox();
        var type = new ComboBox { ItemsSource = Enum.GetValues<VarType>(), SelectedItem = VarType.Double };
        var initial = new TextBox();
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Margin = new Thickness(18) };
        void Field(string label, UIElement control) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) }); panel.Children.Add(control); }
        Field("Gruppe / Gültigkeitsbereich", group); Field("Name", name); Field("Typ", type); Field("Anfangswert (leer = Standardwert)", initial);
        panel.Children.Add(error);
        var button = new Button { Content = "Variable anlegen", Margin = new Thickness(0, 14, 0, 0), IsDefault = true };
        button.SetResourceReference(StyleProperty, "PrimaryButton");
        panel.Children.Add(button);
        var window = new Window { Title = "Variable anlegen", Content = panel, Width = 440, SizeToContent = SizeToContent.Height,
            FontSize = 14, ResizeMode = ResizeMode.NoResize, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        button.Click += (_, _) =>
        {
            if (group.SelectedItem is not GroupDefinition selected || type.SelectedItem is not VarType selectedType) return;
            if (!ReferenceEquals(project, editor.Project)) { error.Text = "Das Projekt wurde zwischenzeitlich gewechselt. Dialog erneut öffnen."; return; }
            var problem = editor.DeclareVisualVariable(selected.Id, name.Text, selectedType, initial.Text);
            if (problem is null) window.DialogResult = true; else error.Text = problem;
        };
        window.ShowDialog();
    }

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs eventArgs)
    { _paletteStart = eventArgs.GetPosition(this); _paletteDrag = (sender as FrameworkElement)?.DataContext as PaletteItem; }
    private void OnPaletteMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (!VisualDragGesture.CanStart(eventArgs.LeftButton, _paletteDrag, _paletteStart, eventArgs.GetPosition(this))) return;
        var item = _paletteDrag; _paletteDrag = null;
        DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Copy);
    }

    private bool TryGetPaletteTarget(DragEventArgs eventArgs, out PaletteItem item, out NodeViewModel parent, out int index)
    {
        item = null!; parent = null!; index = 0;
        if (eventArgs.Data.GetData(typeof(PaletteItem)) is not PaletteItem palette || !ResolveDropPosition(eventArgs, out parent, out index)) return false;
        if (Editor!.Palette.SelectMany(paletteCategory => paletteCategory.Items).All(paletteItem => paletteItem != palette)) return false;
        item = palette; return Editor.CanInsertAt(parent, index);
    }

    private bool ResolveDropPosition(DragEventArgs eventArgs, out NodeViewModel parent, out int index)
    {
        parent = null!; index = 0;
        var card = CardFrom(eventArgs.OriginalSource as DependencyObject);
        if (Editor is null || card?.DataContext is not NodeViewModel target) return false;
        var verticalFraction = eventArgs.GetPosition(card).Y / Math.Max(1, card.ActualHeight);
        var into = card.Name == "EmptySlot" || target.IsSection || target.IsExit || target.IsGroup && verticalFraction is > 0.25 and < 0.75;
        if (into) { parent = target; index = target.Group!.Children.Count; return true; }
        if (target.Parent is null) return false;
        parent = target.Parent; index = parent.Group!.Children.IndexOf(target.Definition);
        if (index < 0) return false;
        if (verticalFraction >= 0.5) index++; return true;
    }
}
