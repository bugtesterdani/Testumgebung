namespace Testumgebung.Instruments;

/// <summary>ET Instrumente EAQ-1000/EAQ-M-1000; dokumentierte Sollwertbefehle, optionale gerätespezifische Abfragen.</summary>
public sealed class EtEaq1000Source : ScpiInstrument
{
    public const string AdapterName = "ET Instrumente EAQ-1000 AC-Quelle";
    public static readonly string[] Models = ["EAQ-1000", "EAQ-M-1000"];
    private readonly InstrumentOptions _options;
    public EtEaq1000Source(string model, ScpiSession session, InstrumentOptions? options = null) : base(AdapterName, model, session)
    {
        _options = options ?? new InstrumentOptions();
        // Die öffentliche ET-Dokumentation definiert keine SYST:ERR?-Antworten.
        Session.CheckErrors = false;
        Channels = [Create("VOLT", "V", 0, 300), Create("CURR", "A", 0, 4), Create("FREQ", "Hz", 1, 500),
            Create("DC", "V", -425, 425), Create("PHAS", "°", 0, 360), Create("OUT", "bool", 0, 1)];
    }
    private InstrumentChannel Create(string name, string unit, double min, double max)
        => new(name, unit, min, max, _options.Text("Abfrage" + name) is { Length: > 0 } ? ChannelAccess.ReadWrite : ChannelAccess.Write,
            "Sollwert / Ausgang; Lesen nur mit dokumentiertem Abfragebefehl");
    public override IReadOnlyList<InstrumentChannel> Channels { get; }
    protected override Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
        => Session.QueryDoubleAsync(_options.Text("Abfrage" + channel.Name)!, ct);
    protected override async Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        if (!double.IsFinite(value) || (channel.Name == "OUT" && value is not (0 or 1))) throw new ArgumentOutOfRangeException(nameof(value));
        if (channel.Name == "OUT")
        {
            if (value == 0) { await SafeStateAsync(ct).ConfigureAwait(false); return; }
            await Session.WriteAsync("SOUR ON", ct).ConfigureAwait(false);
            await Session.WriteAsync("SOUR:OUTP ON", ct).ConfigureAwait(false); return;
        }
        var command = channel.Name switch { "DC" => "SOUR:VOLT:DC", "VOLT" => "SOUR:VOLT", "CURR" => "SOUR:CURR", "FREQ" => "SOUR:FREQ", _ => "SOUR:PHAS" };
        await Session.WriteAsync(command + " " + Scpi.N(value), ct).ConfigureAwait(false);
    }
    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        try { await Session.WriteAsync("SOUR:OUTP OFF", cancellationToken).ConfigureAwait(false); }
        finally { await Session.WriteAsync("SOUR OFF", cancellationToken).ConfigureAwait(false); }
    }
    public override async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        if (_options.Text("Pruefbefehl") is not { Length: > 0 } command)
            return new InstrumentProbe(false, "Identität nicht geprüft", ["Pruefbefehl gemäß Gerätehandbuch konfigurieren; keine Ausgänge wurden geändert."], Log);
        try
        {
            var identity = await Session.QueryAsync(command, cancellationToken).ConfigureAwait(false);
            return new InstrumentProbe(!string.IsNullOrWhiteSpace(identity), identity, ["Antwort auf konfigurierten Prüfbefehl; kein Messwertnachweis."], Log);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InstrumentException)
        { return new InstrumentProbe(false, "nicht erreichbar", [ex.Message], Log); }
    }
}
