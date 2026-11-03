using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Testumgebung.Contracts;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyVisualBlockProgramming(Dispatcher dispatcher)
    {
        // Ein Klick darf keinen Ziehvorgang starten; beide Editoren verwenden
        // dieselbe Windows-Schwelle, auch für negative Koordinaten und Diagonalen.
        var origin = new Point(100, 100);
        var horizontal = new Point(100 + SystemParameters.MinimumHorizontalDragDistance, 100);
        var vertical = new Point(100, 100 - SystemParameters.MinimumVerticalDragDistance);
        Assert.False(VisualDragGesture.CanStart(MouseButtonState.Released, new object(), origin, horizontal));
        Assert.False(VisualDragGesture.CanStart(MouseButtonState.Pressed, null, origin, horizontal));
        Assert.False(VisualDragGesture.CanStart(MouseButtonState.Pressed, new object(), origin, origin));
        Assert.False(VisualDragGesture.CanStart(MouseButtonState.Pressed, new object(), origin,
            new Point(horizontal.X - .5, vertical.Y + .5)));
        Assert.True(VisualDragGesture.CanStart(MouseButtonState.Pressed, new object(), origin, horizontal));
        Assert.True(VisualDragGesture.CanStart(MouseButtonState.Pressed, new object(), origin, vertical));
        var loop = new GroupDefinition("visual-loop", "Sichtprüfung") { EntryCondition = "true" };
        var calc = BlockCatalog.Default.Create(BlockCatalog.SetValue, "calc", "Spannung berechnen",
            new Dictionary<string, string> { ["Ziel"] = "X", ["Ausdruck"] = "2.0" });
        loop.Children.Add(calc);
        var definition = new TestDefinition("Blockprogramm", "1", new("visual-start", "Start"), loop, new("visual-end", "Ende"));
        definition.Root.Variables.Add(new("X", VarType.Double, 2d));
        definition.Root.Variables.Add(new("Bereit", VarType.Bool, true));
        definition.Root.Variables.Add(new("Notiz", VarType.String, "Text"));
        var library = new CodeBlockDefinition("visual-lib", "Eigener Baustein");
        library.Ports.AddRange([new("Input", VarType.Double, PortDirection.Input), new("Output", VarType.Bool, PortDirection.Output), new("InOut", VarType.String, PortDirection.InOut)]);
        definition.Library.Add(library);
        var call = BlockCatalog.Default.Create(BlockCatalog.LibraryBlock, "visual-call", parameters: new Dictionary<string, string> { ["Baustein"] = library.Id });
        loop.Children.Add(call);
        loop.Children.Add(new GroupDefinition("inline-repeat", "Mehrfach prüfen") { Repetition = Repetition.Times(3) });
        foreach (var kind in new[] { ParameterKind.Bool, ParameterKind.Choice, ParameterKind.Code })
        {
            var descriptor = BlockCatalog.Default.All.First(d => d.Parameters.Any(p => p.Kind == kind));
            loop.Children.Add(BlockCatalog.Default.Create(descriptor.Type, "inline-" + kind));
        }
        var shell = new ShellViewModel(project: new TestProject(definition)); var editor = shell.Editor;
        var view = new EditorView { DataContext = shell, Background = Brushes.White };
        var host = new Window { Content = view, Width = 1280, Height = 900, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        host.Show();
        try
        {
            Sta.Flush(dispatcher); view.UpdateLayout();
            var serializer = new ProjectSerializer(); var beforeMode = serializer.Serialize(editor.Project);
            var mode = Descendants(view).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Blockansicht");
            mode.IsChecked = true; Sta.Flush(dispatcher); view.UpdateLayout();
            var tree = Descendants(view).OfType<TreeView>().Single();
            Assert.Same(view.FindResource("VisualBlockTemplate"), tree.ItemTemplate);
            Assert.Equal(beforeMode, serializer.Serialize(editor.Project)); Assert.False(editor.IsDirty);
            mode.IsChecked = false; Sta.Flush(dispatcher); Assert.Same(view.FindResource("FlowNodeTemplate"), tree.ItemTemplate);
            mode.IsChecked = true; Sta.Flush(dispatcher);
            VerifyPaletteDrop(view, editor);
            mode.IsChecked = false; Sta.Flush(dispatcher);

            editor.SelectedNode = editor.FindNode("visual-loop"); view.UpdateLayout(); Sta.Flush(dispatcher);
            var entry = Descendants(view).OfType<Button>().Single(b => b.Tag?.ToString() == "entry");
            InteractExpression(dispatcher, () => entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            {
                var model = (ExpressionBuilderViewModel)window.DataContext;
                ClickExpressionTile(window, "&&", VisualExpressionKind.Binary); ClickExpressionTile(window, ">=", VisualExpressionKind.Binary);
                ClickExpressionTile(window, "", VisualExpressionKind.Variable);
                Descendants(window).OfType<ComboBox>().Single(c => ReferenceEquals(c.DataContext, model.Root.Left!.Left)).SelectedItem = model.Variables.Single(v => v.Path == "X");
                SelectExpressionNode(window, model.Root.Left!.Right!); ClickExpressionTile(window, "0", VisualExpressionKind.Number);
                Descendants(window).OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, model.Root.Left!.Right) && System.Windows.Automation.AutomationProperties.GetName(t) == "Zahlenblock").Text = "10.0";
                SelectExpressionNode(window, model.Root.Right!); ClickExpressionTile(window, "", VisualExpressionKind.Variable);
                model.Root.Right!.VariableChoice = model.Variables.Single(v => v.Path == "Bereit");
                window.UpdateLayout(); Sta.Flush(dispatcher);
                Assert.True(model.CanApply, model.Error);
                Assert.Contains(Descendants(window).OfType<ComboBox>(), c => c.SelectedItem is ExpressionVariable { Path: "X" });
                VerifyExpressionDrops(window);
                CaptureVisual(window, "visual-expression.png");
                Click(window, "Übernehmen");
            });
            Assert.Contains("X >= 10.0", editor.Project.Definition.Loop.EntryCondition);
            Assert.Contains("&& Bereit", editor.Project.Definition.Loop.EntryCondition);
            var saved = serializer.Serialize(editor.Project);
            InteractExpression(dispatcher, () => entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            { var model = (ExpressionBuilderViewModel)window.DataContext; model.Root.Left!.Right!.Value = "99"; Click(window, "Abbrechen"); });
            Assert.Equal(saved, serializer.Serialize(editor.Project));

            var groupProps = (GroupPropertiesViewModel)editor.Properties!;
            groupProps.RepeatMode = RepeatMode.Until; groupProps.RepeatExpression = "false";
            view.UpdateLayout(); Sta.Flush(dispatcher);
            var repeat = Descendants(view).OfType<Button>().Single(b => b.Tag?.ToString() == "repeat");
            InteractExpression(dispatcher, () => repeat.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            { var model = (ExpressionBuilderViewModel)window.DataContext; model.Root.BooleanValue = true; Click(window, "Übernehmen"); });
            Assert.Equal("true", editor.Project.Definition.Loop.Repetition.Expression);
            InteractExpression(dispatcher, () => entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window => Click(window, "Leer / Standard"));
            Assert.Null(editor.Project.Definition.Loop.EntryCondition);

            editor.SelectedNode = editor.FindNode("calc"); view.UpdateLayout(); Sta.Flush(dispatcher);
            Button Parameter(string name) => Descendants(view).OfType<Button>().Single(b => b.Content?.ToString() == "Blöcke …" && b.DataContext is ParameterRowViewModel p && p.Descriptor.Name == name);
            InteractExpression(dispatcher, () => Parameter("Ziel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            {
                var model = (ExpressionBuilderViewModel)window.DataContext;
                Assert.True(model.VariablesOnly); Assert.DoesNotContain(model.Variables, v => v.Path.EndsWith("GruppenErgebnis"));
                model.Root.VariableChoice = model.Variables.Single(v => v.Path == "X"); Click(window, "Übernehmen");
            });
            var oldExpression = editor.FindNode("calc")!.Step!.Parameters["Ausdruck"];
            InteractExpression(dispatcher, () => Parameter("Ausdruck").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            {
                var model = (ExpressionBuilderViewModel)window.DataContext;
                Assert.Equal("2.0", model.Root.Value); // Ausgeblendete Auswahlfelder dürfen Zahlen nicht überschreiben.
                model.Root.Value = "4,5"; Assert.True(model.CanApply, model.Error); Click(window, "Übernehmen");
            });
            Assert.Equal("4.5", editor.FindNode("calc")!.Step!.Parameters["Ausdruck"]);
            editor.UndoCommand.Execute(null); Assert.Equal(oldExpression, editor.FindNode("calc")!.Step!.Parameters["Ausdruck"]);
            editor.RedoCommand.Execute(null);
            view.UpdateLayout(); Sta.Flush(dispatcher);
            InteractExpression(dispatcher, () => Parameter("Ausdruck").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            {
                var model = (ExpressionBuilderViewModel)window.DataContext; model.Root.Value = "5.0";
                ((StepPropertiesViewModel)editor.Properties!).Parameters.Single(p => p.Descriptor.Name == "Ausdruck").Value = "6.0";
                Click(window, "Übernehmen"); Assert.True(window.IsVisible);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text.Contains("zwischenzeitlich")); Click(window, "Abbrechen");
            });
            Assert.Equal("6.0", editor.FindNode("calc")!.Step!.Parameters["Ausdruck"]);
            var expressionRow = ((StepPropertiesViewModel)editor.Properties!).Parameters.Single(p => p.Descriptor.Name == "Ausdruck");
            var input = Descendants(view).OfType<TextBox>().Single(t => t.Name == "TextEditor" && ReferenceEquals(t.DataContext, expressionRow));
            input.Text = "(X + 1.0)";
            InteractExpression(dispatcher, () => Parameter("Ausdruck").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
            {
                Assert.Equal("(X + 1.0)", ((ExpressionBuilderViewModel)window.DataContext).Original);
                Click(window, "Abbrechen");
            });
            Assert.Equal("(X + 1.0)", expressionRow.Value);
            editor.SelectedNode = editor.FindNode("visual-call"); view.UpdateLayout(); Sta.Flush(dispatcher);
            foreach (var name in new[] { "Input", "Output", "InOut" })
            {
                var binding = Descendants(view).OfType<Button>().Single(b => b.Content?.ToString() == "Blöcke / Variable …" && b.DataContext is PortBindingViewModel p && p.Name == name);
                InteractExpression(dispatcher, () => binding.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), window =>
                {
                    var model = (ExpressionBuilderViewModel)window.DataContext;
                    var tile = model.Palette.SelectMany(g => g.Items).First(t => t.Kind == (name == "Input" ? VisualExpressionKind.Number : VisualExpressionKind.Variable));
                    model.Insert(tile); if (name == "Input") model.Root.Value = "7.5";
                    Assert.True(model.CanApply, model.Error); Click(window, "Übernehmen");
                });
            }
            Assert.Equal("7.5", editor.FindNode("visual-call")!.Step!.Parameters[BlockCatalog.InputBindingPrefix + "Input"]);
            Assert.Equal("Bereit", editor.FindNode("visual-call")!.Step!.Parameters[BlockCatalog.OutputBindingPrefix + "Output"]);
            Assert.Equal("Notiz", editor.FindNode("visual-call")!.Step!.Parameters[BlockCatalog.InputBindingPrefix + "InOut"]);
            VerifyVariableDeclarationWindow(dispatcher, view, editor);
            editor.SelectedNode = editor.FindNode("visual-loop"); view.UpdateLayout(); Sta.Flush(dispatcher);
            mode.IsChecked = true; Sta.Flush(dispatcher); view.UpdateLayout();
            VerifyInlineBlocks(dispatcher, view, editor);
            view.UpdateLayout(); Sta.Flush(dispatcher);
            CaptureVisual(host, "visual-flow.png");
        }
        finally { host.Close(); }
    }
    private static void VerifyInlineBlocks(Dispatcher dispatcher, EditorView view, EditorViewModel editor)
    {
        Assert.False(((FrameworkElement)view.FindName("PalettePanel")).IsVisible);
        Assert.False(((FrameworkElement)view.FindName("PropertiesPanel")).IsVisible);
        var node = editor.FindNode("calc")!;
        var block = Descendants(view).OfType<InlineBlockEditor>().Single(b => ReferenceEquals(b.DataContext, node));
        var props = Assert.IsType<StepPropertiesViewModel>(node.InlineProperties);
        editor.SelectedNode = node; Assert.Same(props, editor.Properties);
        var row = props.Parameters.Single(p => p.Descriptor.Name == "Ausdruck");
        var text = Descendants(block).OfType<TextBox>().Single(t => ReferenceEquals(t.DataContext, row));
        text.Text = "8.0"; text.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Assert.Equal("8.0", node.Step!.Parameters["Ausdruck"]);
        editor.Project.Definition.Root.Variables.Add(new("NeuesZiel", VarType.Double, 0d));
        var target = props.Parameters.Single(p => p.IsVariable);
        var targets = Descendants(block).OfType<ComboBox>().Single(c => ReferenceEquals(c.DataContext, target));
        var oldTarget = target.Value; targets.IsDropDownOpen = true; Sta.Flush(dispatcher);
        Assert.Contains("NeuesZiel", targets.Items.Cast<string>()); Assert.Equal(oldTarget, target.Value);
        targets.IsDropDownOpen = false;
        // Die Karte muss auch dann ihr eigenes Ziel bearbeiten, wenn eine andere ausgewählt ist.
        editor.SelectedNode = editor.FindNode("visual-loop");
        var slot = Descendants(block).OfType<Expander>().Single(e => ReferenceEquals(e.DataContext, row));
        slot.IsExpanded = true; Sta.Flush(dispatcher); view.UpdateLayout();
        var inline = Assert.IsType<InlineExpressionEditor>(slot.Content);
        var model = Assert.IsType<ExpressionBuilderViewModel>(inline.DataContext);
        Assert.Equal("8.0", model.Original);
        var tile = Descendants(inline).OfType<Button>().Single(b => b.DataContext is ExpressionTile { Kind: VisualExpressionKind.Number } && b.Content?.ToString() == "Zahl");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tile, []);
        model.Root.Value = "12.5";
        Assert.Equal("8.0", row.Value);
        Click(inline, "Abbrechen"); Assert.Null(slot.Content); Assert.Equal("8.0", row.Value);
        slot.IsExpanded = true; Sta.Flush(dispatcher); view.UpdateLayout();
        inline = Assert.IsType<InlineExpressionEditor>(slot.Content); model = (ExpressionBuilderViewModel)inline.DataContext;
        model.Root.Value = "12.5"; Assert.True(model.CanApply, model.Error); Click(inline, "Übernehmen");
        Assert.Equal("12.5", node.Step.Parameters["Ausdruck"]);
        Assert.Equal("visual-loop", editor.SelectedNode!.Id);
        slot.IsExpanded = true; Sta.Flush(dispatcher); view.UpdateLayout();
        inline = Assert.IsType<InlineExpressionEditor>(slot.Content);
        var destination = props.Parameters.Single(p => p.IsVariable);
        destination.Value = "Bereit"; // geänderter Zieltyp während eines offenen Double-Entwurfs
        ((ExpressionBuilderViewModel)inline.DataContext).Root.Value = "99.0";
        Click(inline, "Übernehmen");
        Assert.Same(inline, slot.Content); Assert.Equal("12.5", row.Value);
        Assert.Contains(Descendants(inline).OfType<TextBlock>(), t => t.Text.Contains("zwischenzeitlich"));
        destination.Value = "X"; Click(inline, "Abbrechen");
        // Entf in einem Eingabefeld darf den Ablaufpunkt nicht löschen.
        var tree = (TreeView)view.FindName("FlowTree");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent, Source = text };
        typeof(EditorView).GetMethod("OnTreeKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [tree, args]);
        Assert.NotNull(editor.FindNode("visual-loop")); Assert.False(args.Handled);
        var loop = editor.FindNode("inline-repeat")!;
        var groupBlock = Descendants(view).OfType<InlineBlockEditor>().Single(b => ReferenceEquals(b.DataContext, loop));
        var check = Descendants(groupBlock).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Gleichzeitig ausführen");
        check.IsChecked = true; check.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();
        Assert.True(loop.Group!.Parallel);
        check.IsChecked = false; check.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();
        VerifyInlineInputKinds(dispatcher, view, editor);
        VerifyInlineCondition(dispatcher, view, editor);
        VerifyInlineMenus(dispatcher, view, editor);
        var original = new ProjectSerializer().Serialize(editor.Project);
        editor.VisualBlocks = false; Sta.Flush(dispatcher); editor.VisualBlocks = true; Sta.Flush(dispatcher);
        Assert.Equal(original, new ProjectSerializer().Serialize(editor.Project));
    }

    private static void VerifyInlineInputKinds(Dispatcher dispatcher, EditorView view, EditorViewModel editor)
    {
        foreach (var kind in new[] { ParameterKind.Bool, ParameterKind.Choice, ParameterKind.Code })
        {
            var node = editor.FindNode("inline-" + kind)!;
            var block = Descendants(view).OfType<InlineBlockEditor>().Single(b => ReferenceEquals(b.DataContext, node));
            var props = (StepPropertiesViewModel)node.InlineProperties!;
            var row = props.Parameters.First(p => p.Kind == kind);
            if (kind == ParameterKind.Bool)
            {
                var check = Descendants(block).OfType<CheckBox>().Single(c => ReferenceEquals(c.DataContext, row));
                check.IsChecked = !row.BoolValue; Assert.Equal(check.IsChecked, row.BoolValue);
            }
            else if (kind == ParameterKind.Choice)
            {
                var choice = Descendants(block).OfType<ComboBox>().Single(c => ReferenceEquals(c.DataContext, row));
                choice.SelectedItem = row.Choices.Last(); Assert.Equal(row.Choices.Last(), row.Value);
            }
            else
            {
                var code = Descendants(block).OfType<Testumgebung.Desktop.Controls.NumberedCodeEditor>().Single(c => ReferenceEquals(c.DataContext, row));
                code.SetCurrentValue(Testumgebung.Desktop.Controls.NumberedCodeEditor.TextProperty, "return Pass();");
                code.GetBindingExpression(Testumgebung.Desktop.Controls.NumberedCodeEditor.TextProperty)!.UpdateSource();
                Assert.Equal("return Pass();", row.Value);
            }
        }
        var library = Descendants(view).OfType<InlineBlockEditor>().Single(b => b.DataContext is NodeViewModel { Id: "visual-call" });
        var port = Descendants(library).OfType<Expander>().First(e => e.DataContext is PortBindingViewModel { Name: "Input" });
        port.IsExpanded = true; Sta.Flush(dispatcher); view.UpdateLayout();
        var composer = Assert.IsType<InlineExpressionEditor>(port.Content);
        ((ExpressionBuilderViewModel)composer.DataContext).Root.Value = "9.5";
        Click(composer, "Übernehmen");
        Assert.Equal("9.5", editor.FindNode("visual-call")!.Step!.Parameters[BlockCatalog.InputBindingPrefix + "Input"]);
    }

    private static void VerifyInlineCondition(Dispatcher dispatcher, EditorView view, EditorViewModel editor)
    {
        var wrapper = editor.AllNodes().First(n => n.Children.FirstOrDefault()?.IsTemplateDecision == true);
        var block = Descendants(view).OfType<InlineBlockEditor>().Single(b => ReferenceEquals(b.DataContext, wrapper));
        var slot = Descendants(block).OfType<Expander>().Single(e => e.DataContext is ParameterRowViewModel);
        slot.IsExpanded = true; Sta.Flush(dispatcher); view.UpdateLayout();
        var composer = Assert.IsType<InlineExpressionEditor>(slot.Content);
        var model = (ExpressionBuilderViewModel)composer.DataContext;
        Assert.Equal(VarType.Bool, model.ExpectedType);
        ExpressionTile Tile(string value, VisualExpressionKind kind) => model.Palette.SelectMany(g => g.Items).Single(t => t.Value == value && t.Kind == kind);
        model.Insert(Tile("&&", VisualExpressionKind.Binary));
        model.Insert(Tile(">=", VisualExpressionKind.Binary));
        model.Insert(Tile("", VisualExpressionKind.Variable)); model.Selected.VariableChoice = model.Variables.Single(v => v.Path == "X");
        model.Selected = model.Root.Left!.Right!; model.Insert(Tile("0", VisualExpressionKind.Number)); model.Selected.Value = "10.0";
        model.Selected = model.Root.Right!; model.Insert(Tile("", VisualExpressionKind.Variable)); model.Selected.VariableChoice = model.Variables.Single(v => v.Path == "Bereit");
        Assert.True(model.CanApply, model.Error);
        VerifyExpressionDrops(composer);
        view.UpdateLayout(); Sta.Flush(dispatcher); slot.BringIntoView(); Sta.Flush(dispatcher);
        CaptureVisual(Window.GetWindow(view), "visual-inline-expression.png");
        Click(composer, "Abbrechen");
        Assert.Equal("true", ((ParameterRowViewModel)slot.DataContext).Value);
    }
    private static void VerifyInlineMenus(Dispatcher dispatcher, EditorView view, EditorViewModel editor)
    {
        InlineBlockEditor Card(string id) => Descendants(view).OfType<InlineBlockEditor>().Single(b => b.DataContext is NodeViewModel n && n.Id == id);
        void Add(string id)
        {
            var block = Card(id); Click(block, "+ Schritt");
            var menu = Assert.IsType<ContextMenu>(block.ContextMenu);
            var entry = menu.Items.Cast<MenuItem>().SelectMany(c => c.Items.Cast<MenuItem>()).First(i => i.Tag is PaletteItem p && p.BlockType == BlockCatalog.Wait);
            typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(entry, []);
            menu.IsOpen = false; Sta.Flush(dispatcher); view.UpdateLayout();
        }
        var count = editor.Project.Definition.Loop.Children.Count;
        var index = editor.Project.Definition.Loop.Children.FindIndex(n => n.Id == "calc");
        editor.SelectedNode = editor.FindNode("visual-start");
        Add("calc");
        Assert.Equal(BlockCatalog.Wait, ((StepDefinition)editor.Project.Definition.Loop.Children[index + 1]).BlockType);
        Assert.Equal(count + 1, editor.Project.Definition.Loop.Children.Count);
        editor.UndoCommand.Execute(null); Sta.Flush(dispatcher); view.UpdateLayout();
        Add("inline-repeat");
        Assert.Single(((GroupDefinition)editor.Project.Definition.Find("inline-repeat")!).Children);
        editor.UndoCommand.Execute(null); Sta.Flush(dispatcher); view.UpdateLayout();
        var card = Card("calc"); Click(card, "⋯");
        var actions = Assert.IsType<ContextMenu>(card.ContextMenu);
        var duplicate = actions.Items.Cast<MenuItem>().Single(i => i.Header?.ToString() == "Duplizieren");
        typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(duplicate, []);
        actions.IsOpen = false; Sta.Flush(dispatcher); Assert.Equal(count + 1, editor.Project.Definition.Loop.Children.Count);
        editor.UndoCommand.Execute(null); Sta.Flush(dispatcher); view.UpdateLayout();
        Assert.Equal(count, editor.Project.Definition.Loop.Children.Count);
    }

    private static void ClickExpressionTile(ExpressionBuilderWindow window, string value, VisualExpressionKind kind)
    {
        var model = (ExpressionBuilderViewModel)window.DataContext;
        var button = Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.Command, model.InsertCommand) && b.CommandParameter is ExpressionTile t && t.Kind == kind && t.Value == value);
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, []);
        window.UpdateLayout(); Sta.Flush(window.Dispatcher);
    }
    private static void SelectExpressionNode(ExpressionBuilderWindow window, VisualExpressionNode node)
    {
        var button = Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.DataContext, node) && b.Content?.ToString() == node.Label);
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, []);
    }
    private static void VerifyExpressionDrops(FrameworkElement window)
    {
        var model = (ExpressionBuilderViewModel)window.DataContext;
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        void Drop(object payload, VisualExpressionNode target, bool copy, bool perform, DragDropEffects expected)
        {
            var border = new Border { DataContext = target, Width = 100, Height = 100 };
            var data = new DataObject(); data.SetData(payload.GetType(), payload);
            var args = (DragEventArgs)ctor.Invoke([data, copy ? DragDropKeyStates.ControlKey : DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, border, new Point()]);
            args.RoutedEvent = DragDrop.DragOverEvent; args.Source = border;
            window.GetType().GetMethod("OnBlockDragOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [border, args]);
            Assert.Equal(expected, args.Effects);
            if (perform) window.GetType().GetMethod("OnBlockDrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [border, args]);
        }
        var before = model.Source;
        Drop(model.Root, model.Root.Left!, false, false, DragDropEffects.None);
        Drop(new ExpressionBuilderViewModel("true", []).Root, model.Root.Right!, false, false, DragDropEffects.None);
        var tile = model.Palette.SelectMany(g => g.Items).Single(t => t.Kind == VisualExpressionKind.Number);
        Drop(tile, model.Root.Left!.Right!, false, true, DragDropEffects.Copy);
        Assert.Contains(">= 0", model.Source); Click(window, "Rückgängig"); Assert.Equal(before, model.Source);
        Drop(model.Root.Right!, model.Root.Left!, true, true, DragDropEffects.Copy);
        Assert.Equal("(Bereit && Bereit)", model.Source); Click(window, "Rückgängig"); Assert.Equal(before, model.Source);
        Drop(model.Root.Right!, model.Root.Left!, false, true, DragDropEffects.Move);
        Assert.False(model.CanApply); Click(window, "Rückgängig"); Assert.Equal(before, model.Source);
        var mouse = new MouseEventArgs(Mouse.PrimaryDevice, 0);
        window.GetType().GetMethod("OnNodeMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, mouse]);
        window.GetType().GetMethod("OnTileMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, mouse]);
    }

    private static void InteractExpression(Dispatcher dispatcher, Action open, Action<ExpressionBuilderWindow> interact)
    {
        Exception? failure = null;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            ExpressionBuilderWindow? window = null;
            try { window = Application.Current.Windows.OfType<ExpressionBuilderWindow>().Single(); window.UpdateLayout(); Sta.Flush(dispatcher); interact(window); }
            catch (Exception ex) { failure = ex; window?.Close(); }
        }));
        open();
        if (failure is not null) throw new InvalidOperationException("Ausdrucksdialog fehlgeschlagen.", failure);
    }
    private static void Click(DependencyObject root, string text)
        => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(
            Descendants(root).OfType<Button>().Single(b => b.Content?.ToString() == text), []);
    private static void CaptureVisual(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var path = Path.Combine(root.FullName, "artifacts", "verification", name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void VerifyPaletteDrop(EditorView view, EditorViewModel editor)
    {
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var card = new Border { Name = "EmptySlot", DataContext = editor.FindNode("visual-start"), Width = 100, Height = 100 };
        card.Measure(new(100, 100)); card.Arrange(new Rect(0, 0, 100, 100));
        var data = new DataObject(); data.SetData(typeof(PaletteItem), EditorViewModel.ControlBlocks.Single(p => p.Preset == "ifelse"));
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, card, new Point(1, 1)]);
        args.RoutedEvent = DragDrop.DragOverEvent; args.Source = card;
        typeof(EditorView).GetMethod("OnTreeDragOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, args]);
        Assert.Equal(DragDropEffects.Copy, args.Effects);
        typeof(EditorView).GetMethod("OnTreeDrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, args]);
        Assert.Single(editor.Project.Definition.Init.Children);
        Assert.Equal(3, ((GroupDefinition)editor.Project.Definition.Init.Children[0]).Children.Count);
        editor.UndoCommand.Execute(null); Assert.Empty(editor.Project.Definition.Init.Children);
        editor.RedoCommand.Execute(null); Assert.Single(editor.Project.Definition.Init.Children);
    }
    private static void VerifyVariableDeclarationWindow(Dispatcher dispatcher, EditorView view, EditorViewModel editor)
    {
        Exception? failure = null;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Title == "Variable anlegen");
            try
            {
                var fields = Descendants(dialog).OfType<TextBox>().ToList(); fields[0].Text = "GruppenErgebnis";
                Click(dialog, "Variable anlegen"); Assert.True(dialog.IsVisible);
                fields[0].Text = "Grenze"; fields[1].Text = "10"; Click(dialog, "Variable anlegen");
            }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        }));
        Click(view, "Variable anlegen …");
        if (failure is not null) throw new InvalidOperationException("Variablendialog fehlgeschlagen.", failure);
        Assert.Contains(editor.Project.Definition.Loop.Variables, v => v.Name == "Grenze" && v.Type == VarType.Double);
    }
}