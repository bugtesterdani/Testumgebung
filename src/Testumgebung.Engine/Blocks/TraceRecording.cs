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
    private readonly bool _touchAllowed;
    private readonly double? _stopPercent;
    private readonly SlopeLimits? _slope;
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
        Maximum = (int)maximum; MaxGap = p.Number("MaxLuecke") ?? 0;
        _slope = ReadSlopeLimits(p, Maximum);
        if (_slope is null && (_names.Length != _curves.Length || _curves.Any(string.IsNullOrWhiteSpace)))
            throw new BlockConfigurationException("Je Messkanal eine Grenzkurve angeben.");
        _abortCurves = ReadAbortCurves(_slope is null ? p.Text(TraceBlocks.AbortCurvesParameter) : "", _names.Length);
        var duration = p.Number("Dauer") ?? 0;
        if (!double.IsFinite(duration) || duration < 0) throw new BlockConfigurationException("Messdauer muss endlich und >= 0 sein.");
        Duration = duration == 0 ? null : duration;
        _minimumDuration = p.Number("MinDauer") ?? 0; _touchAllowed = p.Bool("Beruehren");
        _stopPercent = p.Bool("FailAbbruch") ? p.Number("AbweichungProzent") ?? 0 : null;
    }
    public void Begin(IStepContext ctx)
    {
        if (ctx.IsOperatorAborted) throw new InvalidOperationException("Nach Werkerabbruch keine Aufnahme beginnen.");
        var channels = _names.Select((name, index) => new TraceChannelDefinition(name, _units[index], _slope is null ? Resolve(ctx, _curves[index]) : null)
        {
            AbortLimits = _abortCurves[index].Length == 0 ? null : Resolve(ctx, _abortCurves[index])
        }).ToArray();
        ctx.Recordings.Start(ctx.ProcessOwner, Name, channels, ctx.Clock.Now, Maximum, MaxGap, _minimumDuration, _touchAllowed, _stopPercent, Duration, _slope);
    }
    private static SlopeLimits? ReadSlopeLimits(BlockParameters p, int maximum)
    {
        var mode = p.Choice(TraceBlocks.EvaluationParameter);
        if (mode == TraceBlocks.ValueBand) return null;
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
        return new(window, (int)points, low, high);
    }

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
    public TraceSampleReader(BlockParameters p)
    {
        _devices = p.Choice("Quelle") == "Geräte"; _resources = TraceRecordingSettings.Split(p.Text("Ressourcen"), ',');
        try { _expressions = _devices ? [] : TraceRecordingSettings.Split(p.Text("Werte"), ';').Select(Expression.Parse).ToArray(); }
        catch (ExpressionException ex) { throw new BlockConfigurationException("Wertausdrücke: " + ex.Message); }
    }
    public async Task<TraceEvaluation?> AppendAsync(IStepContext ctx, string name, double? time, double? maxReadSeconds = null)
    {
        try
        {
            var requested = ctx.Clock.Now;
            var values = new List<double>();
            if (_devices)
            {
                var units = new List<string>();
                foreach (var resource in _resources)
                {
                    var reading = await ctx.UseDeviceAsync(resource, "Verlauf lesen", async (adapter, channel, token) =>
                        (Value: await adapter.ReadAsync(channel, token).ConfigureAwait(false), Unit: adapter.GetCapabilities(channel).Unit)).ConfigureAwait(false);
                    values.Add(reading.Value); units.Add(reading.Unit);
                }
                ctx.Recordings.CheckUnits(ctx.ProcessOwner, name, units);
            }
            else foreach (var expression in _expressions) values.Add(BlockCatalog.ToDouble(expression.Evaluate(new ContextResolver(ctx)), "Messwert"));
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (maxReadSeconds is { } limit && (ctx.Clock.Now - requested).TotalSeconds > limit) throw new TimeoutException("Messgeräteabfrage überschreitet die maximale Abfragedauer.");
            return ctx.Recordings.Add(ctx.ProcessOwner, name, time, values, ctx.Clock.Now);
        }
        catch { ctx.Recordings.Invalidate(ctx.ProcessOwner, name); throw; }
    }
}

/// <summary>Software-paced sampling, with explicit first/final trigger positions and no run-wide cancellation.</summary>
internal sealed class TimedTraceAction : IStepAction
{
    private readonly TraceRecordingSettings _settings;
    private readonly TraceSampleReader _reader;
    private readonly double _interval, _duration;
    private readonly string? _target;
    private readonly bool _exitDut;
    public TimedTraceAction(TraceRecordingSettings settings, TraceSampleReader reader, double interval, string? target, bool exitDut)
    {
        _settings = settings; _reader = reader; _target = target; _interval = interval; _exitDut = exitDut;
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
            return TraceBlocks.Outcome(result, true, _exitDut);
        }
        catch { ctx.Recordings.Invalidate(ctx.ProcessOwner, _settings.Name); throw; }
    }
}
