/**
 * @file
 * @brief Berechnet Zeichenpunkte, Achsen, Cursor und RMS-Anzeige aus den Kurvendaten des sichtbaren Bereichs.
 */

using System.Collections.ObjectModel;
using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Kanal einer Kurvenanzeige (Datenquelle des Plots).</summary>
public sealed record PlotSource(string Id, string Unit, SampleBuffer Samples)
{
    public PlotSource(string Id, string Unit, double[] Samples) : this(Id, Unit, new SampleBuffer(Samples)) { }
}

/// <summary>Kanal mit Sichtbarkeit, Cursorwert und Effektivwert im Ansichtsfenster.</summary>
public sealed class PlotChannel : ObservableObject
{
    private readonly Action _changed;
    private bool _isVisible = true;
    private string _cursorText = "—";
    private string _rmsText = "—";

    public PlotChannel(string id, string unit, int colorIndex, Action changed)
    {
        Id = id;
        Unit = unit;
        ColorIndex = colorIndex;
        _changed = changed;
    }

    public string Id { get; }
    public string Unit { get; }
    public int ColorIndex { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set { if (SetProperty(ref _isVisible, value)) _changed(); }
    }

    /// <summary>Wert am Cursor.</summary>
    public string CursorText
    {
        get => _cursorText;
        set => SetProperty(ref _cursorText, value);
    }

    /// <summary>Effektivwert im sichtbaren Zeitfenster.</summary>
    public string RmsText
    {
        get => _rmsText;
        set => SetProperty(ref _rmsText, value);
    }
}

/// <summary>Beschriftung der Y-Achse (Position in Plotkoordinaten).</summary>
public sealed record PlotAxisLabel(double Y, string Text);
public sealed record PlotViolationMarker(double X);
/// <summary>An actual received measurement, excluding interpolated limit knots.</summary>
public sealed record PlotSampleMarker(double X, double Y, int ColorIndex);

/// <summary>
/// Kurvenanzeige mit Kanalauswahl, Zoom, Verschieben und Cursor (Abschnitt 8). Zeichenfläche
/// <see cref="PlotWidth"/> × <see cref="PlotHeight"/>; die Ansicht skaliert per Viewbox.
/// Darstellung reduziert je Pixelspalte auf Minimum/Maximum, damit Spitzen sichtbar bleiben.
/// </summary>
public sealed class WaveformPlotViewModel : ObservableObject
{
    public const double PlotWidth = 1000;
    public const double PlotHeight = 260;
    private const int Columns = 1000;

    private IReadOnlyList<PlotSource> _sources = [];
    private IReadOnlyDictionary<string, double[]> _violations = new Dictionary<string, double[]>();
    public ObservableCollection<PlotViolationMarker> ViolationMarkers { get; } = [];
    private IReadOnlyDictionary<string, double[]> _sampleTimes = new Dictionary<string, double[]>();
    public ObservableCollection<PlotSampleMarker> SampleMarkers { get; } = [];
    private bool _pointTrace;
    private double _sampleRate = 1;
    private IReadOnlyList<double>? _times;
    private double _viewStart;
    private double _viewEnd;
    private double? _cursor;
    private double _yMin = -1;
    private double _yMax = 1;

    public WaveformPlotViewModel()
    {
        ZoomInCommand = new RelayCommand(() => Zoom(0.5), () => HasData);
        ZoomOutCommand = new RelayCommand(() => Zoom(2), () => HasData);
        ZoomAllCommand = new RelayCommand(() => SetView(0, Duration), () => HasData);
        PanLeftCommand = new RelayCommand(() => Pan(-0.25), () => HasData);
        PanRightCommand = new RelayCommand(() => Pan(0.25), () => HasData);
        ClearCursorCommand = new RelayCommand(() => CursorTime = null, () => _cursor is not null);
    }

    public ObservableCollection<PlotChannel> Channels { get; } = [];
    public ObservableCollection<PlotSeries> Series { get; } = [];
    public ObservableCollection<PlotAxisLabel> YLabels { get; } = [];

    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomAllCommand { get; }
    public RelayCommand PanLeftCommand { get; }
    public RelayCommand PanRightCommand { get; }
    public RelayCommand ClearCursorCommand { get; }

    public bool HasEnvelopeViolation => _violations.Values.Any(times => times.Length > 0);
    public bool HasData => _sources.Count > 0 && Count > 0;
    public int Count => _sources.Count == 0 ? 0 : _sources[0].Samples.Length;

    /// <summary>Gesamtdauer in Sekunden.</summary>
    public double Duration => Count == 0 ? 0
        : _times is { } items ? items[^1] + (!_pointTrace && items.Count > 1 ? items[^1] - items[^2] : 0)
        : Count / _sampleRate;

