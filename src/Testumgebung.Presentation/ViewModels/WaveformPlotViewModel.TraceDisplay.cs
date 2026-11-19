using System.Collections.ObjectModel;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class WaveformPlotViewModel
{
    private WaveformPlotViewModel? _parent;
    private bool _synchronizing, _hasTraceData;
    private bool _showMeasurements = true, _showSlopes = true, _showLimits = true, _showSafety = true, _showSuppressed = true;
    public ObservableCollection<WaveformPlotViewModel> Panes { get; } = [];
    public bool IsPane => _parent is not null;
    public bool HasPanes => Panes.Count > 0;
    public bool ShowContentFilters => !IsPane && _hasTraceData;
    public string PaneTitle { get; private set; } = "";
    public string ReplayWarning { get; private set; } = "";
    public bool HasReplayWarning => ReplayWarning.Length > 0;
    public double PreferredHeight => HasPanes ? 500 : 230;
    public bool ShowMeasurements { get => _showMeasurements; set { if (SetProperty(ref _showMeasurements, value)) FilterChanged(); } }
    public bool ShowSlopes { get => _showSlopes; set { if (SetProperty(ref _showSlopes, value)) FilterChanged(); } }
    public bool ShowLimits { get => _showLimits; set { if (SetProperty(ref _showLimits, value)) FilterChanged(); } }
    public bool ShowSafety { get => _showSafety; set { if (SetProperty(ref _showSafety, value)) FilterChanged(); } }
    public bool ShowSuppressed { get => _showSuppressed; set { if (SetProperty(ref _showSuppressed, value)) FilterChanged(); } }

    private bool EffectiveVisible(PlotChannel channel)
    {
        if (!channel.IsVisible) return false;
        var source = _sources[channel.ColorIndex];
        return source.Kind switch
        {
            PlotContentKind.Measurement => ShowMeasurements,
            PlotContentKind.Slope => ShowSlopes,
            PlotContentKind.Limit => ShowLimits && (source.SlopeRelated ? ShowSlopes : ShowMeasurements),
            _ => ShowSafety && ShowMeasurements
        };
    }

    private void FilterChanged()
    {
        Render();
        foreach (var pane in Panes) { CopyFilters(pane); pane.Render(); }
    }
    private void CopyFilters(WaveformPlotViewModel pane)
    {
        pane._showMeasurements = ShowMeasurements; pane._showSlopes = ShowSlopes; pane._showLimits = ShowLimits;
        pane._showSafety = ShowSafety; pane._showSuppressed = ShowSuppressed;
    }
    private void SynchronizePanes(Action<WaveformPlotViewModel> update)
    {
        if (IsPane || _synchronizing) return;
        _synchronizing = true;
        try { foreach (var pane in Panes) update(pane); }
        finally { _synchronizing = false; }
    }

    private void ResetPanes() { if (!IsPane) Panes.Clear(); }

    private void SetTraceData(WaveformSet input)
    {
        var remembered = Panes.SelectMany(pane => pane.Channels).ToDictionary(channel => channel.Id, channel => channel.IsVisible);
        var set = TraceRecordingSnapshot.ForDisplay(input);
        var waves = set.Channels.Where(wave => wave.Count > 0).ToArray();
        if (waves.Length == 0) { Clear(); return; }
        var times = waves.SelectMany(wave => Enumerable.Range(0, wave.Count).Select(wave.TimeOf)).Distinct().Order().ToArray();
        SetWaveChannels(waves, times);
        var groups = waves.GroupBy(wave => wave.Unit).ToArray();
        if (!IsPane && groups.Length > 1)
        {
            foreach (var group in groups)
            {
                var pane = new WaveformPlotViewModel { _parent = this, PaneTitle = group.Key, TimeOrigin = TimeOrigin };
                CopyFilters(pane);
                pane.SetWaveChannels(group.ToArray(), times);
                foreach (var channel in pane.Channels)
                    if (remembered.TryGetValue(channel.Id, out var visible)) channel.IsVisible = visible;
                Panes.Add(pane);
            }
            SynchronizePanes(pane => { pane.SetView(ViewStart, ViewEnd); pane.CursorTime = CursorTime; });
        }
        OnPropertiesChanged(nameof(HasPanes), nameof(PreferredHeight), nameof(ShowContentFilters), nameof(ReplayWarning), nameof(HasReplayWarning));
    }

    private void SetWaveChannels(IReadOnlyList<Waveform> waves, double[] times)
    {
        var metadata = waves.ToDictionary(wave => wave.Channel, wave => EnvelopeTraceMetadata.Decode(wave.Quality));
        var violations = metadata.ToDictionary(pair => pair.Key, pair => pair.Value?.Violations ?? []);
        var samples = metadata.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!.OriginalTimes);
        foreach (var raw in waves.Where(wave => wave.Channel.EndsWith(" · Messwert", StringComparison.Ordinal)
            && !metadata.Any(pair => pair.Key == wave.Channel[..^11] && pair.Value is { Curve: not "Steigung" })))
            samples[raw.Channel] = Enumerable.Range(0, raw.Count).Select(raw.TimeOf).ToArray();
        var suppressed = metadata.SelectMany(pair => (pair.Value?.SuppressedSlopes ?? []).Select(sample => new SuppressedSlopeRow(pair.Key, sample))).ToArray();
        _hasTraceData = metadata.Values.Any(value => value is not null) || samples.Count > 0;
        ReplayWarning = string.Join(" ", waves.Select(wave => wave.DisplayWarning).Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct());
        SetData(waves.Select(wave => Resample(wave, times)).ToArray(), 0, times, keepView: true,
            violations: violations, pointTrace: samples.Count > 0, sampleTimes: samples, suppressedSlopes: suppressed);
    }

    private static PlotSource Resample(Waveform wave, double[] times)
    {
        var values = new double[times.Length];
        var index = 0;
        for (var target = 0; target < times.Length; target++)
        {
            var time = times[target];
            while (index + 1 < wave.Count && wave.TimeOf(index + 1) < time) index++;
            if (time <= wave.TimeOf(0)) values[target] = wave.Values[0];
            else if (index + 1 >= wave.Count) values[target] = wave.Values[^1];
            else
            {
                var weight = Math.Clamp((time - wave.TimeOf(index)) / (wave.TimeOf(index + 1) - wave.TimeOf(index)), 0, 1);
                values[target] = wave.Values[index] * (1 - weight) + wave.Values[index + 1] * weight;
            }
        }
        var slope = EnvelopeTraceMetadata.Decode(wave.Quality)?.Curve == "Steigung" || wave.Channel.Contains("(Steigung)", StringComparison.Ordinal);
        return new(wave.Channel, wave.Unit, values)
        {
            Kind = Classify(wave, slope), SlopeRelated = slope, FirstTime = wave.TimeOf(0), LastTime = wave.TimeOf(wave.Count - 1)
        };
    }
    private static PlotContentKind Classify(Waveform wave, bool slope)
    {
        if (wave.Channel.Contains(" · Sicherheit", StringComparison.Ordinal) || wave.Channel.Contains(" · Abbruch", StringComparison.Ordinal)) return PlotContentKind.Safety;
        if (wave.Channel.Contains(" · Untergrenze", StringComparison.Ordinal) || wave.Channel.Contains(" · Obergrenze", StringComparison.Ordinal)
            || wave.Channel.Contains(" · Zielband", StringComparison.Ordinal)) return PlotContentKind.Limit;
        return slope ? PlotContentKind.Slope : PlotContentKind.Measurement;
    }

    private static bool WithinSource(PlotSource source, double time)
        => (source.FirstTime is null || time >= source.FirstTime) && (source.LastTime is null || time <= source.LastTime);

    private void UpdateSuppressionMarkers()
    {
        if (!ShowSuppressed || !ShowSlopes || _viewEnd <= _viewStart) return;
        var visible = Channels.Where(EffectiveVisible).Select(channel => channel.Id).ToHashSet();
        foreach (var row in _suppressedSlopes.Where(row => visible.Contains(row.Channel)
            && double.IsFinite(row.Sample.Time) && double.IsFinite(row.Sample.Slope)
            && row.Sample.Time >= _viewStart && row.Sample.Time <= _viewEnd).Take(1000))
        {
            var sample = row.Sample;
            SuppressionMarkers.Add(new((sample.Time - _viewStart) / (_viewEnd - _viewStart) * PlotWidth, Y(sample.Slope), row.Description));
        }
    }
}
