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
        var shell = new ShellViewModel(project: new TestProject(definition)); var editor = shell.Editor;
        var view = new EditorView { DataContext = shell };
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
            CaptureVisual(host, "visual-flow.png");
        }
        finally { host.Close(); }
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
    private static void VerifyExpressionDrops(ExpressionBuilderWindow window)
    {
        var model = (ExpressionBuilderViewModel)window.DataContext;
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        void Drop(object payload, VisualExpressionNode target, bool copy, bool perform, DragDropEffects expected)
        {
            var border = new Border { DataContext = target, Width = 100, Height = 100 };
            var data = new DataObject(); data.SetData(payload.GetType(), payload);
            var args = (DragEventArgs)ctor.Invoke([data, copy ? DragDropKeyStates.ControlKey : DragDropKeyStates.None, DragDropEffects.Copy | DragDropEffects.Move, border, new Point()]);
            args.RoutedEvent = DragDrop.DragOverEvent; args.Source = border;
            typeof(ExpressionBuilderWindow).GetMethod("OnBlockDragOver", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [border, args]);
            Assert.Equal(expected, args.Effects);
            if (perform) typeof(ExpressionBuilderWindow).GetMethod("OnBlockDrop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [border, args]);
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
        typeof(ExpressionBuilderWindow).GetMethod("OnNodeMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, mouse]);
        typeof(ExpressionBuilderWindow).GetMethod("OnTileMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, mouse]);
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
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
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