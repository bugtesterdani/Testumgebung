/**
 * @file
 * @brief Übersetzt Mess- und Generatoroperationen für die unterstützten Keysight-Geräte.
 */

using System.Buffers.Binary;
using System.Globalization;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments;

/// <summary>Optionen eines Geräts aus der Prüfplatzkonfiguration (Schlüssel ohne Groß-/Kleinschreibung).</summary>
public sealed class InstrumentOptions(IReadOnlyDictionary<string, string>? values = null)
{
    private readonly Dictionary<string, string> _values = new(values ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

    public string? Text(string key) => _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public double? Number(string key)
        => Text(key) is { } t && double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public bool Flag(string key) => Text(key) is { } t && (t == "1" || t.Equals("ja", StringComparison.OrdinalIgnoreCase) || t.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>„L1=1;L2=2“ → Wörterbuch.</summary>
    public Dictionary<string, string> Map(string key)
        => (Text(key) ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2, StringSplitOptions.TrimEntries)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
}

internal static class Scpi
{
    public static string N(double v) => v.ToString("G12", CultureInfo.InvariantCulture);

    public static string OnOff(double v) => v >= 0.5 ? "ON" : "OFF";
}

// =====================================================================================================================
// Digitalmultimeter Keysight/Agilent Truevolt 34460A/34461A/34465A/34470A sowie 34401A/34410A/34411A
// =====================================================================================================================

/// <summary>
/// Digitalmultimeter (SCPI): Gleich-/Wechselspannung, Gleich-/Wechselstrom, Widerstand 2-/4-Leiter, Frequenz.
/// Funktion wird nur bei Wechsel konfiguriert (CONF … AUTO), danach READ?. Option „NPLC“ (Integrationszeit).
/// </summary>
public sealed class Keysight3446xDmm : ScpiInstrument
{
    public const string AdapterName = "Keysight 3446xA/34401A DMM";
    public static readonly string[] Models = ["34460A", "34461A", "34465A", "34470A", "34401A", "34410A", "34411A"];

    private readonly InstrumentOptions _options;
    private string? _function;

    public Keysight3446xDmm(string model, ScpiSession session, InstrumentOptions? options = null) : base(AdapterName, model, session)
    {
        _options = options ?? new InstrumentOptions();
        var fourWire = model != "34460A";
        var list = new List<InstrumentChannel>
        {
            new("VDC", "V", -1000, 1000, ChannelAccess.Read, "Gleichspannung (CONF:VOLT:DC AUTO)"),
            new("VAC", "V", 0, 750, ChannelAccess.Read, "Wechselspannung True-RMS (CONF:VOLT:AC AUTO)"),
            new("IDC", "A", -3, 3, ChannelAccess.Read, "Gleichstrom 3-A-Eingang (CONF:CURR:DC AUTO)"),
            new("IAC", "A", 0, 3, ChannelAccess.Read, "Wechselstrom True-RMS (CONF:CURR:AC AUTO)"),
            new("RES", "Ohm", 0, 1e9, ChannelAccess.Read, "Widerstand 2-Leiter (CONF:RES AUTO)"),
            new("FREQ", "Hz", 3, 300e3, ChannelAccess.Read, "Frequenz (CONF:FREQ)"),
        };
        if (fourWire) list.Add(new("FRES", "Ohm", 0, 1e9, ChannelAccess.Read, "Widerstand 4-Leiter (CONF:FRES AUTO)"));
        Channels = list;
    }

    public override IReadOnlyList<InstrumentChannel> Channels { get; }

    private static string Function(string channel) => channel.ToUpperInvariant() switch
    {
        "VDC" => "VOLT:DC",
        "VAC" => "VOLT:AC",
        "IDC" => "CURR:DC",
        "IAC" => "CURR:AC",
        "RES" => "RES",
        "FRES" => "FRES",
        _ => "FREQ",
    };

    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        var f = Function(channel.Name);
        if (_function != f)
        {
            await Session.WriteAsync(f == "FREQ" ? "CONF:FREQ" : $"CONF:{f} AUTO", ct).ConfigureAwait(false);
            if (_options.Number("NPLC") is { } nplc && f is "VOLT:DC" or "CURR:DC" or "RES" or "FRES")
                await Session.WriteAsync($"{f}:NPLC {Scpi.N(nplc)}", ct).ConfigureAwait(false);
            _function = f;
        }
        // Lange Integrationszeiten / Autorange: großzügiges Zeitlimit für die Messung
        return await Session.QueryDoubleAsync("READ?", ct, Session.Timeout + TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    protected override Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
        => throw new InstrumentException($"{Model}: Messgerät ohne Ausgänge.");

    /// <summary>Multimeter hat keine Ausgänge; Konfiguration nach Abbruch neu setzen.</summary>
    public override Task SafeStateAsync(CancellationToken cancellationToken)
    {
        _function = null;
        return Task.CompletedTask;
    }

    protected override Task<IReadOnlyList<string>> DescribeAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Model == "34401A"
            ? ["34401A: GPIB oder RS-232 – über VISA (z. B. GPIB0::22::INSTR)."]
            : [$"{Model}: LAN (SOCKET 5025, VXI-11, HiSLIP), USB-TMC; Integrationszeit über Option NPLC."]);
}

// =====================================================================================================================
// DC-Netzgeräte Keysight E36311A/E36312A/E36313A (E36300) und Agilent E3631A
// =====================================================================================================================

/// <summary>
/// Dreifach-Netzgerät: CHn = Spannung (setzen: Sollwert, lesen: Messwert), CHn:I = Strom (setzen: Strombegrenzung,
/// lesen: Messwert), CHn:OUT = Ausgang 0/1. Sicherer Zustand: alle Ausgänge aus.
/// E36300 adressiert Kanäle per Kanalliste „(@n)“, die E3631A über INST:NSEL.
/// </summary>
public sealed class KeysightE36300Supply : ScpiInstrument
{
    public const string AdapterName = "Keysight E36300/E3631A Netzgerät";
    public static readonly string[] Models = ["E36311A", "E36312A", "E36313A", "E3631A"];

    private readonly bool _legacy;

    public KeysightE36300Supply(string model, ScpiSession session) : base(AdapterName, model, session)
    {
        _legacy = model == "E3631A";
        (double V, double I)[] outputs = model switch
        {
            "E36311A" or "E3631A" => [(6, 5), (25, 1), (-25, 1)],
            "E36313A" => [(6, 10), (25, 2), (25, 2)],
            _ => [(6, 5), (25, 1), (25, 1)],
        };
        var list = new List<InstrumentChannel>();
        for (var i = 0; i < outputs.Length; i++)
        {
            var (v, a) = outputs[i];
            var n = i + 1;
            list.Add(new($"CH{n}", "V", Math.Min(0, v), Math.Max(0, v), ChannelAccess.ReadWrite, $"Ausgang {n}: Spannung setzen / messen"));
            list.Add(new($"CH{n}:I", "A", 0, a, ChannelAccess.ReadWrite, $"Ausgang {n}: Strombegrenzung setzen / Strom messen"));
            list.Add(new($"CH{n}:OUT", "bool", 0, 1, ChannelAccess.ReadWrite, $"Ausgang {n} ein (1) / aus (0)"));
        }
        Channels = list;
    }

    public override IReadOnlyList<InstrumentChannel> Channels { get; }

    private static int Number(InstrumentChannel c) => c.Name[2] - '0';

    private static string Legacy(int n) => n switch { 1 => "P6V", 2 => "P25V", _ => "N25V" };

    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        var n = Number(channel);
        if (channel.Name.EndsWith(":OUT", StringComparison.OrdinalIgnoreCase))
            return await Session.QueryDoubleAsync(_legacy ? "OUTP?" : $"OUTP? (@{n})", ct).ConfigureAwait(false);
        var what = channel.Name.EndsWith(":I", StringComparison.OrdinalIgnoreCase) ? "CURR" : "VOLT";
        return await Session.QueryDoubleAsync(_legacy ? $"MEAS:{what}? {Legacy(n)}" : $"MEAS:{what}? (@{n})", ct).ConfigureAwait(false);
    }

    protected override async Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        var n = Number(channel);
        if (channel.Name.EndsWith(":OUT", StringComparison.OrdinalIgnoreCase))
        {
            await Session.WriteAsync(_legacy ? $"OUTP {Scpi.OnOff(value)}" : $"OUTP {Scpi.OnOff(value)},(@{n})", ct).ConfigureAwait(false);
            return;
        }
        var what = channel.Name.EndsWith(":I", StringComparison.OrdinalIgnoreCase) ? "CURR" : "VOLT";
        if (_legacy)
        {
            await Session.WriteAsync($"INST:NSEL {n}", ct).ConfigureAwait(false);
            await Session.WriteAsync($"{what} {Scpi.N(value)}", ct).ConfigureAwait(false);
        }
        else
        {
            await Session.WriteAsync($"{what} {Scpi.N(value)},(@{n})", ct).ConfigureAwait(false);
        }
    }

