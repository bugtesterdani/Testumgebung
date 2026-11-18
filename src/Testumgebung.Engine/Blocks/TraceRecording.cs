using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

/// <summary>Shared settings for manual point capture and the timed acquisition block.</summary>
internal sealed class TraceRecordingSettings
{
    private readonly string[] _names, _units, _curves, _abortCurves;
    private readonly double _minimumDuration;
    private readonly Expression? _minimumPoints;
    private readonly Expression? _targetMinimum, _targetMaximum, _targetPoints;
    private readonly Expression? _safetyMinimum, _safetyMaximum;
    private readonly bool _touchAllowed, _targetEnabled;
    private readonly double? _stopPercent, _stopAbsolute;
    private readonly SlopeLimits? _slope;
    private readonly FormulaLimits? _formula;
    public string Name { get; }
    public int Maximum { get; }
    public double MaxGap { get; }
    public double? Duration { get; }
    public TraceRecordingSettings(BlockParameters p)
    {
        Name = p.Text("Aufnahme"); _names = Split(p.Text("Kanaele"), ','); _units = Split(p.Text("Einheiten"), ','); _curves = Split(p.Text("Grenzkurven"), ',');
        if (_names.Length != _units.Length) throw new BlockConfigurationException("Je Messkanal eine Einheit angeben.");
        var maximum = p.Number("MaxPunkte") ?? 0;
        if (maximum != Math.Truncate(maximum) || maximum is < 2 or > 100000) throw new BlockConfigurationException("Ganzzahliges Punktlimit 2..100000 angeben.");
        _minimumPoints = p.Expr("MinPunkte");
        Maximum = (int)maximum; MaxGap = p.Number("MaxLuecke") ?? 0;
        var mode = p.Choice(TraceBlocks.EvaluationParameter);
        _slope = ReadSlopeLimits(p, Maximum, mode);
        if (mode == TraceBlocks.FormulaBand) _formula = ReadFormulaLimits(p);
        if (mode == TraceBlocks.ValueBand && (_names.Length != _curves.Length || _curves.Any(string.IsNullOrWhiteSpace)))
            throw new BlockConfigurationException("Je Messkanal eine Grenzkurve angeben.");
        _abortCurves = ReadAbortCurves(mode == TraceBlocks.ValueBand ? p.Text(TraceBlocks.AbortCurvesParameter) : "", _names.Length);
        var duration = p.Number("Dauer") ?? 0;
        if (!double.IsFinite(duration) || duration < 0) throw new BlockConfigurationException("Messdauer muss endlich und >= 0 sein.");
        Duration = duration == 0 ? null : duration;
        _minimumDuration = p.Number("MinDauer") ?? 0; _touchAllowed = p.Bool("Beruehren");
        (_stopPercent, _stopAbsolute) = ReadStopMargins(p);
        _targetEnabled = p.Raw.ContainsKey(TraceBlocks.TargetEnabledParameter) ? p.Bool(TraceBlocks.TargetEnabledParameter)
            : p.Optional("ZielbandMin") is not null || p.Optional("ZielbandMax") is not null;
        _targetMinimum = _targetEnabled ? p.Expr("ZielbandMin") : null;
        _targetMaximum = _targetEnabled ? p.Expr("ZielbandMax") : null;
        _targetPoints = _targetEnabled ? p.Expr("ZielbandPunkte") : null;
        _safetyMinimum = p.Expr("SicherheitMin");
        _safetyMaximum = p.Expr("SicherheitMax");
    }
    public void Begin(IStepContext ctx)
    {
        if (ctx.IsOperatorAborted) throw new InvalidOperationException("Nach Werkerabbruch keine Aufnahme beginnen.");
        var resolver = new ContextResolver(ctx);
        var safetyMin = _safetyMinimum is null ? (double?)null : BlockCatalog.ToDouble(_safetyMinimum.Evaluate(resolver), "Sicherheitsuntergrenze");
        var safetyMax = _safetyMaximum is null ? (double?)null : BlockCatalog.ToDouble(_safetyMaximum.Evaluate(resolver), "Sicherheitsobergrenze");
        var channels = _names.Select((name, index) => new TraceChannelDefinition(name, _units[index], _slope is null && _formula is null ? Resolve(ctx, _curves[index]) : null)
        {
            AbortLimits = _abortCurves[index].Length == 0 ? null : Resolve(ctx, _abortCurves[index]),
            SafetyMinimum = safetyMin, SafetyMaximum = safetyMax
        }).ToArray();
        var minimum = _minimumPoints is null ? 2 : BlockCatalog.ToDouble(_minimumPoints.Evaluate(new ContextResolver(ctx)), "Mindestanzahl");
        if (minimum < 2 || minimum > Maximum || minimum != Math.Truncate(minimum))
            throw new BlockConfigurationException("Mindestanzahl gültiger Messpunkte muss ganzzahlig zwischen 2 und dem Punktlimit liegen.");
        ctx.Recordings.Start(ctx.ProcessOwner, Name, channels, ctx.Clock.Now, Maximum, MaxGap, _minimumDuration, _touchAllowed, _stopPercent, Duration, _slope, _formula, _stopAbsolute, (int)minimum, ResolveTarget(ctx));
    }
    private TraceTargetBand? ResolveTarget(IStepContext ctx)
    {
        if (!_targetEnabled) return null;
        if (_targetMinimum is null || _targetMaximum is null)
            throw new BlockConfigurationException("Beide Zielbandgrenzen angeben.");
        var resolver = new ContextResolver(ctx);
        return new(BlockCatalog.ToDouble(_targetMinimum.Evaluate(resolver), "Zielband-Untergrenze"),
            BlockCatalog.ToDouble(_targetMaximum.Evaluate(resolver), "Zielband-Obergrenze"), ResolveTargetPoints(resolver));
    }

