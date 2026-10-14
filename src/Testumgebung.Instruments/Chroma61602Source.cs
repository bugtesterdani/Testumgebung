/**
 * @file
 * @brief Übersetzt Netzteiloperationen für die Chroma-61602-Quelle in deren Gerätekommunikation.
 */

namespace Testumgebung.Instruments;

/// <summary>Chroma 61602, AC-Grundbetrieb; Befehle laut Benutzerhandbuch 61601–61604, Kapitel 7.</summary>
public sealed class Chroma61602Source : ScpiInstrument
{
    public const string AdapterName = "Chroma 61602 AC-Quelle";
    public static readonly string[] Models = ["61602"];
    private readonly string _range;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _initialized;
    public Chroma61602Source(ScpiSession session, InstrumentOptions? options = null) : base(AdapterName, "61602", session)
    {
        _range = (options?.Text("Bereich") ?? "HIGH").ToUpperInvariant();
        if (_range is not ("HIGH" or "LOW")) throw new InstrumentException("Chroma Bereich: HIGH oder LOW.");
        Session.TextErrorReplies = true;
        Channels = [new("VOLT", "V", 0, _range == "LOW" ? 150 : 300, ChannelAccess.ReadWrite, "AC-Spannung setzen / RMS messen"),
            new("FREQ", "Hz", 15, 1000, ChannelAccess.ReadWrite, "Frequenz setzen / messen"),
            new("CURR", "A", 0, _range == "LOW" ? 8 : 4, ChannelAccess.ReadWrite, "Stromgrenze setzen / Strom messen"),
            new("POW", "W", 0, 1000, ChannelAccess.Read, "Wirkleistung"),
            new("OUT", "bool", 0, 1, ChannelAccess.ReadWrite, "Ausgang und Relais ein / aus")];
    }
    public override IReadOnlyList<InstrumentChannel> Channels { get; }
    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        if (channel.Name == "OUT")
        {
            var text = (await Session.QueryAsync("OUTP?", ct).ConfigureAwait(false)).Trim().ToUpperInvariant();
            return text switch { "ON" or "1" => 1, "OFF" or "0" => 0, _ => throw new InstrumentException("Ungültiger Ausgangszustand: " + text) };
        }
        var query = channel.Name switch { "VOLT" => "MEAS:VOLT:ACDC?", "FREQ" => "MEAS:FREQ?", "CURR" => "MEAS:CURR:AC?", _ => "MEAS:POW:AC?" };
        return await Session.QueryDoubleAsync(query, ct).ConfigureAwait(false);
    }
    private async Task InitializeAsync(CancellationToken ct)
    {
        await _init.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Session.WriteAsync("OUTP OFF", ct).ConfigureAwait(false);
            await Session.WriteAsync("OUTP:REL OFF", ct).ConfigureAwait(false);
            if (Resource.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || Resource.StartsWith("ASRL", StringComparison.OrdinalIgnoreCase))
                await Session.WriteAsync("SYST:REM", ct).ConfigureAwait(false);
            await Session.WriteAsync("VOLT:RANG " + _range, ct).ConfigureAwait(false);
            await Session.WriteAsync("OUTP:COUP AC", ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _init.Release(); }
    }
    protected override async Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        if (!double.IsFinite(value) || (channel.Name == "OUT" && value is not (0 or 1))) throw new ArgumentOutOfRangeException(nameof(value));
        if (channel.Name == "OUT" && value == 0) { await SafeStateAsync(ct).ConfigureAwait(false); return; }
        await InitializeAsync(ct).ConfigureAwait(false);
        if (channel.Name == "OUT")
        {
            await Session.WriteAsync("OUTP:REL ON", ct).ConfigureAwait(false);
            await Session.WriteAsync("OUTP ON", ct).ConfigureAwait(false); return;
        }
        var command = channel.Name switch { "VOLT" => "VOLT:AC", "FREQ" => "FREQ", _ => "CURR:LIM" };
        await Session.WriteAsync(command + " " + Scpi.N(value), ct).ConfigureAwait(false);
    }
    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        try { await Session.WriteAsync("OUTP OFF", cancellationToken).ConfigureAwait(false); }
        finally { await Session.WriteAsync("OUTP:REL OFF", cancellationToken).ConfigureAwait(false); }
    }
}