    public override Task SafeStateAsync(CancellationToken cancellationToken)
        => Session.WriteAsync(_legacy ? "OUTP OFF" : "OUTP OFF,(@1:3)", SafeToken(cancellationToken));
}

// =====================================================================================================================
// AC-Quellen Keysight AC6801B/AC6802B/AC6803B/AC6804B
// =====================================================================================================================

/// <summary>
/// Programmierbare AC-Quelle (einphasig, 155/310-V-Bereich, 40–500 Hz): VOLT = Effektivwert setzen / messen,
/// FREQ = Frequenz setzen / messen, CURR = Strombegrenzung setzen / Strom messen, POW = Wirkleistung messen,
/// OUT = Ausgang 0/1. Option „Bereich“ (155 oder 310) wird vor dem ersten Einschalten gesetzt.
/// </summary>
public sealed class KeysightAc6800Source : ScpiInstrument
{
    public const string AdapterName = "Keysight AC6800B AC-Quelle";
    public static readonly string[] Models = ["AC6801B", "AC6802B", "AC6803B", "AC6804B"];

    private readonly InstrumentOptions _options;
    private bool _initialized;

    public KeysightAc6800Source(string model, ScpiSession session, InstrumentOptions? options = null) : base(AdapterName, model, session)
    {
        _options = options ?? new InstrumentOptions();
        var va = model switch { "AC6801B" => 500, "AC6803B" => 2000, "AC6804B" => 4000, _ => 1000 };
        Channels =
        [
            new("VOLT", "V", 0, 310, ChannelAccess.ReadWrite, "Ausgangsspannung (Effektivwert) setzen / messen"),
            new("FREQ", "Hz", 40, 500, ChannelAccess.ReadWrite, "Frequenz setzen / messen"),
            new("CURR", "A", 0, va / 100.0, ChannelAccess.ReadWrite, $"Strombegrenzung setzen / Strom messen ({va} VA)"),
            new("POW", "W", 0, va, ChannelAccess.Read, "Wirkleistung messen"),
            new("OUT", "bool", 0, 1, ChannelAccess.ReadWrite, "Ausgang ein (1) / aus (0)"),
        ];
    }

