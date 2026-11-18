using System.Windows.Controls;
using System.Windows.Threading;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyOperatorLanguage(MainWindow window, Dispatcher dispatcher)
    {
        var language = Assert.IsType<ComboBox>(window.FindName("OperatorLanguage"));
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Sprache Werkeransicht");
        Assert.Contains("Prüfprogramms", Assert.IsType<string>(language.ToolTip));
        var selector = window.Shell.SelectedStation.StartCommand;
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Werkerabbruch");
        language.SelectedIndex = 1;
        Sta.Flush(dispatcher);
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Operator abort");
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Sequence overview");
        Assert.Same(selector, window.Shell.SelectedStation.StartCommand);
        language.SelectedIndex = 0;
        Sta.Flush(dispatcher);
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Werkerabbruch");
        Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Ablaufübersicht");
    }
}
