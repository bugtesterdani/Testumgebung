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

public sealed record TraceChannelDefinition(string Name, string Unit, CurveDefinition? Limits)
{
    public CurveDefinition? AbortLimits { get; init; }
}
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
    private readonly SlopeLimits? _slope;
    private readonly FormulaLimits? _formula;
    private readonly Dictionary<double, (double Lower, double Upper)> _formulaBounds = [];
    private readonly List<double> _slopeTimes = [];
    private readonly List<double>[] _slopes;
    private readonly CurveEnvelope?[] _abortEnvelopes;
    private readonly List<double> _times = [];
    private readonly List<double>[] _values;
    private readonly DateTimeOffset _start;
    private DateTimeOffset? _pausedAt;
    private TimeSpan _pausedDuration;
    private readonly int _maxPoints;
    private readonly double _maxGap, _minimumDuration;
    private readonly bool _touchAllowed;
    private bool _faulted;
    private readonly StopMargin? _stopMargin;
    private readonly double? _duration;
    public bool HasViolation { get; private set; }
    public bool Ended => EarlyStop is not null || (_duration is { } duration && _times.Count > 1 && _times[^1] >= duration - TimeTolerance(duration));
    public EnvelopeEarlyStop? EarlyStop { get; private set; }
    private DateTimeOffset _lastPreview;
    public EnvelopeRecording(IReadOnlyList<TraceChannelDefinition> channels, DateTimeOffset start, int maxPoints, double maxGap, double minimumDuration, bool touchAllowed, double? stopPercent, double? duration, SlopeLimits? slope, FormulaLimits? formula, double? stopAbsolute)
    {
        ValidateChannels(channels);
        ValidateAcquisition(maxPoints, maxGap, minimumDuration);
        _stopMargin = StopMargin.Create(stopPercent, stopAbsolute);
        _slope = slope;
        if (formula is not null && slope is not null)
            throw new ArgumentException("Für die Aufnahme entweder Formelgrenzen oder Steigungsgrenzen wählen.");
        _formula = formula;
        slope?.Validate(maxPoints);
        _channels = channels.ToArray();
        _envelopes = slope is null && formula is null ? channels.Select(channel => new CurveEnvelope(
            channel.Limits ?? throw new ArgumentException("Je Messkanal eine Grenzkurve angeben."), channel.Unit)).ToArray() : [];
        _abortEnvelopes = channels.Select(channel => slope is null && formula is null && channel.AbortLimits is { } limits
            ? new CurveEnvelope(limits, channel.Unit) : null).ToArray();
        ValidateDuration(duration, minimumDuration);
        _duration = duration;
        _values = channels.Select(_ => new List<double>()).ToArray(); _start = start;
        _slopes = channels.Select(_ => new List<double>()).ToArray();
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
        foreach (var envelope in AllEnvelopes()) { _ = envelope.At(0); _ = envelope.At(value); }
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
        try
        {
            Validate(time, values);
            if (_formula is not null) _formulaBounds.Add(time, _formula.At(time));
        }
        catch { _faulted = true; throw; }
        _times.Add(time); for (var index = 0; index < values.Count; index++) _values[index].Add(values[index]);
        var alreadyFailed = HasViolation;
        if (_formula is not null) CheckFormulaPoint(time, values);
        else if (_slope is null) CheckSegment();
        else
        {
            if (!_slope.IsWindowEnd(_times.Count) && !Ended) return false;
            try { CheckSlope(_slope); }
            catch { _faulted = true; throw; }
        }
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
        foreach (var envelope in AllEnvelopes()) _ = envelope.At(time);
    }
    public TraceEvaluation Evaluate(bool stopped)
    {
        var complete = stopped && EarlyStop is null && !_faulted && HasFullDuration();
        if (_slope is { } slope) return EvaluateSlopes(slope, complete);
        if (_times.Count == 0) return new(new([]), [], false);
        var grid = _formula is not null ? _times.ToArray() : AllEnvelopes().SelectMany(curveEnvelope => curveEnvelope.Times).Concat(EarlyStop is { } early ? new[] { early.Time } : []).Where(concat => concat >= _times[0] && concat <= _times[^1]).Concat(_times).Distinct().Order().ToArray();
        var waves = new List<Waveform>(); var metadata = new List<EnvelopeTraceMetadata>();
        for (var channel = 0; channel < _channels.Length; channel++)
        {
            EvaluateChannel(channel, grid, complete, waves, metadata);
            AppendAbortLines(channel, grid, waves);
        }
        return new(new(waves), metadata, complete) { EarlyStop = EarlyStop, Finished = Ended };
    }
    private void CheckSlope(SlopeLimits limits)
    {
        if (!limits.IsWindowEnd(_times.Count)) return;
        var first = _times.Count - limits.Points;
        var elapsed = _times[^1] - _times[first];
        var slopes = _values.Select(values => (values[^1] - values[first]) / elapsed).ToArray();
        if (slopes.Any(value => !double.IsFinite(value) ||
            !double.IsFinite(Math.Max(limits.Minimum - value, value - limits.Maximum))))
            throw new ArgumentException("Wertebereich der Steigungsauswertung überschritten.");

        var time = _times[^1];
        _slopeTimes.Add(time);
        for (var channel = 0; channel < slopes.Length; channel++)
        {
            var value = slopes[channel];
            _slopes[channel].Add(value);
            HasViolation |= !Inside(value, limits.Minimum, limits.Maximum);
            EarlyStop ??= _stopMargin?.Check(_channels[channel].Name, time, value, limits.Minimum, limits.Maximum);
        }
    }

    private TraceEvaluation EvaluateSlopes(SlopeLimits limits, bool complete)
    {
        if (_slopeTimes.Count == 0) return new(new([]), [], false) { Finished = Ended };
        var times = _slopeTimes.ToArray();
        var lower = Enumerable.Repeat(limits.Minimum, times.Length).ToArray();
        var upper = Enumerable.Repeat(limits.Maximum, times.Length).ToArray();
        var waves = new List<Waveform>();
        var metadata = new List<EnvelopeTraceMetadata>();
        for (var channel = 0; channel < _channels.Length; channel++)
        {
            var definition = _channels[channel];
            var values = _slopes[channel].ToArray();
            var details = new List<EnvelopeViolation>();
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (!Inside(value, limits.Minimum, limits.Maximum))
                    details.Add(new(times[index], value, limits.Minimum, limits.Maximum,
                        Math.Max(0, Math.Max(limits.Minimum - value, value - limits.Maximum))));
            }

            var info = new EnvelopeTraceMetadata("Steigung", "", definition.Name, times,
                details.Select(detail => detail.Time).ToArray(), details.Count,
                details.Count == 0 ? 0 : details.Max(detail => detail.Deviation), complete)
                { Details = details, EarlyStop = EarlyStop, PlannedDuration = _duration };
            metadata.Add(info);
            var unit = definition.Unit + "/s";
            waves.Add(new(definition.Name, unit, 0, _start, values, times, info.Encode()));
            waves.Add(new(definition.Name + " · Untergrenze (Steigung)", unit, 0, _start, lower, times));
            waves.Add(new(definition.Name + " · Obergrenze (Steigung)", unit, 0, _start, upper, times));
        }
        return new(new(waves), metadata, complete) { EarlyStop = EarlyStop, Finished = Ended };
    }

    private void EvaluateChannel(int channel, double[] grid, bool complete, List<Waveform> waves, List<EnvelopeTraceMetadata> metadata)
    {
        var definition = _channels[channel];
        var envelope = _formula is null ? _envelopes[channel] : null;
        var key = envelope?.Key ?? "Formelgrenzen";
        var measured = new double[grid.Length]; var lower = new double[grid.Length]; var upper = new double[grid.Length];
        var violations = new List<double>(); var details = new List<EnvelopeViolation>(); double maximum = 0;
        for (var index = 0; index < grid.Length; index++)
        {
            measured[index] = envelope is null ? _values[channel][index] : MeasuredAt(channel, grid[index]);
            (lower[index], upper[index]) = envelope is null ? _formulaBounds[grid[index]] : envelope.At(grid[index]);
            if (!Inside(measured[index], lower[index], upper[index]))
            {
                violations.Add(grid[index]); details.Add(new(grid[index], measured[index], lower[index], upper[index], Math.Max(0, Math.Max(lower[index] - measured[index], measured[index] - upper[index]))));
            }
            maximum = Math.Max(maximum, Math.Max(lower[index] - measured[index], measured[index] - upper[index]));
        }
        if (measured.Any(measured2 => !double.IsFinite(measured2)) || !double.IsFinite(maximum)) throw new ArgumentException("Wertebereich der Kurvenauswertung überschritten.");
        var info = new EnvelopeTraceMetadata(key, envelope?.Hash ?? "", definition.Name, _times.ToArray(), violations.ToArray(), violations.Count, maximum, complete) { Details = details, EarlyStop = EarlyStop, PlannedDuration = _duration };
        metadata.Add(info);
        waves.Add(new(definition.Name, definition.Unit, 0, _start, measured, grid, info.Encode()));
        waves.Add(new(definition.Name + " · Untergrenze (" + key + ")", definition.Unit, 0, _start, lower, grid));
        waves.Add(new(definition.Name + " · Obergrenze (" + key + ")", definition.Unit, 0, _start, upper, grid));
    }
    private IEnumerable<CurveEnvelope> AllEnvelopes() => _envelopes.Concat(_abortEnvelopes.OfType<CurveEnvelope>());

    private void AppendAbortLines(int channel, double[] grid, List<Waveform> waves)
    {
        if (_abortEnvelopes[channel] is not { } limits) return;
        var definition = _channels[channel];
        var samples = grid.Select(limits.At).ToArray();
        waves.Add(new(definition.Name + " · Abbruch-Untergrenze (" + limits.Key + ")", definition.Unit, 0, _start,
            samples.Select(bounds => bounds.Lower).ToArray(), grid));
        waves.Add(new(definition.Name + " · Abbruch-Obergrenze (" + limits.Key + ")", definition.Unit, 0, _start,
            samples.Select(bounds => bounds.Upper).ToArray(), grid));
    }

    private void CheckFormulaPoint(double time, IReadOnlyList<double> values)
    {
        var (lower, upper) = _formulaBounds[time];
        for (var channel = 0; channel < values.Count; channel++)
        {
            var value = values[channel];
            HasViolation |= !Inside(value, lower, upper);
            EarlyStop = _stopMargin?.Check(_channels[channel].Name, time, value, lower, upper);
            if (EarlyStop is not null) return;
        }
    }

    /// <summary>Inspect only the newly received segment, including hidden violations between samples.</summary>
    private void CheckSegment()
    {
        var start = _times.Count > 1 ? _times[^2] : _times[0];
        var end = _times[^1];
        for (var channel = 0; channel < _channels.Length; channel++)
        {
            var normal = _envelopes[channel];
            if (!HasViolation)
                HasViolation = normal.Checkpoints(start, end).Any(time => IsNormalViolation(normal, channel, time));
            EarlyStop = FindEarlyStop(channel, start, end);
            if (EarlyStop is null) continue;
            HasViolation = true;
            return;
        }
    }

    private bool IsNormalViolation(CurveEnvelope envelope, int channel, double time)
    {
        var (lower, upper) = envelope.At(time);
        return !Inside(MeasuredAt(channel, time), lower, upper);
    }

    private EnvelopeEarlyStop? FindEarlyStop(int channel, double start, double end)
    {
        var abort = _abortEnvelopes[channel];
        var margin = abort is null ? _stopMargin : new StopMargin(0, false);
        if (margin is not { } stop) return null;
        var limits = abort ?? _envelopes[channel];
        foreach (var time in limits.Checkpoints(start, end).Distinct().Order())
        {
            var value = MeasuredAt(channel, time);
            var (lower, upper) = limits.At(time);
            if (stop.Check(_channels[channel].Name, time, value, lower, upper) is { } trigger)
                return trigger with { AbortCurve = abort?.Key };
        }
        return null;
    }

    private double MeasuredAt(int channel, double time) => _times.Count == 1 ? _values[channel][0]
        : CurveEnvelope.Interpolate(_times, _values[channel], time);

    private bool HasFullDuration()
    {
        if (_times.Count < 2 || (_slope is not null && _slopeTimes.Count == 0)) return false;
        if (_times[^1] - _times[0] + TimeTolerance(_minimumDuration) < _minimumDuration) return false;
        return _duration is not { } duration || (_times[0] <= TimeTolerance(duration) && _times[^1] >= duration - TimeTolerance(duration));
    }
    private bool Inside(double value, double low, double high) => _touchAllowed ? value >= low && value <= high : value > low && value < high;
}

/// <summary>Run-owned, bounded recordings. A DUT cannot reuse another DUT's open capture.</summary>
public sealed class EnvelopeRecordings
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Owner, string Name), EnvelopeRecording> _recordings = [];
    public void Start(string owner, string name, IReadOnlyList<TraceChannelDefinition> channels, DateTimeOffset now, int maxPoints = 10000, double maxGap = 1, double minimumDuration = 0, bool touchAllowed = true, double? stopPercent = null, double? duration = null, SlopeLimits? slope = null, FormulaLimits? formula = null, double? stopAbsolute = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(name) || _recordings.ContainsKey((owner, name))) throw new InvalidOperationException("Aufnahmekennung fehlt oder ist bereits aktiv.");
            if (_recordings.Count >= 8) throw new InvalidOperationException("Maximal acht gleichzeitige Aufnahmen je Lauf.");
            _recordings.Add((owner, name), new(channels, now, maxPoints, maxGap, minimumDuration, touchAllowed, stopPercent, duration, slope, formula, stopAbsolute));
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