    public override IReadOnlyList<InstrumentChannel> Channels { get; }

    private async Task InitAsync(CancellationToken ct)
    {
        if (_initialized) return;
        if (_options.Number("Bereich") is { } range) await Session.WriteAsync($"VOLT:RANG {Scpi.N(range)}", ct).ConfigureAwait(false);
        _initialized = true;
    }

    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct) => channel.Name switch
    {
        "VOLT" => await Session.QueryDoubleAsync("MEAS:VOLT:AC?", ct).ConfigureAwait(false),
        "FREQ" => await Session.QueryDoubleAsync("FREQ?", ct).ConfigureAwait(false),
        "CURR" => await Session.QueryDoubleAsync("MEAS:CURR:AC?", ct).ConfigureAwait(false),
        "POW" => await Session.QueryDoubleAsync("MEAS:POW:AC?", ct).ConfigureAwait(false),
        _ => await Session.QueryDoubleAsync("OUTP?", ct).ConfigureAwait(false),
    };

    protected override async Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        await InitAsync(ct).ConfigureAwait(false);
        var command = channel.Name switch
        {
            "VOLT" => $"VOLT {Scpi.N(value)}",
            "FREQ" => $"FREQ {Scpi.N(value)}",
            "CURR" => $"CURR {Scpi.N(value)}",
            _ => $"OUTP {Scpi.OnOff(value)}",
        };
        await Session.WriteAsync(command, ct).ConfigureAwait(false);
    }

    public override Task SafeStateAsync(CancellationToken cancellationToken) => Session.WriteAsync("OUTP OFF", SafeToken(cancellationToken));
}