    private int ResolveTargetPoints(ContextResolver resolver)
    {
        var count = _targetPoints is null ? 10 : BlockCatalog.ToDouble(_targetPoints.Evaluate(resolver), "Zielbandpunkte");
        if (count < 2 || count > Maximum || count != Math.Truncate(count))
            throw new BlockConfigurationException("Zielbandpunkte müssen ganzzahlig zwischen 2 und dem Punktlimit liegen.");
        return (int)count;
    }

    private static (double? Percent, double? Absolute) ReadStopMargins(BlockParameters p)
    {
        if (!p.Bool(TraceBlocks.StopEnabledParameter)) return (null, null);
        var absolute = p.Choice(TraceBlocks.StopMarginModeParameter) == TraceBlocks.AbsoluteMargin;
        var value = p.Number(absolute ? TraceBlocks.AbsoluteMarginParameter : TraceBlocks.PercentMarginParameter) ?? 0;
        if (!double.IsFinite(value) || value < 0)
            throw new BlockConfigurationException("FAIL-Abbruchabweichung muss endlich und >= 0 sein.");
        return absolute ? (null, value) : (value, null);
    }

    private static FormulaLimits ReadFormulaLimits(BlockParameters p)
    {
        try { return new(p.Text(TraceBlocks.LowerFormulaParameter), p.Text(TraceBlocks.UpperFormulaParameter)); }
        catch (ExpressionException exception) { throw new BlockConfigurationException(exception.Message); }
    }

    private static SlopeLimits? ReadSlopeLimits(BlockParameters p, int maximum, string mode)
    {
        if (mode is TraceBlocks.ValueBand or TraceBlocks.FormulaBand) return null;
        var points = mode == TraceBlocks.PairSlope ? 2 : p.Number("Steigungsfenster") ?? 0;
        if (points < 2 || points > maximum || points != Math.Truncate(points))
            throw new BlockConfigurationException("Ganzzahlige Fenstergröße zwischen 2 und dem Punktlimit angeben.");
        var minimum = p.Number("SteigungMin");
        var upper = p.Number("SteigungMax");
        if (minimum is not { } low || upper is not { } high || !double.IsFinite(low) || !double.IsFinite(high) || low > high)
            throw new BlockConfigurationException("Endliche Steigungsgrenzen mit Minimum ≤ Maximum angeben.");
        var window = mode switch
        {
            TraceBlocks.PairSlope => SlopeWindowMode.Consecutive,
            TraceBlocks.DisjointSlope => SlopeWindowMode.Disjoint,
            _ => SlopeWindowMode.Rolling
        };
        return ReadSlopeSuppression(p, maximum, new(window, (int)points, low, high));
    }

