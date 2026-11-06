using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Curves;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;
public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyEnvelopePlot(Dispatcher dispatcher)
    {
        var band = new CurveDefinition { Id = "band", Name = "Grenzband", Version = "1", SampleRate = 1, Times = [0, 1, 2],
            Channels = [new("Untergrenze", "V", [0, 0, 0]), new("Obergrenze", "V", [10, 1, 10])] };
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "capture", [new("Spannung", "V", band)], DateTimeOffset.UnixEpoch, maxGap: 2);
        recordings.Add("owner", "capture", 0, [5], DateTimeOffset.UnixEpoch);
        recordings.Add("owner", "capture", 2, [5], DateTimeOffset.UnixEpoch.AddSeconds(2));
        var result = recordings.Stop("owner", "capture");
        var model = new WaveformPlotViewModel(); model.SetData(result.Data);
        var plot = new WaveformPlot { DataContext = model };
        var window = new Window { Title = "Grenzbandprüfung", Content = new Border { Background = Brushes.White, Padding = new Thickness(16), Child = plot }, Width = 1000, Height = 520,
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show(); Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Equal(3, model.Series.Count); Assert.Single(model.ViolationMarkers);
            Assert.Contains(Descendants(plot).OfType<Line>(), line => line.Stroke is SolidColorBrush brush && brush.Color == Colors.Firebrick);
            CaptureVisual(window, "messwert-grenzband.png");
            model.SetView(0.5, 1.5); window.UpdateLayout(); Assert.Single(model.ViolationMarkers);
            model.Channels[0].IsVisible = false; window.UpdateLayout(); Assert.Empty(model.ViolationMarkers);
        }
        finally { window.Close(); }
    }
}