// =====================================================================================================================
// Funktions-/Arbiträrgeneratoren Keysight 33500B / 33600A (Trueform)
// =====================================================================================================================

/// <summary>
/// Generator mit Kurvenausgabe (Laden → Übertragen → Starten → Stoppen). Skalare Kanäle CHn:FREQ, CHn:AMPL (Vss),
/// CHn:OFFS, CHn:DC (Gleichspannung), CHn:OUT. Ausgabekanal „ARB“: Kurvenkanäle → Generatorkanäle über Option
/// „Kurvenkanäle“ (Standard „L1=1;L2=2“). Die Kurve wird verlustfrei als float32-Block übertragen; Amplitude und Offset
/// ergeben sich aus Minimum/Maximum der Kurve (keine Skalierung der Werte, nur Darstellung im Gerät).
/// Option „Last“: 50 (Standard) oder HighZ (Ausgangsbereich verdoppelt sich).
/// </summary>
public sealed class Keysight33500Generator : ScpiInstrument, IWaveformOutputAdapter
{
    public const string AdapterName = "Keysight 33500B/33600A Generator";
    public const string OutputChannel = "ARB";
    public static readonly string[] Models = ["33509B", "33510B", "33511B", "33512B", "33519B", "33520B", "33521B", "33522B", "33611A", "33612A", "33621A", "33622A"];

    private readonly object _gate = new();
    private readonly int _count;
    private readonly double _limit;
    private readonly bool _highZ;
    private readonly Dictionary<string, int> _map;
    private OutputState _state = OutputState.Idle;
    private OutputCurve? _loaded;
    private OutputCurve? _inDevice;

    public Keysight33500Generator(string model, ScpiSession session, InstrumentOptions? options = null) : base(AdapterName, model, session)
    {
        options ??= new InstrumentOptions();
        _count = model.EndsWith("0B", StringComparison.Ordinal) || model.EndsWith("2B", StringComparison.Ordinal) || model.EndsWith("2A", StringComparison.Ordinal) ? 2 : 1;
        _highZ = string.Equals(options.Text("Last"), "HighZ", StringComparison.OrdinalIgnoreCase) || string.Equals(options.Text("Last"), "INF", StringComparison.OrdinalIgnoreCase);
        _limit = _highZ ? 10 : 5;
        var map = options.Map("Kurvenkanäle");
        if (map.Count == 0) map = _count == 2 ? new() { ["L1"] = "1", ["L2"] = "2" } : new() { ["L1"] = "1" };
        _map = map.Where(kv => int.TryParse(kv.Value, out var n) && n >= 1 && n <= _count).ToDictionary(kv => kv.Key, kv => int.Parse(kv.Value, CultureInfo.InvariantCulture), StringComparer.Ordinal);
        MaxSamples = (int)(options.Number("Speicher") ?? (model.StartsWith("336", StringComparison.Ordinal) ? 4_000_000 : 1_000_000));
        MaxRate = model.StartsWith("336", StringComparison.Ordinal) ? 1e9 : 250e6;
        var list = new List<InstrumentChannel>();
        for (var n = 1; n <= _count; n++)
        {
            list.Add(new($"CH{n}:FREQ", "Hz", 1e-6, model.StartsWith("336", StringComparison.Ordinal) ? 120e6 : 30e6, ChannelAccess.ReadWrite, $"Kanal {n}: Frequenz"));
            list.Add(new($"CH{n}:AMPL", "Vss", 1e-3, 2 * _limit, ChannelAccess.ReadWrite, $"Kanal {n}: Amplitude (Spitze-Spitze)"));
            list.Add(new($"CH{n}:OFFS", "V", -_limit, _limit, ChannelAccess.ReadWrite, $"Kanal {n}: Offset"));
            list.Add(new($"CH{n}:DC", "V", -_limit, _limit, ChannelAccess.Write, $"Kanal {n}: Gleichspannung ausgeben (FUNC DC)"));
            list.Add(new($"CH{n}:OUT", "bool", 0, 1, ChannelAccess.ReadWrite, $"Kanal {n}: Ausgang ein/aus"));
        }
        list.Add(new(OutputChannel, "V", -_limit, _limit, ChannelAccess.WaveformOutput, "Kurvenausgabe: " + string.Join(", ", _map.Select(kv => $"{kv.Key}→Kanal {kv.Value}"))));
        Channels = list;
    }

