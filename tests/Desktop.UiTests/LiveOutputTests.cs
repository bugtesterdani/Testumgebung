using System.Windows;
using System.Windows.Controls;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;
namespace Testumgebung.Desktop.UiTests;
public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyLiveOutputPanel(System.Windows.Threading.Dispatcher dispatcher)
    {
        var outputs=new LiveOutputCollection();
        var panel=new LiveOutputPanel { DataContext=new { LiveOutputs=outputs }, Width=500,Height=300 };
        outputs.Apply("flash","Programmierung",new("Zeile 1\n",37,"Schreiben"));
        panel.Measure(new Size(500,300));panel.Arrange(new Rect(0,0,500,300));panel.UpdateLayout();Sta.Flush(dispatcher);
        var bar=Descendants(panel).OfType<ProgressBar>().Single();Assert.Equal(37,bar.Value);Assert.False(bar.IsIndeterminate);
        var box=Descendants(panel).OfType<TextBox>().Single();Assert.True(box.IsReadOnly);Assert.Equal("Zeile 1\n",box.Text);
        outputs.Apply("flash","Programmierung",new("Zeile 2\n",80,"Verifizieren"));Sta.Flush(dispatcher);
        Assert.Contains("Zeile 2",box.Text);Assert.Equal(80,bar.Value);
        Assert.Contains(Descendants(panel).OfType<TextBlock>(),t=>t.Text=="Verifizieren");
        outputs.Clear();Sta.Flush(dispatcher);Assert.Empty(Descendants(panel).OfType<TextBox>());
    }
}
