using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    [Theory]
    [InlineData(1920, 1080, 96, 1920, 1080)]
    [InlineData(3840, 2160, 192, 1920, 1080)]
    [InlineData(2880, 1620, 144, 1920, 1080)]
    [InlineData(1920, 1080, 120, 1536, 864)]
    [InlineData(1366, 728, 0, 1366, 728)]
    public void Monitor_pixels_are_converted_using_the_current_window_DPI(int width, int height, uint dpi, double expectedWidth, double expectedHeight)
        => Assert.Equal(new Size(expectedWidth, expectedHeight), AdaptiveWindowLayout.WorkAreaInDips(width, height, dpi));

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 1)]
    [InlineData(1200, 850, 1920, 1080, 0.8)]
    [InlineData(1500, 680, 1920, 1080, 0.8)]
    [InlineData(1920, 1080, 1200, 850, 0.8)]
    [InlineData(800, 400, 1366, 768, 0.65)]
    [InlineData(0, 0, 1366, 768, 1)]
    public void Layout_density_uses_both_window_and_current_monitor(double width, double height, double workWidth, double workHeight, double expected)
        => Assert.Equal(expected, AdaptiveWindowLayout.Calculate(new(width, height), new(workWidth, workHeight)), 6);

    private static void VerifyAdaptiveLayout(Dispatcher dispatcher)
    {
        var work = new Size(1366, 728);
        var content = new Grid();
        content.Children.Add(new TextBlock { Text = "Prüfplatz – Testprogramm", FontSize = 22 });
        var viewport = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var window = new Window { Content = viewport, Width = 1500, Height = 900,
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual };
        _ = new AdaptiveWindowLayout(window, viewport, content, _ => work);
        try
        {
            window.Show(); Layout();
            Assert.InRange(window.Width, 1, work.Width);
            Assert.InRange(window.Height, 1, work.Height);
            var scale = Assert.IsType<ScaleTransform>(content.LayoutTransform);
            Assert.InRange(scale.ScaleX, 0.65, 0.99);
            Assert.Equal(scale.ScaleX, scale.ScaleY);
            work = new Size(1920, 1040);
            window.Left += 20;
            window.Width = 1800; window.Height = 1000; Layout();
            Assert.Equal(1, scale.ScaleX);
            window.Width = 1200; Layout();
            Assert.InRange(scale.ScaleX, 0.65, 0.81);
            // Same window moves to a smaller work area without changing display 1.
            work = new Size(1100, 680);
            window.Left += 20; Layout();
            Assert.InRange(window.Width, 1, 1100);
            Assert.InRange(window.Height, 1, 680);
            Assert.InRange(scale.ScaleX, 0.65, 0.74);
            work = new Size(800, 500);
            window.Left += 20; Layout();
            Assert.Equal(0.65, scale.ScaleX);
            Assert.True(viewport.ScrollableWidth > 0);
            Assert.True(viewport.ScrollableHeight > 0);
            viewport.ScrollToBottom(); viewport.ScrollToRightEnd(); Layout();
            Assert.True(viewport.VerticalOffset > 0);
            Assert.True(viewport.HorizontalOffset > 0);
        }
        finally { window.Close(); }
        void Layout() { window.UpdateLayout(); Sta.Flush(dispatcher); }

        // Verify actual transformed page bounds. Pixel rendering is unavailable
        // in some unattended Windows sessions, so these checks use WPF layout.
        foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
        {
            var view = new Testumgebung.Desktop.Views.ReleasesView { DataContext = new Testumgebung.Presentation.ViewModels.ShellViewModel() };
            var factor = AdaptiveWindowLayout.Calculate(size, size);
            view.Width = size.Width / factor; view.Height = size.Height / factor;
            view.LayoutTransform = new ScaleTransform(factor, factor);
            var host = new Border { Child = view, Width = size.Width, Height = size.Height };
            host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout(); Sta.Flush(dispatcher);
            host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();
            Assert.Contains(Descendants(view).OfType<TextBlock>(), text => text.ActualHeight > 0);
            var bounds = view.TransformToAncestor(host).TransformBounds(new Rect(view.RenderSize));
            Assert.InRange(bounds.Right, 1, size.Width + 0.5);
            Assert.InRange(bounds.Bottom, 1, size.Height + 0.5);
            Assert.True(((ComboBox)view.FindName("ReleaseProgramsList")).ActualWidth > 0);
        }
    }
}