    public override IReadOnlyList<InstrumentChannel> Channels { get; }
    public int MaxSamples { get; }
    public double MaxRate { get; }

    private static (int N, string Kind) Parse(InstrumentChannel c) => (c.Name[2] - '0', c.Name[(c.Name.IndexOf(':') + 1)..]);

    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        var (n, kind) = Parse(channel);
        return kind switch
        {
            "FREQ" => await Session.QueryDoubleAsync($"SOUR{n}:FREQ?", ct).ConfigureAwait(false),
            "AMPL" => await Session.QueryDoubleAsync($"SOUR{n}:VOLT?", ct).ConfigureAwait(false),
            "OFFS" => await Session.QueryDoubleAsync($"SOUR{n}:VOLT:OFFS?", ct).ConfigureAwait(false),
            _ => await Session.QueryDoubleAsync($"OUTP{n}?", ct).ConfigureAwait(false),
        };
    }

    protected override async Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        var (n, kind) = Parse(channel);
        switch (kind)
        {
            case "FREQ": await Session.WriteAsync($"SOUR{n}:FREQ {Scpi.N(value)}", ct).ConfigureAwait(false); break;
            case "AMPL": await Session.WriteAsync($"SOUR{n}:VOLT {Scpi.N(value)}", ct).ConfigureAwait(false); break;
            case "OFFS": await Session.WriteAsync($"SOUR{n}:VOLT:OFFS {Scpi.N(value)}", ct).ConfigureAwait(false); break;
            case "DC":
                await Session.WriteAsync($"SOUR{n}:FUNC DC", ct).ConfigureAwait(false);
                await Session.WriteAsync($"SOUR{n}:VOLT:OFFS {Scpi.N(value)}", ct).ConfigureAwait(false);
                break;
            default: await Session.WriteAsync($"OUTP{n} {Scpi.OnOff(value)}", ct).ConfigureAwait(false); break;
        }
    }

    // ------------------------------------------------------------------ Kurvenausgabe

    private void CheckOutput(string channel)
    {
        if (!string.Equals(channel, OutputChannel, StringComparison.OrdinalIgnoreCase))
            throw new InstrumentException($"{Model}: Kurvenausgabe über Kanal „{OutputChannel}“ (nicht „{channel}“).");
    }

    public OutputCapabilities GetOutputCapabilities(string channel)
    {
        CheckOutput(channel);
        return new OutputCapabilities($"{Model} ({Resource})", "V", -_limit, _limit, _map.Keys.ToList(), [], false, MaxSamples, 1e-6, MaxRate);
    }

    public OutputState GetOutputState(string channel)
    {
        lock (_gate) return _state;
    }

    public string? LoadedCurve(string channel)
    {
        lock (_gate) return (_inDevice ?? _loaded)?.Key;
    }

    public Task LoadAsync(string channel, OutputCurve curve, CancellationToken cancellationToken)
    {
        CheckOutput(channel);
        lock (_gate)
        {
            if (_state == OutputState.Running) throw new OutputSequenceException("Ausgabe läuft – erst stoppen, dann neue Kurve laden.");
            var errors = OutputSuitability.Check(GetOutputCapabilities(channel), curve).Where(i => i.IsError).ToList();
            if (errors.Count > 0) throw new OutputSequenceException("Gerät ungeeignet: " + string.Join(" ", errors.Select(e => e.Text)));
            _loaded = curve;
            _state = OutputState.Loaded;
        }
        return Task.CompletedTask;
    }

    public async Task TransferAsync(string channel, CancellationToken cancellationToken)
    {
        CheckOutput(channel);
        OutputCurve curve;
        lock (_gate)
        {
            if (_state != OutputState.Loaded || _loaded is null) throw new OutputSequenceException("Übertragen erst nach dem Laden einer Kurve.");
            curve = _loaded;
        }
        foreach (var data in curve.Channels)
        {
            var n = _map[data.Id];
            await Session.WriteAsync($"OUTP{n}:LOAD {(_highZ ? "INF" : "50")}", cancellationToken).ConfigureAwait(false);
            var min = data.Samples.Min();
            var max = data.Samples.Max();
            if (max - min < 1e-3)
            {
                await Session.WriteAsync($"SOUR{n}:FUNC DC", cancellationToken).ConfigureAwait(false);
                await Session.WriteAsync($"SOUR{n}:VOLT:OFFS {Scpi.N((max + min) / 2)}", cancellationToken).ConfigureAwait(false);
                continue;
            }
            var amplitude = max - min;
            var offset = (max + min) / 2;
            var block = new byte[data.Samples.Length * 4];
            for (var i = 0; i < data.Samples.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(i * 4), (float)Math.Clamp((data.Samples[i] - offset) / (amplitude / 2), -1, 1));
            var name = $"TU{n}";
            await Session.WriteAsync($"SOUR{n}:DATA:VOL:CLE", cancellationToken).ConfigureAwait(false);
            await Session.WriteAsync("FORM:BORD SWAP", cancellationToken).ConfigureAwait(false);
            await Session.WriteBlockAsync($"SOUR{n}:DATA:ARB {name},", block, cancellationToken).ConfigureAwait(false);
            await Session.WriteAsync($"SOUR{n}:FUNC:ARB {name}", cancellationToken).ConfigureAwait(false);
            await Session.WriteAsync($"SOUR{n}:FUNC:ARB:SRAT {Scpi.N(curve.SampleRate)}", cancellationToken).ConfigureAwait(false);
            await Session.WriteAsync($"SOUR{n}:FUNC ARB", cancellationToken).ConfigureAwait(false);
            await Session.WriteAsync($"SOUR{n}:VOLT {Scpi.N(amplitude)}", cancellationToken).ConfigureAwait(false);
            await Session.WriteAsync($"SOUR{n}:VOLT:OFFS {Scpi.N(offset)}", cancellationToken).ConfigureAwait(false);
        }
        // Zwei Kanäle: Arbiträrkurven phasengleich neu starten (gemeinsame Zeitbasis L1/L2)
        if (curve.Channels.Count > 1) await Session.WriteAsync("FUNC:ARB:SYNC", cancellationToken).ConfigureAwait(false);
        await Session.WaitCompleteAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _inDevice = curve;
            _state = OutputState.Transferred;
        }
    }

    public async Task StartAsync(string channel, CancellationToken cancellationToken)
    {
        CheckOutput(channel);
        OutputCurve curve;
        lock (_gate)
        {
            if (_state is not (OutputState.Transferred or OutputState.Stopped) || _inDevice is null)
                throw new OutputSequenceException("Starten erst nach dem Übertragen einer Kurve.");
            curve = _inDevice;
        }
        foreach (var data in curve.Channels)
            await Session.WriteAsync($"OUTP{_map[data.Id]} ON", cancellationToken).ConfigureAwait(false);
        lock (_gate) _state = OutputState.Running;
    }

    public async Task StopAsync(string channel, CancellationToken cancellationToken)
    {
        CheckOutput(channel);
        lock (_gate)
        {
            if (_state != OutputState.Running) throw new OutputSequenceException("Ausgabe läuft nicht.");
        }
        foreach (var n in _map.Values.Distinct())
            await Session.WriteAsync($"OUTP{n} OFF", SafeToken(cancellationToken)).ConfigureAwait(false);
        lock (_gate) _state = OutputState.Stopped;
    }

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        var token = SafeToken(cancellationToken);
        for (var n = 1; n <= _count; n++) await Session.WriteAsync($"OUTP{n} OFF", token).ConfigureAwait(false);
        lock (_gate)
            if (_state == OutputState.Running) _state = OutputState.Stopped;
    }
}

