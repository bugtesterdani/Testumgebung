using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Testumgebung.Engine.Blocks;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCompactPalette(EditorView view, EditorViewModel editor, Window host, Dispatcher dispatcher)
    {
        var panel = (FrameworkElement)view.FindName("PalettePanel");
        var categories = Descendants(panel).OfType<Expander>().ToList();
        Assert.DoesNotContain(categories, category => category.Header?.ToString() == "Steuerung");
        var communication = categories.Single(category => category.Header?.ToString() == "Kommunikation");
        Assert.False(communication.IsExpanded);
        communication.IsExpanded = true; Sta.Flush(dispatcher);
        Assert.True(communication.IsExpanded);
        communication.IsExpanded = false;
        CaptureVisual(host, "editor-palette-compact.png");
        var search = Descendants(panel).OfType<TextBox>().Single(box => AutomationProperties.GetName(box) == "Bausteine suchen");
        search.Text = "Modbus"; Sta.Flush(dispatcher); view.UpdateLayout();
        Assert.Equal("Modbus", editor.PaletteSearch);
        Assert.Single(editor.Palette);
        Assert.All(Descendants(panel).OfType<Expander>(), category => Assert.True(category.IsExpanded));
        CaptureVisual(host, "editor-palette-search.png");
        search.Text = "not-a-real-block"; Sta.Flush(dispatcher);
        Assert.Empty(editor.Palette);
        Assert.Equal(Visibility.Visible, Descendants(panel).OfType<TextBlock>().Single(text => text.Text == "Keine passenden Bausteine gefunden.").Visibility);
        search.Text = ""; Sta.Flush(dispatcher);
        Assert.NotEmpty(editor.Palette);
    }

    private static void VerifyEvaluationOptions(EditorView view, EditorViewModel editor, Dispatcher dispatcher)
    {
        editor.VisualBlocks = false; editor.GraphView = false; Sta.Flush(dispatcher);
        var item = editor.Palette.SelectMany(category => category.Items).Single(item => item.BlockType == BlockCatalog.Evaluate);
        var node = editor.InsertAt(item, editor.FindNode(editor.Project.Definition.Loop.Id)!, 0);
        Sta.Flush(dispatcher); view.UpdateLayout();
        var properties = (StepPropertiesViewModel)editor.Properties!;
        var panel = (FrameworkElement)view.FindName("PropertiesPanel");
        var mode = Descendants(panel).OfType<ComboBox>().Single(box => box.Items.Contains("Festes Ergebnis"));
        mode.SelectedItem = "Festes Ergebnis"; Sta.Flush(dispatcher);
        Assert.Equal("Festes Ergebnis", node.Step!.Parameters["Art"]);
        var rows = Descendants(panel).OfType<Grid>().Where(grid => grid.DataContext is ParameterRowViewModel).ToList();
        Assert.Contains(rows, grid => ((ParameterRowViewModel)grid.DataContext).Descriptor.Name == "Bedingung" && grid.Visibility == Visibility.Collapsed);
        Assert.Contains(rows, grid => ((ParameterRowViewModel)grid.DataContext).Descriptor.Name == "Bewertung" && grid.Visibility == Visibility.Visible);
        editor.VisualBlocks = true; Sta.Flush(dispatcher); view.UpdateLayout();
        Assert.Contains(Descendants(view).OfType<ComboBox>(), box => box.IsVisible && box.Items.Contains("Festes Ergebnis"));
        editor.VisualBlocks = false; editor.GraphView = true; Sta.Flush(dispatcher); view.UpdateLayout();
        Assert.Contains(Descendants(view).OfType<ComboBox>(), box => box.IsVisible && box.Items.Contains("Festes Ergebnis"));
        editor.GraphView = false;
        while (editor.UndoCommand.CanExecute(null)) editor.UndoCommand.Execute(null);
        editor.Load(editor.Project); // Restore the clean fixture for the existing visual-editor checks.
        Sta.Flush(dispatcher);
    }
}