    private static SlopeLimits ReadSlopeSuppression(BlockParameters p, int maximum, SlopeLimits limits)
    {
        var count = p.Number("SteigungAnlaufAnzahl") ?? 0;
        if (count < 0 || count > maximum || count != Math.Truncate(count))
            throw new BlockConfigurationException("Anlaufausnahmen müssen ganzzahlig zwischen 0 und dem Punktlimit liegen.");
        var configured = limits with
        {
            InitialExceptionCount = (int)count,
            ExceptionDirection = ReadExceptionDirection(p),
            InitialExceptionAbove = p.Number("SteigungAnlaufSchwelle") ?? 0,
            PositiveThreshold = p.Number("SteigungSchwellePositiv") ?? 0,
            NegativeThreshold = p.Number("SteigungSchwelleNegativ") ?? 0
        };
        try { configured.ValidateThresholdRange(p.Bool("Beruehren")); }
        catch (ArgumentException error) { throw new BlockConfigurationException(error.Message); }
        return configured;
    }

    private static SlopeExceptionDirection ReadExceptionDirection(BlockParameters p)
        => p.Choice("SteigungAnlaufrichtung") switch
        {
            "Fallend" => SlopeExceptionDirection.Falling,
            "Beide" => SlopeExceptionDirection.Both,
            _ => SlopeExceptionDirection.Rising
        };

    private static string[] ReadAbortCurves(string value, int channelCount)
    {
        if (string.IsNullOrWhiteSpace(value)) return Enumerable.Repeat("", channelCount).ToArray();
        var keys = Split(value, ',');
        if (keys.Length != channelCount)
            throw new BlockConfigurationException("Je Messkanal eine Abbruchgrenzkurve oder einen leeren Eintrag angeben.");
        return keys;
    }

    private static CurveDefinition Resolve(IStepContext ctx, string key)
    {
        var separator = key.LastIndexOf('@'); var id = separator < 0 ? key : key[..separator]; var version = separator < 0 ? null : key[(separator + 1)..];
        return ctx.FindCurve(id, version) ?? throw new BlockConfigurationException($"Grenzkurve {key} fehlt im Prüfprogramm.");
    }
    internal static string[] Split(string value, char separator) => value.Split(separator, StringSplitOptions.TrimEntries);
}

/// <summary>Read all channel values using the same device locking and units checks as manual points.</summary>
internal sealed class TraceSampleReader
{
    private readonly bool _devices;
    private readonly string[] _resources;
    private readonly Expression[] _expressions;
    private readonly Expression? _attempts;
    public TraceSampleReader(BlockParameters p)
    {
        _attempts = p.Expr("Messversuche");
        _devices = p.Choice("Quelle") == "Geräte"; _resources = TraceRecordingSettings.Split(p.Text("Ressourcen"), ',');
        try { _expressions = _devices ? [] : TraceRecordingSettings.Split(p.Text("Werte"), ';').Select(Expression.Parse).ToArray(); }
        catch (ExpressionException ex) { throw new BlockConfigurationException("Wertausdrücke: " + ex.Message); }
    }
    public async Task<TraceEvaluation?> AppendAsync(IStepContext ctx, string name, double? time, double? maxReadSeconds = null)
    {
        try
        {
            var resolver = new ContextResolver(ctx);
            var count = _attempts is null ? 1 : BlockCatalog.ToDouble(_attempts.Evaluate(resolver), "Messversuche");
            if (count < 1 || count > 10 || count != Math.Truncate(count))
                throw new BlockConfigurationException("Messversuche müssen ganzzahlig zwischen 1 und 10 liegen.");
            var values = await ReadWithRetriesAsync(ctx, resolver, name, (int)count, maxReadSeconds).ConfigureAwait(false);
            return ctx.Recordings.Add(ctx.ProcessOwner, name, time, values, ctx.Clock.Now);
        }
        catch { ctx.Recordings.Invalidate(ctx.ProcessOwner, name); throw; }
    }