// =====================================================================================================================
// Oszilloskope Keysight InfiniiVision 1000X/2000X/3000T/4000X
// =====================================================================================================================

/// <summary>
/// Oszilloskop: Kurvenaufnahme aller angeforderten Kanäle mit einer Erfassung (:DIGitize) – gemeinsame Zeitbasis.
/// Daten als WORD-Block (LSB zuerst), Umrechnung über die Präambel. Das Gerät bestimmt die tatsächliche
/// Abtastrate/Punktzahl; sie wird unverändert übernommen und in der Qualität vermerkt (keine stille Umrechnung).
/// Skalare Kanäle: CHANn:VRMS, CHANn:VPP, CHANn:FREQ, CHANn:VAVG (lesen), CHANn:SCALE, CHANn:OFFSET, TIMEBASE (setzen/lesen).
/// Option „Triggerquelle“ (z. B. EXT, CHAN1; Standard: automatische Triggerung), „Triggerpegel“.
/// </summary>
public sealed class KeysightInfiniiVisionScope : ScpiInstrument, IWaveformAdapter
{
    public const string AdapterName = "Keysight InfiniiVision Oszilloskop";
    public static readonly string[] Models = ["DSOX1102G", "DSOX1204G", "DSOX2002A", "DSOX2004A", "DSOX3014T", "DSOX3024T", "DSOX3034T", "DSOX3054T", "DSOX3104T", "DSOX4024A", "DSOX4034A", "DSOX4054A"];

