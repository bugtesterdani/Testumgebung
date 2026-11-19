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
        var first = recordings.Add("owner", "capture", 0, [5], DateTimeOffset.UnixEpoch)!;
        var model = new WaveformPlotViewModel(); model.SetData(first.Data);
        var plot = new WaveformPlot { DataContext = model };
        var window = new Window { Title = "Grenzbandprüfung", Content = new Border { Background = Brushes.White, Padding = new Thickness(16), Child = plot }, Width = 1000, Height = 520,
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show(); Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Single(model.SampleMarkers);
            Assert.Contains(Descendants(plot).OfType<Ellipse>(), ellipse => ellipse.ActualWidth > 0);
            CaptureVisual(window, "messwert-live-erster-punkt.png");
            model.SetData(recordings.Add("owner", "capture", 0.25, [7], DateTimeOffset.UnixEpoch.AddSeconds(1))!.Data);
            model.SetData(recordings.Add("owner", "capture", 2, [5], DateTimeOffset.UnixEpoch.AddSeconds(2))!.Data);
            Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Equal(3, model.SampleMarkers.Count); Assert.Equal(3, Descendants(plot).OfType<Ellipse>().Count());
            Assert.Equal(3, model.Series.Count); Assert.Single(model.ViolationMarkers);
            Assert.Contains(Descendants(plot).OfType<Line>(), line => line.Stroke is SolidColorBrush brush && brush.Color == Colors.Firebrick);
            CaptureVisual(window, "messwert-grenzband.png");
            model.SetView(0.5, 1.5); window.UpdateLayout(); Assert.Single(model.ViolationMarkers);
            model.Channels[0].IsVisible = false; window.UpdateLayout(); Assert.Empty(model.ViolationMarkers);
            var slopes = new EnvelopeRecordings();
            slopes.Start("owner", "slope", [new("U", "V", null)], DateTimeOffset.UnixEpoch, maxGap: 2,
                slope: new(SlopeWindowMode.Consecutive, 2, -10, 10));
            slopes.Add("owner", "slope", 0, [1], DateTimeOffset.UnixEpoch);
            slopes.Add("owner", "slope", 1, [3], DateTimeOffset.UnixEpoch);
            model.SetData(slopes.Stop("owner", "slope").Data);
            Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Equal(2, model.Panes.Count);
            Assert.Equal(2, Descendants(plot).OfType<WaveformPlot>().Count());
            Assert.Contains(Descendants(plot).OfType<CheckBox>(), box => Equals(box.Content, "Messwert") && box.IsVisible);
            CaptureVisual(window, "messwert-steigung-getrennte-einheiten.png");
            var liveSlopes = new EnvelopeRecordings();
            liveSlopes.Start("owner", "preview", [new("U", "V", null)], DateTimeOffset.UnixEpoch,
                slope: new(SlopeWindowMode.Disjoint, 50, -1, 1));
            var firstSlopePreview = liveSlopes.Add("owner", "preview", 0, [5], DateTimeOffset.UnixEpoch);
            Assert.NotNull(firstSlopePreview);
            model.SetData(firstSlopePreview.Data);
            Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Single(model.SampleMarkers);
            Assert.Contains(Descendants(plot).OfType<Ellipse>(), ellipse => ellipse.ActualWidth > 0);
            for (var i = 1; i <= 26; i++)
            {
                var preview = liveSlopes.Add("owner", "preview", i * 0.01, [5], DateTimeOffset.UnixEpoch.AddMilliseconds(i * 10));
                if (preview is not null) model.SetData(preview.Data);
            }
            Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Equal(27, model.SampleMarkers.Count);
            Assert.All(model.Channels, channel => Assert.Equal("V", channel.Unit));
            CaptureVisual(window, "messwert-steigung-live-vor-fenster.png");
            for (var i = 27; i <= 51; i++)
            {
                var preview = liveSlopes.Add("owner", "preview", i * 0.01, [5], DateTimeOffset.UnixEpoch.AddMilliseconds(i * 10));
                if (preview is not null) model.SetData(preview.Data);
            }
            Sta.Flush(dispatcher); window.UpdateLayout();
            Assert.Contains(model.Panes, pane => pane.PaneTitle == "V/s");
            Assert.Contains(model.Panes, pane => pane.PaneTitle == "V" && pane.SampleMarkers.Count == 52);

        }
        finally { window.Close(); }
    }
}