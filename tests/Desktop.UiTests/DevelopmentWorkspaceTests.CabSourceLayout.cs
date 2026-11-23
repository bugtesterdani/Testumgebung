using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    [Fact]
    public void Cab_source_keeps_logical_lines_horizontal_after_real_window_layout()
    {
        Sta.Run(dispatcher =>
        {
            var text = "; Synthetic fixture for source layout\rJ\rT 1,8,0,5,3;" + new string('x', 300) + "\rA 1\r";
            var bytes = Encoding.UTF8.GetBytes(text);
            var layout = CabLabelImport.Read(bytes, 203);
            var window = new CabSourceWindow(layout) { Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); Sta.Flush(dispatcher);
                foreach (var width in new[] { 800d, 1150d, 1600d })
                {
                    window.Width = width; window.UpdateLayout(); Sta.Flush(dispatcher);
                    var box = Assert.Single(Descendants(window).OfType<RichTextBox>());
                    var positions = box.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>())
                        .Where(r => r.Text.Contains("Synthetic", StringComparison.Ordinal) || r.Text.Contains(new string('x', 100), StringComparison.Ordinal)).ToArray();
                    Assert.Equal(2, positions.Length);
                    foreach (var run in positions)
                    {
                        var start = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                        var end = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                        Assert.True(Math.Abs(start.Y - end.Y) < 1, $"Quelltext wird bei Fensterbreite {width} umgebrochen: {start} -> {end}");
                        Assert.True(end.X - start.X > 100);
                    }
                    Assert.Equal(bytes, CabLabelLayout.Render(layout));
                    Assert.Equal(text.ReplaceLineEndings("\n") + "\n", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.ReplaceLineEndings("\n"));
                    var scroll = Assert.Single(Descendants(window).OfType<ScrollViewer>(), s => s.Content is StackPanel panel
                        && panel.Children.OfType<DockPanel>().Any(row => row.Children.OfType<RichTextBox>().Any()));
                    Assert.True(scroll.ExtentWidth > scroll.ViewportWidth);
                    scroll.ScrollToRightEnd(); window.UpdateLayout(); Sta.Flush(dispatcher);
                    Assert.True(scroll.HorizontalOffset > 0);
                    scroll.ScrollToLeftEnd(); window.UpdateLayout(); Sta.Flush(dispatcher);
                    if (width == 1150) CaptureCabSourceLayout(window);
                }
            }
            finally { window.Close(); }
        });
    }

    private static void CaptureCabSourceLayout(Window window)
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/verification"));
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, "cab-source-layout.png")); encoder.Save(file);
    }
}