    private readonly InstrumentOptions _options;
    private readonly int _count;
    private long _captures;

    public KeysightInfiniiVisionScope(string model, ScpiSession session, InstrumentOptions? options = null) : base(AdapterName, model, session)
    {
        _options = options ?? new InstrumentOptions();
        var digits = new string(model.Where(char.IsDigit).ToArray());
        _count = digits.Length >= 4 && digits[3] is '2' or '4' ? digits[3] - '0' : 4;
        var list = new List<InstrumentChannel>();
        for (var n = 1; n <= _count; n++)
        {
            list.Add(new($"CHAN{n}", "V", -300, 300, ChannelAccess.Waveform, $"Kanal {n}: Kurvenaufnahme"));
            list.Add(new($"CHAN{n}:VRMS", "V", 0, 300, ChannelAccess.Read, $"Kanal {n}: Effektivwert (AC, Bildschirm)"));
            list.Add(new($"CHAN{n}:VPP", "V", 0, 600, ChannelAccess.Read, $"Kanal {n}: Spitze-Spitze"));
            list.Add(new($"CHAN{n}:VAVG", "V", -300, 300, ChannelAccess.Read, $"Kanal {n}: Mittelwert"));
            list.Add(new($"CHAN{n}:FREQ", "Hz", 0, 1e9, ChannelAccess.Read, $"Kanal {n}: Frequenz"));
            list.Add(new($"CHAN{n}:SCALE", "V/div", 1e-3, 10, ChannelAccess.ReadWrite, $"Kanal {n}: Vertikalskala"));
            list.Add(new($"CHAN{n}:OFFSET", "V", -300, 300, ChannelAccess.ReadWrite, $"Kanal {n}: Offset"));
        }
        list.Add(new("TIMEBASE", "s/div", 1e-9, 50, ChannelAccess.ReadWrite, "Zeitbasis"));
        Channels = list;
    }

    public override IReadOnlyList<InstrumentChannel> Channels { get; }

    private static (string Source, string Kind) Split(string name)
        => name.IndexOf(':') is var i and > 0 ? (name[..i], name[(i + 1)..]) : (name, "");

