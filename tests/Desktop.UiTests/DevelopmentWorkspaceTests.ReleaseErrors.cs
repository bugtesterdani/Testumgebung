using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyReleaseErrorLayout(Dispatcher dispatcher)
    {
        var view = new ReleasesView { DataContext = new ShellViewModel() };
        var panel = (StackPanel)view.FindName("ReleaseErrorPanel");
        var expander = (Expander)view.FindName("ReleaseErrorDetails");
        var message = string.Join("\n", Enumerable.Range(0, 2000).Select(i =>
            $"Komponente {i}: Version oder Inhalt weicht vom Freigabepaket ab. " + new string('X', 100)));
        // Override only the diagnostic region: the rest uses the real release page.
        panel.DataContext = new { StoreError = message };
        foreach (var size in new[] { new Size(1280, 900), new Size(1920, 1080) })
        {
            view.Width = size.Width; view.Height = size.Height;
            expander.IsExpanded = false;
            Layout();
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.InRange(panel.ActualHeight, 1, 110);
            expander.IsExpanded = true;
            Layout();
            var details = Assert.Single(Descendants(expander).OfType<TextBox>());
            Assert.Equal(message, details.Text);
            Assert.True(details.IsReadOnly);
            Assert.InRange(details.ActualHeight, 1, 140);
            Assert.InRange(panel.ActualHeight, 1, 250);
            var scroll = Descendants(details).OfType<ScrollViewer>().First();
            Assert.True(scroll.ScrollableHeight > 0);
            scroll.ScrollToEnd(); Layout();
            Assert.True(scroll.VerticalOffset > 0);
            var releases = Descendants(view).OfType<ListBox>().Single();
            Assert.True(releases.ActualHeight > 30, "Release-Liste muss trotz langer Details sichtbar bleiben.");
            void Layout()
            {
                view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout(); Sta.Flush(dispatcher);
            }
        }
        panel.DataContext = new { StoreError = (string?)null };
        view.UpdateLayout(); Sta.Flush(dispatcher);
        Assert.Equal(Visibility.Collapsed, panel.Visibility);
    }
}
