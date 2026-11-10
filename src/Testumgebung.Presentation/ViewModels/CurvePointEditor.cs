using System.Globalization;
using Testumgebung.Domain.Curves;
using Testumgebung.Presentation.Mvvm;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Verwaltet Fixpunkte und Ziehvorschau für die aktuelle Arbeitskopie.</summary>
public sealed class CurvePointEditor : ObservableObject
{
    private readonly WaveformPlotViewModel _plot;
    private readonly Action<Func<CurveDefinition, CurveDefinition>> _apply;
    private readonly HashSet<SamplePoint> _fixedPoints = [];
    private CurveDefinition? _curve;
    private Drag? _drag;
    private bool _isEnabled;

    internal CurvePointEditor(WaveformPlotViewModel plot, Action<Func<CurveDefinition, CurveDefinition>> apply)
    {
        _plot = plot;
        _apply = apply;
        _plot.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(WaveformPlotViewModel.ViewText)) return;
            CancelDrag();
            RefreshPins();
        };
    }

    public bool CanEdit => _curve is { Count: > 0 };

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value && CanEdit)) return;
            CancelDrag();
            RefreshPins();
        }
    }

    public bool IsDragging => _drag is not null;
    public IReadOnlyList<Pin> Pins { get; private set; } = [];
    public IReadOnlyList<Pin> Markers { get; private set; } = [];
    public PlotSeries? Preview { get; private set; }

    internal void UseCurve(CurveDefinition? curve, bool reset)
    {
        CancelDrag();
        if (reset || !UsesSameGrid(curve)) _fixedPoints.Clear();
        _curve = curve;
        if (reset || !CanEdit) IsEnabled = false;
        OnPropertyChanged(nameof(CanEdit));
    }

    private bool UsesSameGrid(CurveDefinition? curve)
        => _curve is not null && curve is not null && _curve.Count == curve.Count
            && _curve.SampleRate == curve.SampleRate && ReferenceEquals(_curve.Times, curve.Times)
            && _curve.Channels.Select(channel => channel.Id).SequenceEqual(curve.Channels.Select(channel => channel.Id));

    public bool TogglePin(double x, double y)
    {
        if (IsDragging || FindPoint(x, y) is not { } point) return false;
        if (!_fixedPoints.Add(point)) _fixedPoints.Remove(point);
        RefreshPins();
        return true;
    }

    public bool BeginDrag(double x, double y)
    {
        if (_curve is not { } curve || FindPoint(x, y) is not { } point || _fixedPoints.Contains(point)) return false;
        var channelPins = _fixedPoints.Where(pin => pin.ChannelIndex == point.ChannelIndex).Select(pin => pin.SampleIndex);
        var left = channelPins.Where(index => index < point.SampleIndex).DefaultIfEmpty(Math.Max(0, point.SampleIndex - 1)).Max();
        var right = channelPins.Where(index => index > point.SampleIndex).DefaultIfEmpty(Math.Min(curve.Count - 1, point.SampleIndex + 1)).Min();
        _drag = new Drag(point, left, right, y);
        return true;
    }

    public void MoveDrag(double y)
    {
        if (_drag is not { } drag || _curve is not { } curve) return;
        var series = _plot.Series.First(line => line.ColorIndex == drag.Point.ChannelIndex);
        var channel = curve.Channels[drag.Point.ChannelIndex];
        var left = _plot.XAtTime(curve.TimeOf(drag.Left));
        var right = _plot.XAtTime(curve.TimeOf(drag.Right));
        var point = _plot.XAtTime(curve.TimeOf(drag.Point.SampleIndex));
        // The displayed series can omit samples at low zoom. Include the grabbed point and both anchors.
        var anchors = new[] { drag.Left, drag.Point.SampleIndex, drag.Right }
            .Select(index => new PlotPoint(_plot.XAtTime(curve.TimeOf(index)), _plot.YAtValue(channel.Samples[index])));
        var points = series.Points.Concat(anchors).DistinctBy(sample => sample.X).OrderBy(sample => sample.X);
        Preview = series with
        {
            Points = points.Select(sample => sample with
            {
                Y = sample.Y + (y - drag.StartY) * CurveOps.PointMoveWeight(sample.X, point, left, right),
            }).ToArray(),
        };
        OnPropertyChanged(nameof(Preview));
    }

    public void EndDrag(double y)
    {
        if (_drag is not { } drag) return;
        CancelDrag();
        if (y == drag.StartY) return;
        var delta = _plot.ValueDelta(y - drag.StartY);
        _apply(curve => CurveOps.MovePoint(curve, drag.Point.ChannelIndex, drag.Point.SampleIndex, drag.Left, drag.Right, delta));
    }

    public void CancelDrag()
    {
        _drag = null;
        if (Preview is null) return;
        Preview = null;
        OnPropertyChanged(nameof(Preview));
    }

    private SamplePoint? FindPoint(double x, double y)
    {
        if (!IsEnabled || _curve is not { } curve) return null;
        const double hitDistance = 10;
        var time = _plot.TimeAtX(x);
        if (x < _plot.XAtTime(curve.TimeOf(0)) - hitDistance || x > _plot.XAtTime(curve.TimeOf(curve.Count - 1)) + hitDistance)
            return null;
        var closest = _plot.Channels.Where(channel => channel.IsVisible)
            .Select(channel => (channel.ColorIndex, Distance: Math.Abs(y - _plot.YAtValue(curve.ValueAt(channel.ColorIndex, time)))))
            .Where(candidate => candidate.Distance <= hitDistance)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => (int?)candidate.ColorIndex).FirstOrDefault();
        return closest is { } channelIndex ? new SamplePoint(channelIndex, _plot.NearestSample(time)) : null;
    }

    private void RefreshPins()
    {
        Pins = _fixedPoints.OrderBy(point => point.ChannelIndex).ThenBy(point => point.SampleIndex).Select(CreatePin).ToArray();
        Markers = IsEnabled
            ? Pins.Where(pin => pin.X is >= 0 and <= WaveformPlotViewModel.PlotWidth
                && _plot.Channels.Any(channel => channel.ColorIndex == pin.Point.ChannelIndex && channel.IsVisible)).ToArray()
            : [];
        OnPropertiesChanged(nameof(Pins), nameof(Markers));
    }

    private Pin CreatePin(SamplePoint point)
    {
        var curve = _curve!;
        var channel = curve.Channels[point.ChannelIndex];
        var time = curve.TimeOf(point.SampleIndex);
        var label = string.Create(CultureInfo.CurrentCulture, $"{channel.Id} · {time * 1000:0.###} ms");
        return new Pin(point, _plot.XAtTime(time), _plot.YAtValue(channel.Samples[point.SampleIndex]), label, new RelayCommand(() =>
        {
            _fixedPoints.Remove(point);
            CancelDrag();
            RefreshPins();
        }));
    }

    public sealed record Pin(SamplePoint Point, double X, double Y, string Label, RelayCommand RemoveCommand);
    public readonly record struct SamplePoint(int ChannelIndex, int SampleIndex);
    private sealed record Drag(SamplePoint Point, int Left, int Right, double StartY);
}
