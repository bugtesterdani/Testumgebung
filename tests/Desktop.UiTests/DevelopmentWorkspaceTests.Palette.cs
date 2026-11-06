using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
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
}