    public double ViewStart => _viewStart;
    public double ViewEnd => _viewEnd;

    /// <summary>Nulllinie (falls im Wertebereich).</summary>
    public double ZeroY => Y(0);
    public bool ShowZero => _yMin < 0 && _yMax > 0;

    public double? CursorTime
    {
        get => _cursor;
        set
        {
            if (value is { } value2) value = Math.Clamp(value2, 0, Duration);
            if (_cursor == value) return;
            _cursor = value;
            UpdateCursor();
            ClearCursorCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasCursor => _cursor is { } number && number >= _viewStart && number <= _viewEnd;
    public double CursorX => _cursor is { } number && _viewEnd > _viewStart ? (number - _viewStart) / (_viewEnd - _viewStart) * PlotWidth : 0;
    public string CursorText => _cursor is { } number ? "Cursor " + FormatTime(number) : "Cursor: in die Kurve klicken";
    public string ViewText => HasData ? $"{FormatTime(_viewStart)} … {FormatTime(_viewEnd)} von {FormatTime(Duration)}" : "Keine Kurve";
    public string RangeText => HasData ? string.Create(CultureInfo.InvariantCulture, $"{_yMin:0.###} … {_yMax:0.###} {Unit}") : "";
    /// <summary>Y-Achse: oben, Mitte, unten (die Achse liegt außerhalb der gestreckten Zeichenfläche).</summary>
    public string YTopText => HasData ? _yMax.ToString("0.##", CultureInfo.InvariantCulture) : "";
    public string YMidText => HasData ? ((_yMax + _yMin) / 2).ToString("0.##", CultureInfo.InvariantCulture) : "";
    public string YBottomText => HasData ? _yMin.ToString("0.##", CultureInfo.InvariantCulture) : "";

    private string Unit => string.Join("/", Channels.Where(channel => channel.IsVisible).Select(channel => channel.Unit).Distinct());

    public void Clear() => SetData([], 1, null);

    /// <summary>Neue Daten. <paramref name="keepView"/> behält Zoom und Cursor, soweit noch im Bereich.</summary>
    public void SetData(IReadOnlyList<PlotSource> sources, double sampleRate, IReadOnlyList<double>? times, bool keepView = false, IReadOnlyDictionary<string, double[]>? violations = null, bool pointTrace = false, IReadOnlyDictionary<string, double[]>? sampleTimes = null)
    {
        var wasFullView = _viewStart == 0 && Math.Abs(_viewEnd - Duration) < 1e-9;
        _pointTrace = pointTrace;
        _sampleTimes = sampleTimes ?? new Dictionary<string, double[]>();
        var visible = Channels.ToDictionary(channel => channel.Id, channel => channel.IsVisible);
        _sources = sources;
        _violations = violations ?? new Dictionary<string, double[]>();
        _sampleRate = sampleRate > 0 ? sampleRate : 1;
        _times = times;
        Channels.Clear();
        var index = 0;
        foreach (var plotSource in sources)
        {
            var ch = new PlotChannel(plotSource.Id, plotSource.Unit, index++, Render);
            if (keepView && visible.TryGetValue(plotSource.Id, out var flag)) ch.IsVisible = flag;
            Channels.Add(ch);
        }
        var durationValue = Duration;
        if (!keepView || _viewEnd <= _viewStart || _viewEnd > durationValue + 1e-12 || (pointTrace && wasFullView)) { _viewStart = 0; _viewEnd = durationValue; }
        if (!keepView || _cursor > durationValue) _cursor = null;
        foreach (var relayCommand in new[] { ZoomInCommand, ZoomOutCommand, ZoomAllCommand, PanLeftCommand, PanRightCommand, ClearCursorCommand })
            relayCommand.RaiseCanExecuteChanged();
        Render();
    }

    public void SetData(CurveDefinition curve, bool keepView = false)
        => SetData(curve.Channels.Select(curveChannel => new PlotSource(curveChannel.Id, curveChannel.Unit, curveChannel.Samples)).ToList(), curve.SampleRate, curve.Times, keepView);

    public void SetData(WaveformSet set)
    {
        if (set.Channels.Count == 0) { Clear(); return; }
        var first = set.Channels[0];
        var metadata = set.Channels.ToDictionary(waveform => waveform.Channel, waveform => EnvelopeTraceMetadata.Decode(waveform.Quality));
        var violations = metadata.ToDictionary(pair => pair.Key, pair => pair.Value?.Violations ?? []);
        var samples = metadata.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!.OriginalTimes);
        SetData(set.Channels.Select(waveform => new PlotSource(waveform.Channel, waveform.Unit, waveform.Values)).ToList(), first.SampleRate, first.TimeValues,
            keepView: true, violations: violations, pointTrace: samples.Count > 0, sampleTimes: samples);
    }

    public void SetView(double start, double end)
    {
        var durationValue = Duration;
        if (durationValue <= 0) return;
        var minSpan = Math.Max(durationValue / Math.Max(Count, 1) * 4, 1e-9);
        if (end - start < minSpan) end = start + minSpan;
        if (end - start > durationValue) { start = 0; end = durationValue; }
        if (start < 0) { end -= start; start = 0; }
        if (end > durationValue) { start -= end - durationValue; end = durationValue; }
        _viewStart = Math.Max(0, start);
        _viewEnd = end;
        Render();
    }

    private void Zoom(double factor)
    {
        var center = _cursor is { } number && number >= _viewStart && number <= _viewEnd ? number : (_viewStart + _viewEnd) / 2;
        var half = (_viewEnd - _viewStart) * factor / 2;
        SetView(center - half, center + half);
    }

    private void Pan(double fraction)
    {
        var span = Math.Max(_viewEnd - _viewStart, 1e-12);
        SetView(_viewStart + span * fraction, _viewEnd + span * fraction);
    }

    /// <summary>Plot-X (0…<see cref="PlotWidth"/>) → Zeit in Sekunden.</summary>
    public double TimeAtX(double x) => _viewStart + Math.Clamp(x / PlotWidth, 0, 1) * (_viewEnd - _viewStart);

    /// <summary>Cursor per Klick setzen.</summary>
    public void SetCursorAtX(double x) => CursorTime = TimeAtX(x);

    /// <summary>Bereich aufziehen → zoomen.</summary>
    public void ZoomToX(double x0, double x1)
    {
        if (Math.Abs(x1 - x0) < 4) return;
        SetView(TimeAtX(Math.Min(x0, x1)), TimeAtX(Math.Max(x0, x1)));
    }

    /// <summary>Mausrad: um die Mausposition zoomen.</summary>
    public void WheelZoom(double x, bool zoomIn)
    {
        var time = TimeAtX(x);
        var frequency = zoomIn ? 0.8 : 1.25;
        SetView(time - (time - _viewStart) * frequency, time + (_viewEnd - time) * frequency);
    }

    private int IndexAt(double time)
    {
        if (Count == 0) return 0;
        if (_times is null) return Math.Clamp((int)Math.Floor(time * _sampleRate), 0, Count - 1);
        var low = 0;
        var high = _times.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_times[middle] == time) return middle;
            if (_times[middle] < time) low = middle + 1;
            else high = middle - 1;
        }
        return Math.Clamp(high, 0, Count - 1);
    }

    private double TimeOf(int index) => _times is { } items ? items[index] : index / _sampleRate;

    private double ValueAt(SampleBuffer sampleBuffer, double time)
    {
        var index = IndexAt(time);
        if (index >= sampleBuffer.Length - 1) return sampleBuffer[^1];
        var t0 = TimeOf(index);
        var t1 = TimeOf(index + 1);
        return t1 > t0 ? sampleBuffer[index] + (sampleBuffer[index + 1] - sampleBuffer[index]) * Math.Clamp((time - t0) / (t1 - t0), 0, 1) : sampleBuffer[index];
    }

    private double Y(double value) => _yMax > _yMin ? PlotHeight - (value - _yMin) / (_yMax - _yMin) * PlotHeight : PlotHeight / 2;

    private void UpdateRmsLabels()
    {
        foreach (var channel in Channels)
        {
            if (!HasData) { channel.RmsText = "—"; continue; }
            var sampleBuffer = _sources[channel.ColorIndex].Samples;
            var startIndex = IndexAt(_viewStart);
            var bit = Math.Max(startIndex, Math.Min(Count - 1, IndexAt(_viewEnd - 1e-15)));
            double sum = 0;
            for (var index = startIndex; index <= bit; index++) sum += sampleBuffer[index] * sampleBuffer[index];
            channel.RmsText = string.Create(CultureInfo.InvariantCulture, $"{Math.Sqrt(sum / (bit - startIndex + 1)):0.###} {channel.Unit}");
        }
    }

    private void Render()
    {
        ViolationMarkers.Clear();
        SampleMarkers.Clear();
        Series.Clear();
        YLabels.Clear();
        var visible = Channels.Where(channel => channel.IsVisible).ToList();
        if (HasData && visible.Count > 0)
        {
            // Wertebereich über die ganze Kurve (stabil beim Zoomen), 5 % Rand
            var min = visible.Min(channel => _sources[channel.ColorIndex].Samples.Min());
            var max = visible.Max(channel => _sources[channel.ColorIndex].Samples.Max());
            if (max - min < 1e-12) { min -= 1; max += 1; }
            var pad = (max - min) * 0.05;
            _yMin = min - pad;
            _yMax = max + pad;
            for (var previousIndex = 0; previousIndex <= 4; previousIndex++)
            {
                var value = _yMin + (_yMax - _yMin) * previousIndex / 4;
                YLabels.Add(new PlotAxisLabel(Y(value), value.ToString("0.##", CultureInfo.InvariantCulture)));
            }

            var i0 = IndexAt(_viewStart);
            var i1 = Math.Min(Count - 1, IndexAt(_viewEnd) + 1);
            var span = Math.Max(_viewEnd - _viewStart, 1e-12);
            foreach (var channel in visible)
            {
                var sampleBuffer = _sources[channel.ColorIndex].Samples;
                var points = new List<PlotPoint>();
                double X(int index) => (TimeOf(index) - _viewStart) / span * PlotWidth;
                if (i1 - i0 <= Columns * 2)
                {
                    for (var index2 = i0; index2 <= i1; index2++) points.Add(new PlotPoint(X(index2), Y(sampleBuffer[index2])));
                }
                else
                {
                    var per = (i1 - i0 + 1) / (double)Columns;
                    for (var col = 0; col < Columns; col++)
                    {
                        var startIndex = i0 + (int)(col * per);
                        var bit = Math.Min(i1, i0 + (int)((col + 1) * per) - 1);
                        int imin = startIndex, imax = startIndex;
                        for (var index3 = startIndex; index3 <= bit; index3++)
                        {
                            if (sampleBuffer[index3] < sampleBuffer[imin]) imin = index3;
                            if (sampleBuffer[index3] > sampleBuffer[imax]) imax = index3;
                        }
                        var (first, second) = imin <= imax ? (imin, imax) : (imax, imin);
                        points.Add(new PlotPoint(X(first), Y(sampleBuffer[first])));
                        if (second != first) points.Add(new PlotPoint(X(second), Y(sampleBuffer[second])));
                    }
                }
                Series.Add(new PlotSeries(channel.Id, channel.ColorIndex, points, $"{channel.Id} [{channel.Unit}]"));
            }
        }
        UpdateSampleMarkers();
        UpdateViolationMarkers();
        UpdateRmsLabels();
        UpdateCursor();
        OnPropertiesChanged(nameof(HasData), nameof(Count), nameof(Duration), nameof(ViewStart), nameof(ViewEnd), nameof(ViewText),
            nameof(RangeText), nameof(ZeroY), nameof(ShowZero), nameof(YTopText), nameof(YMidText), nameof(YBottomText));
    }

    /// <summary>Mark actual sample times, never synthetic interpolation points. Bound display work per channel.</summary>
    private void UpdateSampleMarkers()
    {
        var span = Math.Max(_viewEnd - _viewStart, 1e-12);
        foreach (var channel in Channels.Where(channel => channel.IsVisible))
        {
            if (!_sampleTimes.TryGetValue(channel.Id, out var original)) continue;
            var times = original.Where(original2 => original2 >= _viewStart && original2 <= _viewEnd).ToArray();
            var count = Math.Min(1000, times.Length);
            for (var index2 = 0; index2 < count; index2++)
            {
                // Preserve first and newest points when reducing a dense marker cloud.
                var index = times.Length <= 1000 ? index2 : (int)((long)index2 * (times.Length - 1) / (count - 1));
                var time = times[index]; var value = ValueAt(_sources[channel.ColorIndex].Samples, time);
                SampleMarkers.Add(new((time - _viewStart) / span * PlotWidth, Y(value), channel.ColorIndex));
            }
        }
    }
    private void UpdateViolationMarkers()
    {
        if (_viewEnd <= _viewStart) return;
        var times = Channels.Where(channel => channel.IsVisible).SelectMany(channel => _violations.GetValueOrDefault(channel.Id) ?? []);
        foreach (var time in times.Where(time => double.IsFinite(time) && time >= _viewStart && time <= _viewEnd).Distinct().Take(1000))
            ViolationMarkers.Add(new((time - _viewStart) / (_viewEnd - _viewStart) * PlotWidth));
    }
    private void UpdateCursor()
    {
        foreach (var channel in Channels)
            channel.CursorText = _cursor is { } time && HasData
                ? string.Create(CultureInfo.InvariantCulture, $"{ValueAt(_sources[channel.ColorIndex].Samples, time):0.###} {channel.Unit}")
                : "—";
        OnPropertiesChanged(nameof(CursorTime), nameof(HasCursor), nameof(CursorX), nameof(CursorText));
    }

    public static string FormatTime(double seconds)
        => Math.Abs(seconds) < 10
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds * 1000:0.###} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds:0.###} s");
}
