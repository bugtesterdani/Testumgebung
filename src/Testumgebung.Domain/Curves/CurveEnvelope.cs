using Testumgebung.Contracts;

namespace Testumgebung.Domain.Curves;

/// <summary>Frozen piecewise-linear limits; no endpoint extrapolation and no implicit unit conversion.</summary>
public sealed class CurveEnvelope
{
    private readonly double[] _times, _lower, _upper;
    public string Key { get; }
    public string Hash { get; }
    public IReadOnlyList<double> Times => _times;
    public CurveEnvelope(CurveDefinition curve, string unit)
    {
        ValidateDefinition(curve);
        var lower = FindChannel(curve, "Untergrenze");
        var upper = FindChannel(curve, "Obergrenze");
        ValidateChannels(curve, lower, upper, unit);
        _times = Enumerable.Range(0, curve.Count).Select(curve.TimeOf).ToArray();
        _lower = lower.Samples.ToArray(); _upper = upper.Samples.ToArray();
        for (var index = 0; index < _times.Length; index++) ValidatePoint(index);
        Key = curve.Key; Hash = curve.ContentHash();
    }
    private static void ValidateDefinition(CurveDefinition curve)
    {
        if (curve.Count is < 2 or > 10000) throw new ArgumentException("Grenzkurve benötigt 2 bis 10000 Stützpunkte.");
        if (curve.Quality is not null) throw new ArgumentException("Grenzkurve hat einen Qualitätshinweis und muss zuerst bereinigt werden.");
        if (curve.Times is null && (!double.IsFinite(curve.SampleRate) || curve.SampleRate <= 0)) throw new ArgumentException("Grenzkurve benötigt eine gültige Zeitbasis.");
    }
    private static CurveChannel FindChannel(CurveDefinition curve, string name)
        => curve.Channels.SingleOrDefault(curveChannel => curveChannel.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException($"Grenzkurvenkanal {name} fehlt.");
    private static void ValidateChannels(CurveDefinition curve, CurveChannel lower, CurveChannel upper, string unit)
    {
        if (lower.Unit != unit || upper.Unit != unit) throw new ArgumentException("Einheit der Grenzkurven passt nicht zur Aufnahme.");
        if (lower.Samples.Length != curve.Count || upper.Samples.Length != curve.Count || (curve.Times is { } samples && samples.Length != curve.Count))
            throw new ArgumentException("Grenzkurve hat inkonsistente Kanallängen.");
    }
    private void ValidatePoint(int pointIndex)
    {
        if (!double.IsFinite(_times[pointIndex]) || !double.IsFinite(_lower[pointIndex]) || !double.IsFinite(_upper[pointIndex]) || _lower[pointIndex] > _upper[pointIndex])
            throw new ArgumentException("Grenzkurve enthält ungültige Werte oder eine Untergrenze oberhalb der Obergrenze.");
        if (_times[pointIndex] < 0 || (pointIndex > 0 && _times[pointIndex] <= _times[pointIndex - 1])) throw new ArgumentException("Grenzkurvenzeiten müssen nichtnegativ und streng steigend sein.");
    }
    public (double Lower, double Upper) At(double time)
    {
        if (!double.IsFinite(time) || time < _times[0] || time > _times[^1]) throw new ArgumentException("Messpunkt außerhalb des definierten Grenzkurvenbereichs; keine Extrapolation.");
        return (Interpolate(_times, _lower, time), Interpolate(_times, _upper, time));
    }
    public IEnumerable<double> Checkpoints(double start, double end)
    {
        yield return start;
        var found = Array.BinarySearch(_times, start);
        var first = Math.Max(1, found >= 0 ? found : ~found);
        for (var index = first; index < _times.Length && _times[index - 1] <= end; index++)
        {
            if (_times[index] > start && _times[index] < end) yield return _times[index];
            foreach (var values in new[] { _lower, _upper })
            {
                if (Math.Sign(values[index - 1]) == Math.Sign(values[index])) continue;
                // Scaled weights avoid overflow when limits have opposite signs.
                var scale = Math.Max(Math.Abs(values[index - 1]), Math.Abs(values[index]));
                var left = Math.Abs(values[index - 1]) / scale; var right = Math.Abs(values[index]) / scale;
                var zero = _times[index - 1] + (_times[index] - _times[index - 1]) * (left / (left + right));
                if (zero > start && zero < end) yield return zero;
            }
        }
        yield return end;
    }
    public static double Interpolate(IReadOnlyList<double> times, IReadOnlyList<double> values, double time)
    {
        var low = 0; var high = times.Count - 1;
        while (high - low > 1) { var middle = (low + high) / 2; if (times[middle] <= time) low = middle; else high = middle; }
        var factor = Math.Clamp((time - times[low]) / (times[high] - times[low]), 0, 1);
        return values[low] * (1 - factor) + values[high] * factor;
    }
}

public sealed record TraceChannelDefinition(string Name, string Unit, CurveDefinition Limits);
public sealed record TraceEvaluation(WaveformSet Data, IReadOnlyList<EnvelopeTraceMetadata> Channels, bool Complete)
{
    public EnvelopeEarlyStop? EarlyStop { get; init; }
    public bool Finished { get; init; }
    public bool Passed => EarlyStop is null && Complete && Channels.All(envelopeTraceMetadata => envelopeTraceMetadata.ViolationCount == 0);
    public int Violations => Channels.Sum(envelopeTraceMetadata => envelopeTraceMetadata.ViolationCount);
}

/// <summary>One finite recording with a common receipt-time base for all channels.</summary>
internal sealed class EnvelopeRecording
{
    private readonly TraceChannelDefinition[] _channels;
    private readonly CurveEnvelope[] _envelopes;
    private readonly List<double> _times = [];
    private readonly List<double>[] _values;
    private readonly DateTimeOffset _start;
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedDuration;
    private readonly int _maxPoints;
    private readonly double _maxGap, _minimumDuration;
    private readonly bool _touchAllowed;
    private bool _faulted;
    private readonly double? _stopPercent, _duration;
    public bool HasViolation { get; private set; }
    public bool Ended => EarlyStop is not null || (_duration is { } duration && _times.Count > 1 && _times[^1] >= duration - TimeTolerance(duration));
    public EnvelopeEarlyStop? EarlyStop { get; private set; }
    private DateTimeOffset _lastPreview;
    public EnvelopeRecording(IReadOnlyList<TraceChannelDefinition> channels, DateTimeOffset start, int maxPoints, double maxGap, double minimumDuration, bool touchAllowed, double? stopPercent, double? duration)
    {
        ValidateChannels(channels);
        ValidateAcquisition(maxPoints, maxGap, minimumDuration);
        if (stopPercent is { } percent && (!double.IsFinite(percent) || percent < 0)) throw new ArgumentException("FAIL-Abbruchabweichung muss endlich und >= 0 sein.");
        _stopPercent = stopPercent;
        _channels = channels.ToArray(); _envelopes = channels.Select(traceChannelDefinition => new CurveEnvelope(traceChannelDefinition.Limits, traceChannelDefinition.Unit)).ToArray();
        ValidateDuration(duration, minimumDuration);
        _duration = duration;
        _values = channels.Select(_ => new List<double>()).ToArray(); _start = start;
        _maxPoints = maxPoints; _maxGap = maxGap; _minimumDuration = minimumDuration; _touchAllowed = touchAllowed;
    }
    private static void ValidateChannels(IReadOnlyList<TraceChannelDefinition> channels)
    {
        if (channels.Count is < 1 or > 8 || channels.Select(traceChannelDefinition => traceChannelDefinition.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != channels.Count || channels.Any(traceChannelDefinition => string.IsNullOrWhiteSpace(traceChannelDefinition.Name) || traceChannelDefinition.Name.Contains(" · ", StringComparison.Ordinal)))
            throw new ArgumentException("1 bis 8 eindeutig benannte Messkanäle erforderlich.");
    }
    private static void ValidateAcquisition(int maxPoints, double maxGap, double minimumDuration)
    {
        if (maxPoints is < 2 or > 100000 || !double.IsFinite(maxGap) || maxGap <= 0 || !double.IsFinite(minimumDuration) || minimumDuration < 0)
            throw new ArgumentException("Punktlimit 2..100000, maximale Lücke > 0 und Mindestdauer >= 0 angeben.");
    }
    private void ValidateDuration(double? duration, double minimumDuration)
    {
        if (duration is not { } value) return;
        if (!double.IsFinite(value) || value <= 0 || minimumDuration > value)
            throw new ArgumentException("Messdauer muss endlich und > 0 sein und mindestens der Mindestdauer entsprechen.");
        foreach (var envelope in _envelopes) { _ = envelope.At(0); _ = envelope.At(value); }
    }
    private static double TimeTolerance(double value) => Math.Max(1, value) * 1e-12;
    public void CheckUnits(IReadOnlyList<string> units)
    {
        if (units.Count != _channels.Length || units.Where((unit, index) => unit != _channels[index].Unit).Any()) throw new ArgumentException("Geräteeinheiten passen nicht zur Aufnahme; keine automatische Umrechnung.");
    }
    public void Invalidate() => _faulted = true;
    public bool IsPaused => _pausedAt.HasValue;

    public void SetPaused(bool paused, DateTimeOffset now)
    {
        if (paused)
            _pausedAt ??= now;
        else if (_pausedAt is { } pausedAt)
        {
            _pausedDuration += now - pausedAt;
            _pausedAt = null;
        }
    }

    public double Elapsed(DateTimeOffset now) => ((_pausedAt ?? now) - _start - _pausedDuration).TotalSeconds;
    public bool Add(double time, IReadOnlyList<double> values, DateTimeOffset now)
    {
        if (_duration is { } end && Math.Abs(time - end) <= TimeTolerance(end)) time = end;
        try { Validate(time, values); }
        catch { _faulted = true; throw; }
        _times.Add(time); for (var index = 0; index < values.Count; index++) _values[index].Add(values[index]);
        var alreadyFailed = HasViolation;
        CheckSegment();
        if (Ended || (HasViolation && !alreadyFailed)) return true;
        if (_times.Count > 2 && now - _lastPreview < TimeSpan.FromMilliseconds(250)) return false;
        _lastPreview = now; return true;
    }
    private void Validate(double time, IReadOnlyList<double> values)
    {
        if (Ended) throw new InvalidOperationException("Aufnahme ist bereits beendet.");
        if (_duration is { } duration && time > duration) throw new ArgumentException("Messzeit liegt nach der eingestellten Aufnahmedauer.");
        if (_faulted) throw new InvalidOperationException("Aufnahme ist nach einem Datenfehler ungültig; neu starten.");
        if (_times.Count >= _maxPoints) throw new InvalidOperationException("Maximale Messpunktzahl erreicht.");
        if (values.Count != _channels.Length || values.Any(value => !double.IsFinite(value)) || !double.IsFinite(time) || time < 0)
            throw new ArgumentException("Für jeden Kanal einen endlichen Messwert und eine nichtnegative Zeit angeben.");
        if (_times.Count > 0 && (time <= _times[^1] || time - _times[^1] > _maxGap + Math.Max(1, _maxGap) * 1e-12)) throw new ArgumentException("Zeitpunkte müssen streng steigen und dürfen die maximale Messlücke nicht überschreiten.");
        foreach (var envelope in _envelopes) _ = envelope.At(time);
    }
    public TraceEvaluation Evaluate(bool stopped)
    {
        var complete = stopped && EarlyStop is null && !_faulted && HasFullDuration();
        if (_times.Count == 0) return new(new([]), [], false);
        var grid = _envelopes.SelectMany(curveEnvelope => curveEnvelope.Times).Concat(EarlyStop is { } early ? new[] { early.Time } : []).Where(concat => concat >= _times[0] && concat <= _times[^1]).Concat(_times).Distinct().Order().ToArray();
        var waves = new List<Waveform>(); var metadata = new List<EnvelopeTraceMetadata>();
        for (var channel = 0; channel < _channels.Length; channel++) EvaluateChannel(channel, grid, complete, waves, metadata);
        return new(new(waves), metadata, complete) { EarlyStop = EarlyStop, Finished = Ended };
    }
    private void EvaluateChannel(int channel, double[] grid, bool complete, List<Waveform> waves, List<EnvelopeTraceMetadata> metadata)
    {
        var definition = _channels[channel]; var envelope = _envelopes[channel];
        var measured = new double[grid.Length]; var lower = new double[grid.Length]; var upper = new double[grid.Length];
        var violations = new List<double>(); var details = new List<EnvelopeViolation>(); double maximum = 0;
        for (var index = 0; index < grid.Length; index++)
        {
            measured[index] = _times.Count == 1 ? _values[channel][0] : CurveEnvelope.Interpolate(_times, _values[channel], grid[index]);
            (lower[index], upper[index]) = envelope.At(grid[index]);
            if (!Inside(measured[index], lower[index], upper[index]))
            {
                violations.Add(grid[index]); details.Add(new(grid[index], measured[index], lower[index], upper[index], Math.Max(0, Math.Max(lower[index] - measured[index], measured[index] - upper[index]))));
            }
            maximum = Math.Max(maximum, Math.Max(lower[index] - measured[index], measured[index] - upper[index]));
        }
        if (measured.Any(measured2 => !double.IsFinite(measured2)) || !double.IsFinite(maximum)) throw new ArgumentException("Wertebereich der Kurvenauswertung überschritten.");
        var info = new EnvelopeTraceMetadata(envelope.Key, envelope.Hash, definition.Name, _times.ToArray(), violations.ToArray(), violations.Count, maximum, complete) { Details = details, EarlyStop = EarlyStop, PlannedDuration = _duration };
        metadata.Add(info);
        waves.Add(new(definition.Name, definition.Unit, 0, _start, measured, grid, info.Encode()));
        waves.Add(new(definition.Name + " · Untergrenze (" + envelope.Key + ")", definition.Unit, 0, _start, lower, grid));
        waves.Add(new(definition.Name + " · Obergrenze (" + envelope.Key + ")", definition.Unit, 0, _start, upper, grid));
    }
    /// <summary>Inspect only the newly received segment, including hidden violations between samples.</summary>
    private void CheckSegment()
    {
        var start = _times.Count > 1 ? _times[^2] : _times[0]; var end = _times[^1];
        for (var channel = 0; channel < _channels.Length; channel++)
        {
            foreach (var time in _envelopes[channel].Checkpoints(start, end).Distinct().Order())
            {
                var value = _times.Count == 1 ? _values[channel][0] : CurveEnvelope.Interpolate(_times, _values[channel], time);
                var (lower, upper) = _envelopes[channel].At(time);
                if (!Inside(value, lower, upper)) HasViolation = true;
                if (_stopPercent is not { } percent || !BeyondStopLimit(value, lower, upper, percent)) continue;
                EarlyStop = new(percent, _channels[channel].Name, time, value, lower, upper);
                return;
            }
        }
    }
    private bool HasFullDuration()
    {
        if (_times.Count < 2) return false;
        if (_times[^1] - _times[0] + TimeTolerance(_minimumDuration) < _minimumDuration) return false;
        return _duration is not { } duration || (_times[0] <= TimeTolerance(duration) && _times[^1] >= duration - TimeTolerance(duration));
    }
    private static bool BeyondStopLimit(double value, double lower, double upper, double percent)
    {
        // Decimal configuration and interpolation may differ by a few binary rounding bits.
        var low = lower - Math.Abs(lower) * (percent / 100);
        var high = upper + Math.Abs(upper) * (percent / 100);
        var lowTolerance = Math.Max(Math.Abs(value), Math.Abs(lower)) * 1e-12;
        var highTolerance = Math.Max(Math.Abs(value), Math.Abs(upper)) * 1e-12;
        return value < low - lowTolerance || value > high + highTolerance;
    }
    private bool Inside(double value, double low, double high) => _touchAllowed ? value >= low && value <= high : value > low && value < high;
}

/// <summary>Run-owned, bounded recordings. A DUT cannot reuse another DUT's open capture.</summary>
public sealed class EnvelopeRecordings
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Owner, string Name), EnvelopeRecording> _recordings = [];
    public void Start(string owner, string name, IReadOnlyList<TraceChannelDefinition> channels, DateTimeOffset now, int maxPoints = 10000, double maxGap = 1, double minimumDuration = 0, bool touchAllowed = true, double? stopPercent = null, double? duration = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(name) || _recordings.ContainsKey((owner, name))) throw new InvalidOperationException("Aufnahmekennung fehlt oder ist bereits aktiv.");
            if (_recordings.Count >= 8) throw new InvalidOperationException("Maximal acht gleichzeitige Aufnahmen je Lauf.");
            _recordings.Add((owner, name), new(channels, now, maxPoints, maxGap, minimumDuration, touchAllowed, stopPercent, duration));
        }
    }
    public TraceEvaluation? Add(string owner, string name, double? time, IReadOnlyList<double> values, DateTimeOffset now)
    {
        lock (_gate)
        {
            var recording = Get(owner, name);
            if (recording.IsPaused) return null;
            return recording.Add(time ?? recording.Elapsed(now), values, now) ? recording.Evaluate(recording.Ended) : null;
        }
    }
    public void CheckUnits(string owner, string name, IReadOnlyList<string> units) { lock (_gate) Get(owner, name).CheckUnits(units); }
    public void SetPaused(string owner, string name, bool paused, DateTimeOffset now)
    {
        lock (_gate)
        {
            var recording = Get(owner, name);
            if (!recording.Ended) recording.SetPaused(paused, now);
        }
    }
    public bool IsPaused(string owner, string name) { lock (_gate) return Get(owner, name).IsPaused; }
    public bool IsEnded(string owner, string name) { lock (_gate) return Get(owner, name).Ended; }
    public bool HasViolation(string owner, string name) { lock (_gate) return Get(owner, name).HasViolation; }
    public void Invalidate(string owner, string name) { lock (_gate) { if (_recordings.TryGetValue((owner, name), out var recording)) recording.Invalidate(); } }
    public TraceEvaluation Stop(string owner, string name, bool complete = true)
    {
        lock (_gate) { var recording = Get(owner, name); _recordings.Remove((owner, name)); return recording.Evaluate(complete); }
    }
    private EnvelopeRecording Get(string owner, string name) => _recordings.GetValueOrDefault((owner, name)) ?? throw new InvalidOperationException("Aufnahme nicht aktiv oder gehört zu einem anderen Prüfling.");
    private static TraceEvaluation Incomplete(EnvelopeRecording recording)
    {
        try { return recording.Evaluate(false); } catch (ArgumentException) { return new(new([]), [], false); }
    }
    public IReadOnlyList<TraceEvaluation> CloseOwner(string owner)
    {
        lock (_gate)
        {
            var keys = _recordings.Keys.Where(namedEntry => namedEntry.Owner == owner).ToArray();
            var results = keys.Where(namedEntry => !_recordings[namedEntry].Ended).Select(namedEntry => Incomplete(_recordings[namedEntry])).ToArray();
            foreach (var key in keys) _recordings.Remove(key);
            return results;
        }
    }
}