    private async Task<double[]> ReadWithRetriesAsync(IStepContext ctx, ContextResolver resolver, string name, int attempts, double? maxReadSeconds)
    {
        var started = ctx.Clock.Now;
        var source = _devices ? string.Join(", ", _resources) : "Wertausdrücke";
        for (var attempt = 1; ; attempt++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                var values = _devices ? await ReadDevicesAsync(ctx, name).ConfigureAwait(false)
                    : _expressions.Select(expression => BlockCatalog.ToDouble(expression.Evaluate(resolver), "Messwert")).ToArray();
                ctx.CancellationToken.ThrowIfCancellationRequested();
                if (ReadTimeExceeded(ctx, started, maxReadSeconds))
                    throw new TimeoutException($"Messgeräteabfrage ({source}) überschreitet die maximale Abfragedauer.");
                return values;
            }
            catch (Exception error) when (error is SampleReadException or ResourceTimeoutException || (!_devices && error is ExpressionException or BlockConfigurationException))
            {
                if (attempt == attempts || ReadTimeExceeded(ctx, started, maxReadSeconds))
                    throw new InvalidOperationException($"Messung ({source}) nach {attempt} von maximal {attempts} Versuch(en) fehlgeschlagen: {error.Message}", error);
            }
        }
    }

    private async Task<double[]> ReadDevicesAsync(IStepContext ctx, string name)
    {
        var values = new double[_resources.Length];
        var units = new string[_resources.Length];
        for (var index = 0; index < _resources.Length; index++)
        {
            var resource = _resources[index];
            var reading = await ctx.UseDeviceAsync(resource, "Verlauf lesen", async (adapter, channel, token) =>
            {
                double value;
                try { value = await adapter.ReadAsync(channel, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException and not NotSupportedException)
                { throw new SampleReadException($"Gerät „{resource}“: {error.Message}", error); }
                if (!double.IsFinite(value))
                    throw new SampleReadException($"Gerät „{resource}“ liefert keinen endlichen Messwert.");
                return (Value: value, Unit: adapter.GetCapabilities(channel).Unit);
            }).ConfigureAwait(false);
            values[index] = reading.Value;
            units[index] = reading.Unit;
        }
        ctx.Recordings.CheckUnits(ctx.ProcessOwner, name, units);
        return values;
    }

    private static bool ReadTimeExceeded(IStepContext ctx, DateTimeOffset started, double? limit)
        => limit is { } seconds && (ctx.Clock.Now - started).TotalSeconds > seconds;

    private sealed class SampleReadException(string message, Exception? inner = null) : Exception(message, inner);
}

/// <summary>Software-paced sampling, with explicit first/final trigger positions and no run-wide cancellation.</summary>
internal sealed class TimedTraceAction : IStepAction
{
    private readonly TraceRecordingSettings _settings;
    private readonly TraceSampleReader _reader;
    private readonly double _interval, _duration;
    private readonly string? _target;
    private readonly bool _exitDut, _exitOnEnd;
    public TimedTraceAction(TraceRecordingSettings settings, TraceSampleReader reader, double interval, string? target, bool exitDut, bool exitOnEnd)
    {
        _settings = settings; _reader = reader; _target = target; _interval = interval; _exitDut = exitDut; _exitOnEnd = exitOnEnd;
        _duration = settings.Duration ?? throw new BlockConfigurationException("Zeitgesteuerte Messung benötigt eine Dauer > 0.");
        if (!double.IsFinite(interval) || interval < 1e-7 || _duration > TimeSpan.MaxValue.TotalSeconds)
            throw new BlockConfigurationException("Endliche Dauer und Abfrageintervall >= 0,0000001 s angeben.");
        if (Math.Ceiling(_duration / interval) + 1 > settings.Maximum)
            throw new BlockConfigurationException("Abfragetakt und Dauer überschreiten das eingestellte Punktlimit.");
    }
    public async Task<StepOutcome> ExecuteAsync(IStepContext ctx)
    {
        TraceBlocks.ValidateTarget(ctx, _target); _settings.Begin(ctx);
        try
        {
            var started = ctx.Clock.Now; double time = 0;
            while (true)
            {
                var remaining = TimeSpan.FromSeconds(time) - (ctx.Clock.Now - started);
                await ctx.Clock.Delay(remaining, ctx.CancellationToken).ConfigureAwait(false);
                var requested = ctx.Clock.Now;
                if ((requested - started).TotalSeconds - time > _settings.MaxGap) throw new TimeoutException("Abfragetakt zu stark überschritten.");
                var preview = await _reader.AppendAsync(ctx, _settings.Name, time, _settings.MaxGap).ConfigureAwait(false);
                if (preview is not null) ctx.ReportTrace(preview.Data);
                if (ctx.Recordings.IsEnded(ctx.ProcessOwner, _settings.Name)) break;
                time = Math.Min(_duration, Math.Max(time + _interval, (ctx.Clock.Now - started).TotalSeconds));
            }
            var result = ctx.Recordings.Stop(ctx.ProcessOwner, _settings.Name, !ctx.IsOperatorAborted);
            TraceBlocks.WriteDetails(ctx, _target, result);
            return TraceBlocks.Outcome(result, true, _exitDut) with { ExitDut = _exitOnEnd || (_exitDut && result.EarlyStop is not null) };
        }
        catch { ctx.Recordings.Invalidate(ctx.ProcessOwner, _settings.Name); throw; }
    }
}