    protected override async Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct)
    {
        if (channel.Name == "TIMEBASE") return await Session.QueryDoubleAsync(":TIM:SCAL?", ct).ConfigureAwait(false);
        var (src, kind) = Split(channel.Name);
        var command = kind switch
        {
            "VRMS" => $":MEAS:VRMS? DISP,AC,{src}",
            "VPP" => $":MEAS:VPP? {src}",
            "VAVG" => $":MEAS:VAV? DISP,{src}",
            "FREQ" => $":MEAS:FREQ? {src}",
            "SCALE" => $":{src}:SCAL?",
            _ => $":{src}:OFFS?",
        };
        return await Session.QueryDoubleAsync(command, ct).ConfigureAwait(false);
    }

    protected override Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct)
    {
        if (channel.Name == "TIMEBASE") return Session.WriteAsync($":TIM:SCAL {Scpi.N(value)}", ct);
        var (src, kind) = Split(channel.Name);
        return Session.WriteAsync(kind == "SCALE" ? $":{src}:SCAL {Scpi.N(value)}" : $":{src}:OFFS {Scpi.N(value)}", ct);
    }

    /// <summary>Oszilloskop hat keine Ausgänge.</summary>
    public override Task SafeStateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<WaveformSet> CaptureAsync(IReadOnlyList<string> channels, double sampleRate, int samples, CancellationToken cancellationToken)
    {
        if (channels.Count == 0) throw new InstrumentException("Keine Kanäle für die Aufnahme.");
        var sources = channels.Select(c => Channel(c)).ToList();
        if (sources.Any(c => !c.Access.HasFlag(ChannelAccess.Waveform)))
            throw new InstrumentException($"{Model}: Kurvenaufnahme nur auf CHAN1…CHAN{_count}.");
        var duration = samples / sampleRate;
        var setup = $":TIM:REF LEFT;:TIM:SCAL {Scpi.N(duration / 10)};:WAV:POIN:MODE RAW";
        setup += _options.Text("Triggerquelle") is { } trig
            ? $";:TRIG:SWE NORM;:TRIG:EDGE:SOUR {trig}" + (_options.Number("Triggerpegel") is { } level ? $";:TRIG:EDGE:LEV {Scpi.N(level)}" : "")
            : ";:TRIG:SWE AUTO";
        await Session.WriteAsync(setup, cancellationToken).ConfigureAwait(false);
        var start = DateTimeOffset.Now;
        // Eine Erfassung für alle Kanäle → gemeinsame Zeitbasis
        await Session.WriteAsync(":DIG " + string.Join(",", sources.Select(s => s.Name)), cancellationToken).ConfigureAwait(false);
        await Session.WaitCompleteAsync(TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(duration * 4), cancellationToken).ConfigureAwait(false);
        var sync = $"{Model}-{Interlocked.Increment(ref _captures)}";
        var result = new List<Waveform>();
        foreach (var src in sources)
        {
            await Session.WriteAsync($":WAV:SOUR {src.Name};:WAV:FORM WORD;:WAV:BYT LSBF;:WAV:UNS 0;:WAV:POIN {samples}", cancellationToken).ConfigureAwait(false);
            var pre = (await Session.QueryAsync(":WAV:PRE?", cancellationToken).ConfigureAwait(false)).Split(',')
                .Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (pre.Length < 10) throw new InstrumentException($":WAV:PRE? lieferte {pre.Length} statt 10 Werte.");
            var (xinc, xorig, yinc, yorig, yref) = (pre[4], pre[5], pre[7], pre[8], pre[9]);
            SampleBufferWriter? writer = null;
            SampleBuffer data;
            byte? carry = null;
            try
            {
                await Session.QueryBlockStreamAsync(":WAV:DATA?", length =>
                {
                    if (length % 2 != 0) throw new InstrumentException("WORD-Kurve hat ungerade Byteanzahl.");
                    writer = new SampleBufferWriter(length / 2);
                }, memory =>
                {
                    var bytes = memory.Span;
                    var values = new double[(bytes.Length + (carry is null ? 0 : 1)) / 2];
                    var offset = 0;
                    var index = 0;
                    if (carry is { } first && bytes.Length > 0)
                    {
                        values[index++] = ((short)(first | bytes[offset++] << 8) - yref) * yinc + yorig;
                        carry = null;
                    }
                    while (offset + 1 < bytes.Length)
                    {
                        values[index++] = (BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) - yref) * yinc + yorig;
                        offset += 2;
                    }
                    if (offset < bytes.Length) carry = bytes[offset];
                    writer!.Append(values);
                }, cancellationToken, Session.Timeout + TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                data = writer!.Complete();
            }
            finally { writer?.Dispose(); }
            var rate = 1 / xinc;
            var quality = string.Create(CultureInfo.InvariantCulture,
                $"{Model}: {data.Count} Punkte, {rate:0.###} Hz, Start {xorig:G6} s");
            if (data.Count != samples || Math.Abs(rate - sampleRate) > 1e-6 * sampleRate)
                quality += string.Create(CultureInfo.InvariantCulture, $" (angefordert {samples} @ {sampleRate:0.###} Hz – Gerätewerte übernommen)");
            result.Add(new Waveform(src.Name, "V", rate, start, data, Quality: quality, SyncGroup: sync));
        }
        return new WaveformSet(result);
    }
}
