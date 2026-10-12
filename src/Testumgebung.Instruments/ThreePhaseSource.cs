using Testumgebung.Contracts;

namespace Testumgebung.Instruments;

/// <summary>
/// Dreiphasige AC-Versorgung aus drei einphasigen Quellen (L1, L2, L3). Die AC6800B ist einphasig; für dreiphasige Prüflinge
/// werden drei Geräte zu einem logischen Gerät zusammengefasst (Abschnitt 10, ADR 0009 offener Punkt). Kanalnamen mit Präfix:
/// <c>L1:VOLT</c>, <c>L2:FREQ</c>, …; ohne Präfix oder mit <c>ALLE:</c> wird auf alle drei Phasen gewirkt (Schreiben an alle,
/// Lesen von L1). Ressource: drei durch „;“ getrennte Adressen; Phasenlage über Optionen informativ.
/// </summary>
public sealed class ThreePhaseAcSource : IInstrumentDriver
{
    public const string AdapterName = "Dreiphasige AC-Quelle (3× einphasig)";
    public static readonly string[] Phases = ["L1", "L2", "L3"];

    private readonly IInstrumentDriver[] _phases;

    public ThreePhaseAcSource(string resource, IReadOnlyList<IInstrumentDriver> phases)
    {
        if (phases.Count != 3) throw new InstrumentException("Dreiphasige AC-Quelle: genau drei Phasenquellen erforderlich.");
        _phases = [.. phases];
        Resource = resource;
        Channels = BuildChannels(phases[0].Channels);
    }

    public string Adapter => AdapterName;
    public string Name => $"Dreiphasige AC-Quelle ({_phases[0].Model})";
    public string Model => "3× " + _phases[0].Model;
    public string Resource { get; }
    public IReadOnlyList<InstrumentChannel> Channels { get; }
    public IReadOnlyList<string> Log => _phases.SelectMany((p, i) => p.Log.Select(l => $"[{Phases[i]}] {l}")).ToList();

    private static IReadOnlyList<InstrumentChannel> BuildChannels(IReadOnlyList<InstrumentChannel> single)
    {
        var list = new List<InstrumentChannel>();
        foreach (var phase in Phases)
            foreach (var c in single)
                list.Add(c with { Name = $"{phase}:{c.Name}", Description = $"{phase}: {c.Description}" });
        // gemeinsame Kanäle auf alle drei Phasen
        foreach (var c in single.Where(c => c.Access.HasFlag(ChannelAccess.Write)))
            list.Add(c with { Name = $"ALLE:{c.Name}", Description = $"Alle Phasen: {c.Description}" });
        return list;
    }

    /// <summary>Kanal „L2:VOLT“ → (Phase 1, „VOLT“); „ALLE:FREQ“ bzw. „FREQ“ → (-1, „FREQ“).</summary>
    private (int Phase, string Channel) Split(string channel)
    {
        var colon = channel.IndexOf(':');
        if (colon < 0) return (-1, channel);
        var prefix = channel[..colon];
        var rest = channel[(colon + 1)..];
        if (prefix.Equals("ALLE", StringComparison.OrdinalIgnoreCase)) return (-1, rest);
        var index = Array.FindIndex(Phases, p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? (index, rest) : (-1, channel);
    }

    public ChannelCapabilities GetCapabilities(string channel)
    {
        var (phase, inner) = Split(channel);
        return _phases[phase < 0 ? 0 : phase].GetCapabilities(inner);
    }

    public Task<double> ReadAsync(string channel, CancellationToken cancellationToken)
    {
        var (phase, inner) = Split(channel);
        return _phases[phase < 0 ? 0 : phase].ReadAsync(inner, cancellationToken); // „ALLE“/ohne Präfix liest L1
    }

    public async Task WriteAsync(string channel, double value, CancellationToken cancellationToken)
    {
        var (phase, inner) = Split(channel);
        if (phase >= 0)
        {
            await _phases[phase].WriteAsync(inner, value, cancellationToken).ConfigureAwait(false);
            return;
        }
        foreach (var p in _phases) await p.WriteAsync(inner, value, cancellationToken).ConfigureAwait(false);
    }

    public async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        // jede Phase einzeln in den sicheren Zustand – ein Fehler darf die anderen nicht verhindern
        List<Exception>? errors = null;
        foreach (var p in _phases)
        {
            try { await p.SafeStateAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (errors is not null) throw new AggregateException("Sicherer Zustand einer Phase fehlgeschlagen.", errors);
    }

    public async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();
        var ok = true;
        for (var i = 0; i < 3; i++)
        {
            var probe = await _phases[i].ProbeAsync(cancellationToken).ConfigureAwait(false);
            ok &= probe.Ok;
            details.Add($"{Phases[i]}: {(probe.Ok ? "OK" : "Fehler")} – {probe.Identity}");
            details.AddRange(probe.Details.Select(d => $"  {Phases[i]} {d}"));
        }
        return new InstrumentProbe(ok, $"Dreiphasig: {string.Join(" / ", _phases.Select(p => p.Resource))}", details, Log);
    }

    public void Dispose()
    {
        foreach (var p in _phases) p.Dispose();
    }
}
