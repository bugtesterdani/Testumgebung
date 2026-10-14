/**
 * @file
 * @brief Bindet Byte- und Modbus-Verbindungen als Geräte an die Hardwarevermittlung an.
 */

using System.Collections.Concurrent;
using System.Globalization;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>
/// Basis der Kommunikationsgeräte (Adapterfamilie „Kommunikationsgeräte“, Abschnitt 7): Treibervertrag wie bei Messgeräten
/// (Gerät testen, Protokoll, sicherer Zustand), eine Transaktion zur Zeit.
/// </summary>
public abstract class CommDevice : IInstrumentDriver
{
    private readonly ConcurrentQueue<string> _log = new();
    protected readonly SemaphoreSlim Gate = new(1, 1);

    protected CommDevice(string adapter, string model, string resource, InstrumentOptions options)
    {
        Adapter = adapter;
        Model = model;
        Resource = resource;
        Options = options;
    }

    public string Adapter { get; }
    public string Model { get; }
    public string Resource { get; protected set; }
    public virtual string Name => $"{Adapter} ({Resource})";
    protected InstrumentOptions Options { get; set; }

    public IReadOnlyList<string> Log => _log.ToList();

    protected void Trace(string direction, string text)
    {
        _log.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {direction} {text}");
        while (_log.Count > 400) _log.TryDequeue(out _);
    }

    public abstract IReadOnlyList<InstrumentChannel> Channels { get; }

    /// <summary>Kanalnamen sind bei Kommunikationsgeräten frei (Modbus: Adresse); Standard: jeder Name.</summary>
    public virtual bool AcceptsChannel(string channel) => true;

    public virtual ChannelCapabilities GetCapabilities(string channel) => new("", double.MinValue, double.MaxValue);

    public virtual Task<double> ReadAsync(string channel, CancellationToken cancellationToken)
        => throw new CommunicationException($"{Adapter}: Zahlenwerte über die Bausteine „Kommunikation“ (Abfrage, Empfangen) lesen.");

    public virtual Task WriteAsync(string channel, double value, CancellationToken cancellationToken)
        => throw new CommunicationException($"{Adapter}: Werte über die Bausteine „Kommunikation“ (Senden) schreiben.");

    public abstract Task SafeStateAsync(CancellationToken cancellationToken);

    public abstract Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken);

    protected static CancellationToken SafeToken(CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        return cts.Token;
    }

    protected async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public virtual void Dispose()
    {
        Gate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Byte-Strom-Gerät: RS-232/RS-485 (Win32), TCP-Client, UDP. Optionen: Baud, Datenbits, Parität, Stoppbits, Handshake, RS485
/// (seriell); Verbindungszeit (TCP); Lokaler Port (UDP); Prüfbefehl und Zeilenende für „Gerät testen“; Sicherer Zustand
/// (Befehle, getrennt durch „|“, z. B. OUTP OFF\n).
/// </summary>
public sealed class StreamDevice : CommDevice, IByteStreamAdapter, IConfigurableConnection
{
    public const string SerialAdapter = "Seriell (RS-232/RS-485)";
    public const string TcpAdapter = "TCP-Client";
    public const string UdpAdapter = "UDP";
    public const string TelnetAdapter = "Telnet";

    private IStreamTransport _transport;
    private StreamReceiver _receiver;
    private string _configuration = "";

    public StreamDevice(string adapter, string resource, InstrumentOptions options, IStreamTransport? transport = null)
        : base(adapter, adapter switch { SerialAdapter => "RS-232/RS-485", TcpAdapter => "TCP", _ => "UDP" }, resource, options)
    {
        _transport = transport ?? adapter switch
        {
            SerialAdapter => new SerialTransport(SerialSettings.From(resource, options)),
            TcpAdapter => Tcp(resource, options),
            TelnetAdapter => new TelnetTransport(Tcp(resource, options)),
            UdpAdapter => Udp(resource, options),
            _ => throw new InstrumentException($"Unbekannter Kommunikationsadapter „{adapter}“."),
        };
        _receiver = new StreamReceiver(_transport);
    }

    public Task ConfigureAsync(string endpoint, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
        => LockedAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = string.Join("\n", options.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
            if (Resource == endpoint && signature == _configuration) return 0;
            var updated = options.Count == 0 ? Options : new InstrumentOptions(options);
            var transport = Adapter switch
            {
                SerialAdapter => (IStreamTransport)new SerialTransport(SerialSettings.From(endpoint, updated)),
                TcpAdapter => Tcp(endpoint, updated),
                TelnetAdapter => new TelnetTransport(Tcp(endpoint, updated)),
                UdpAdapter => Udp(endpoint, updated),
                _ => throw new CommunicationException("Adapter unterstützt keinen Zielwechsel."),
            };
            _transport.Dispose();
            _transport = transport;
            _receiver = new StreamReceiver(transport);
            Options = updated;
            Resource = endpoint;
            _configuration = signature;
            await Task.CompletedTask;
            return 0;
        }, cancellationToken);

    private static TcpTransport Tcp(string resource, InstrumentOptions o)
    {
        var (host, port) = Endpoints.Parse(resource, 23);
        return new TcpTransport(host, port, TimeSpan.FromMilliseconds(o.Number("Verbindungszeit") ?? 3000), TlsSettings.From(host, o));
    }

    private static UdpTransport Udp(string resource, InstrumentOptions o)
    {
        var (host, port) = Endpoints.Parse(resource, 5000);
        return new UdpTransport(host, port, (int)(o.Number("Lokaler Port") ?? 0));
    }

    public override string Name => $"{Adapter} {_transport.Description}";

    public override IReadOnlyList<InstrumentChannel> Channels { get; } =
        [new("Daten", "", 0, 0, ChannelAccess.ReadWrite, "Byte-Strom (Senden/Empfangen/Abfrage)")];

    public async Task SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => await LockedAsync(async () =>
        {
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            Trace("→", CommText.Trace(data.Span));
            await _transport.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            return 0;
        }, cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> ReceiveAsync(string channel, ReceiveRule rule, CancellationToken cancellationToken)
        => await LockedAsync(async () =>
        {
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var data = await _receiver.ReceiveAsync(rule, cancellationToken).ConfigureAwait(false);
                Trace("←", CommText.Trace(data));
                return data;
            }
            catch (CommunicationTimeoutException ex)
            {
                Trace("←", $"Zeitüberschreitung ({CommText.Trace(ex.Partial)})");
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

    public void DiscardInput(string channel)
    {
        if (_transport.IsOpen) _receiver.Discard();
    }

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        if (Options.Text("Sicherer Zustand") is not { } commands) return;
        var token = SafeToken(cancellationToken);
        foreach (var command in commands.Split('|', StringSplitOptions.RemoveEmptyEntries))
            await SendAsync("", CommText.Parse(command.Trim(), hex: false), token).ConfigureAwait(false);
    }

    public override async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();
        try
        {
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            details.Add("Geöffnet: " + _transport.Description);
            if (Options.Text("Prüfbefehl") is { } command)
            {
                var end = CommText.Parse(Options.Text("Zeilenende") ?? "\\r\\n", hex: false);
                DiscardInput("");
                await SendAsync("", (byte[])[.. CommText.Parse(command, hex: false), .. end], cancellationToken).ConfigureAwait(false);
                var answer = await ReceiveAsync("", new ReceiveRule(end.Length > 0 ? end : null, null, TimeSpan.FromSeconds(2)), cancellationToken).ConfigureAwait(false);
                return new InstrumentProbe(true, CommText.Format(answer, hex: false), details, Log);
            }
            details.Add("Ohne Option „Prüfbefehl“ wird nur die Schnittstelle geöffnet.");
            return new InstrumentProbe(true, _transport.Description, details, Log);
        }
        catch (CommunicationException ex)
        {
            details.Add(ex.Message);
            return new InstrumentProbe(false, "nicht erreichbar", details, Log);
        }
        finally
        {
            if (Adapter == SerialAdapter) _transport.Close(); // COM-Port nicht blockieren
        }
    }

    public override void Dispose()
    {
        _transport.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Modbus RTU (seriell, RS-485) und Modbus TCP (Port 502), optional RTU über TCP (Gateway). Kanal = Geräteadresse (Unit-ID);
/// Messen/Ausgeben über Kanal „[Unit/]HR100[:Typ]“ (z. B. „1/IR10:float32“). Optionen: serielle Einstellungen, Einheit
/// (Standard-Unit-ID), Wiederholungen, Rahmenpause [ms], Wortfolge (hoch/niedrig), RTU über TCP, Sicherer Zustand
/// („HR10=0;C1=0“).
/// </summary>
public sealed class ModbusDevice : CommDevice, IModbusAdapter
{
    public const string RtuAdapter = "Modbus RTU";
    public const string TcpAdapter = "Modbus TCP";

    private readonly IStreamTransport _transport;
    private readonly StreamReceiver _receiver;
    private readonly bool _rtu;
    private readonly TimeSpan _timeout;
    private readonly int _retries;
    private readonly TimeSpan _frameGap;
    private readonly byte _defaultUnit;
    private ushort _transaction;
    private DateTime _lastFrame = DateTime.MinValue;

    public ModbusDevice(string adapter, string resource, InstrumentOptions options, int? timeoutMs = null, IStreamTransport? transport = null)
        : base(adapter, adapter == RtuAdapter ? "RTU" : "TCP", resource, options)
    {
        var rtuOverTcp = adapter == TcpAdapter && options.Flag("RTU über TCP");
        _rtu = adapter == RtuAdapter || rtuOverTcp;
        if (transport is not null) _transport = transport;
        else if (adapter == RtuAdapter)
        {
            var serial = SerialSettings.From(resource, options);
            _transport = new SerialTransport(serial);
            _frameGap = TimeSpan.FromTicks(Math.Max(serial.CharTime.Ticks * 35 / 10, TimeSpan.FromMilliseconds(2).Ticks));
        }
        else
        {
            var (host, port) = Endpoints.Parse(resource, 502);
            _transport = new TcpTransport(host, port, TimeSpan.FromMilliseconds(options.Number("Verbindungszeit") ?? 3000));
        }
        if (options.Number("Rahmenpause") is { } gap) _frameGap = TimeSpan.FromMilliseconds(gap);
        _receiver = new StreamReceiver(_transport);
        _timeout = TimeSpan.FromMilliseconds(timeoutMs ?? 1000);
        _retries = (int)(options.Number("Wiederholungen") ?? 1);
        _defaultUnit = (byte)(options.Number("Einheit") ?? 1);
    }

    public override string Name => $"{Adapter} {_transport.Description}";

    public override IReadOnlyList<InstrumentChannel> Channels { get; } =
    [
        new("1", "", 0, 0, ChannelAccess.ReadWrite, "Geräteadresse 1 (Bausteine Modbus lesen/schreiben)"),
        new("1/HR0", "", short.MinValue, ushort.MaxValue, ChannelAccess.ReadWrite, "Holding-Register 0 als Zahl (Messung/Ausgabe), Typ mit :float32 usw."),
        new("1/IR0", "", short.MinValue, ushort.MaxValue, ChannelAccess.Read, "Input-Register 0 als Zahl"),
        new("1/C0", "bool", 0, 1, ChannelAccess.ReadWrite, "Coil 0"),
        new("1/DI0", "bool", 0, 1, ChannelAccess.Read, "Diskreter Eingang 0"),
    ];

    /// <summary>Kanal „1“ … „247“ oder Punkt „[Unit/]Adresse[:Typ]“.</summary>
    public override bool AcceptsChannel(string channel)
    {
        try
        {
            _ = ParsePoint(channel);
            return true;
        }
        catch (FormatException)
        {
            return byte.TryParse(channel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) && u is >= 1 and <= 247;
        }
    }

    private byte Unit(string channel)
    {
        var c = channel.Split('/')[0].Trim();
        return byte.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) && u <= 247 ? u : _defaultUnit;
    }

    private (byte Unit, ModbusArea Area, int Address, ModbusDataType Type) ParsePoint(string channel)
    {
        var unit = _defaultUnit;
        var point = channel.Trim();
        var slash = point.IndexOf('/');
        if (slash >= 0)
        {
            if (!byte.TryParse(point[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out unit)) throw new FormatException("Unit-ID ungültig.");
            point = point[(slash + 1)..];
        }
        var colon = point.IndexOf(':');
        var (area, address) = ModbusCodec.ParseAddress(colon < 0 ? point : point[..colon]);
        var type = colon < 0 ? (area is ModbusArea.Coil or ModbusArea.DiscreteInput ? ModbusDataType.Bool : ModbusDataType.UInt16) : ModbusCodec.ParseType(point[(colon + 1)..]);
        if (type == ModbusDataType.Text) throw new FormatException("Text ist kein Zahlenwert.");
        return (unit, area, address, type);
    }

    private bool LowWordFirst => (Options.Text("Wortfolge") ?? "").StartsWith("niedrig", StringComparison.OrdinalIgnoreCase);

    public override async Task<double> ReadAsync(string channel, CancellationToken cancellationToken)
    {
        var (unit, area, address, type) = Point(channel);
        if (area is ModbusArea.Coil or ModbusArea.DiscreteInput)
            return (await ReadBitsAsync(unit.ToString(CultureInfo.InvariantCulture), area, address, 1, cancellationToken).ConfigureAwait(false))[0] ? 1 : 0;
        var regs = await ReadRegistersAsync(unit.ToString(CultureInfo.InvariantCulture), area, address, ModbusCodec.Registers(type), cancellationToken).ConfigureAwait(false);
        return Convert.ToDouble(ModbusCodec.Decode(regs, type, LowWordFirst), CultureInfo.InvariantCulture);
    }

    public override async Task WriteAsync(string channel, double value, CancellationToken cancellationToken)
    {
        var (unit, area, address, type) = Point(channel);
        var u = unit.ToString(CultureInfo.InvariantCulture);
        if (area == ModbusArea.Coil) await WriteBitsAsync(u, address, [value >= 0.5], cancellationToken).ConfigureAwait(false);
        else if (area == ModbusArea.HoldingRegister) await WriteRegistersAsync(u, address, ModbusCodec.Encode(type is ModbusDataType.Float32 or ModbusDataType.Float64 ? value : Math.Round(value), type, LowWordFirst), cancellationToken).ConfigureAwait(false);
        else throw new CommunicationException($"{channel}: nur Holding-Register und Coils sind schreibbar.");
    }

    private (byte, ModbusArea, int, ModbusDataType) Point(string channel)
    {
        try
        {
            return ParsePoint(channel);
        }
        catch (FormatException ex)
        {
            throw new CommunicationException($"Modbus-Kanal „{channel}“: {ex.Message} (z. B. 1/HR100:float32).");
        }
    }

    public async Task<ushort[]> ReadRegistersAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 125) throw new CommunicationException("Register lesen: 1–125 Register je Anfrage.");
        var function = area switch
        {
            ModbusArea.HoldingRegister => (byte)3,
            ModbusArea.InputRegister => (byte)4,
            _ => throw new CommunicationException("Register lesen: HR oder IR."),
        };
        var data = await TransactAsync(Unit(channel), function, [.. U16(address), .. U16(count)], cancellationToken).ConfigureAwait(false);
        if (data.Length < 1 || data[0] != count * 2 || data.Length != 1 + count * 2) throw new CommunicationException($"Modbus: Antwortlänge {data.Length} passt nicht zu {count} Registern.");
        return Enumerable.Range(0, count).Select(i => (ushort)((data[1 + 2 * i] << 8) | data[2 + 2 * i])).ToArray();
    }

    public async Task<bool[]> ReadBitsAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken)
    {
        if (count is < 1 or > 2000) throw new CommunicationException("Bits lesen: 1–2000 je Anfrage.");
        var function = area switch
        {
            ModbusArea.Coil => (byte)1,
            ModbusArea.DiscreteInput => (byte)2,
            _ => throw new CommunicationException("Bits lesen: C oder DI."),
        };
        var data = await TransactAsync(Unit(channel), function, [.. U16(address), .. U16(count)], cancellationToken).ConfigureAwait(false);
        if (data.Length < 1 || data[0] != (count + 7) / 8) throw new CommunicationException("Modbus: Antwortlänge passt nicht zur Bitanzahl.");
        return Enumerable.Range(0, count).Select(i => (data[1 + i / 8] >> (i % 8) & 1) != 0).ToArray();
    }

    public async Task WriteRegistersAsync(string channel, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
    {
        if (values.Count is < 1 or > 123) throw new CommunicationException("Register schreiben: 1–123 je Anfrage.");
        if (values.Count == 1)
            await TransactAsync(Unit(channel), 6, [.. U16(address), .. U16(values[0])], cancellationToken).ConfigureAwait(false);
        else
            await TransactAsync(Unit(channel), 16, [.. U16(address), .. U16(values.Count), (byte)(values.Count * 2), .. values.SelectMany(v => U16(v))], cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteBitsAsync(string channel, int address, IReadOnlyList<bool> values, CancellationToken cancellationToken)
    {
        if (values.Count is < 1 or > 1968) throw new CommunicationException("Coils schreiben: 1–1968 je Anfrage.");
        if (values.Count == 1)
        {
            await TransactAsync(Unit(channel), 5, [.. U16(address), values[0] ? (byte)0xFF : (byte)0, 0], cancellationToken).ConfigureAwait(false);
            return;
        }
        var packed = new byte[(values.Count + 7) / 8];
        for (var i = 0; i < values.Count; i++)
            if (values[i]) packed[i / 8] |= (byte)(1 << (i % 8));
        await TransactAsync(Unit(channel), 15, [.. U16(address), .. U16(values.Count), (byte)packed.Length, .. packed], cancellationToken).ConfigureAwait(false);
    }

    private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

    /// <summary>Anfrage senden, Antwort-PDU-Daten (ohne Funktionscode) liefern; Wiederholung bei Zeitüberschreitung/Prüfsummenfehler.</summary>
    private Task<byte[]> TransactAsync(byte unit, byte function, byte[] data, CancellationToken cancellationToken)
        => LockedAsync(async () =>
        {
            Exception? last = null;
            for (var attempt = 0; attempt <= _retries; attempt++)
            {
                try
                {
                    await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
                    return _rtu ? await RtuAsync(unit, function, data, cancellationToken).ConfigureAwait(false)
                        : await TcpAsync(unit, function, data, cancellationToken).ConfigureAwait(false);
                }
                catch (ModbusException)
                {
                    throw; // Ausnahmeantwort: Gerät hat geantwortet, keine Wiederholung
                }
                catch (CommunicationException ex)
                {
                    last = ex;
                    Trace("!", ex.Message + (attempt < _retries ? " – Wiederholung" : ""));
                    _receiver.Discard();
                }
            }
            throw last is CommunicationTimeoutException t ? t : new CommunicationException($"Modbus: {last!.Message}", last);
        }, cancellationToken);

    private async Task<byte[]> TcpAsync(byte unit, byte function, byte[] data, CancellationToken ct)
    {
        var id = ++_transaction;
        var pdu = new byte[1 + data.Length];
        pdu[0] = function;
        data.CopyTo(pdu, 1);
        byte[] frame = [.. U16(id), 0, 0, .. U16(pdu.Length + 1), unit, .. pdu];
        _receiver.Discard();
        Trace("→", CommText.Format(frame, hex: true));
        await _transport.WriteAsync(frame, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + _timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new CommunicationTimeoutException($"Modbus TCP: keine Antwort innerhalb von {_timeout.TotalMilliseconds:0} ms.", []);
            var header = await _receiver.ReceiveAsync(new ReceiveRule(null, 7, remaining), ct).ConfigureAwait(false);
            var length = (header[4] << 8) | header[5];
            if (length is < 2 or > 254) throw new CommunicationException($"Modbus TCP: ungültige Länge {length}.");
            var body = await _receiver.ReceiveAsync(new ReceiveRule(null, length - 1, _timeout), ct).ConfigureAwait(false);
            Trace("←", CommText.Format([.. header, .. body], hex: true));
            var rid = (ushort)((header[0] << 8) | header[1]);
            if (rid != id || header[6] != unit) continue; // verspätete Antwort einer früheren Anfrage
            return Pdu(function, body);
        }
    }

    private async Task<byte[]> RtuAsync(byte unit, byte function, byte[] data, CancellationToken ct)
    {
        var wait = _lastFrame + _frameGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        byte[] body = [unit, function, .. data];
        var crc = Crc16(body);
        byte[] frame = [.. body, (byte)crc, (byte)(crc >> 8)];
        _receiver.Discard();
        Trace("→", CommText.Format(frame, hex: true));
        await _transport.WriteAsync(frame, ct).ConfigureAwait(false);
        if (unit == 0)
        {
            _lastFrame = DateTime.UtcNow;
            return []; // Broadcast: keine Antwort
        }
        try
        {
            var head = await _receiver.ReceiveAsync(new ReceiveRule(null, 2, _timeout), ct).ConfigureAwait(false);
            int rest;
            if ((head[1] & 0x80) != 0) rest = 3;
            else if (head[1] is 1 or 2 or 3 or 4)
            {
                var count = await _receiver.ReceiveAsync(new ReceiveRule(null, 1, _timeout), ct).ConfigureAwait(false);
                head = [.. head, count[0]];
                rest = count[0] + 2;
            }
            else rest = 6;
            var tail = await _receiver.ReceiveAsync(new ReceiveRule(null, rest, _timeout), ct).ConfigureAwait(false);
            byte[] response = [.. head, .. tail];
            Trace("←", CommText.Format(response, hex: true));
            var received = (ushort)(response[^2] | (response[^1] << 8));
            if (Crc16(response.AsSpan(0, response.Length - 2)) != received) throw new CommunicationException("Modbus RTU: Prüfsummenfehler (CRC).");
            if (response[0] != unit) throw new CommunicationException($"Modbus RTU: Antwort von Unit {response[0]} statt {unit}.");
            return Pdu(function, response.AsSpan(1, response.Length - 3).ToArray());
        }
        finally
        {
            _lastFrame = DateTime.UtcNow;
        }
    }

    /// <summary>PDU (Funktionscode + Daten) prüfen; Ausnahmeantwort → <see cref="ModbusException"/>.</summary>
    private static byte[] Pdu(byte function, byte[] pdu)
    {
        if (pdu.Length == 0) throw new CommunicationException("Modbus: leere Antwort.");
        if (pdu[0] == (function | 0x80)) throw new ModbusException(function, pdu.Length > 1 ? pdu[1] : (byte)0);
        if (pdu[0] != function) throw new CommunicationException($"Modbus: Antwort mit Funktion {pdu[0]:X2} statt {function:X2}.");
        return pdu[1..];
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        if (Options.Text("Sicherer Zustand") is not { } list) return;
        var token = SafeToken(cancellationToken);
        foreach (var item in list.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                await WriteAsync(parts[0], v, token).ConfigureAwait(false);
        }
    }

    public override async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();
        try
        {
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            details.Add("Geöffnet: " + _transport.Description + (_rtu ? $", Rahmenpause {_frameGap.TotalMilliseconds:0.#} ms" : ""));
            if (Options.Text("Prüfadresse") is { } point)
            {
                var value = await ReadAsync(point, cancellationToken).ConfigureAwait(false);
                return new InstrumentProbe(true, $"Unit {_defaultUnit}: {point} = {value.ToString("G10", CultureInfo.InvariantCulture)}", details, Log);
            }
            details.Add("Mit Option „Prüfadresse“ (z. B. 1/HR0) wird zusätzlich ein Register gelesen.");
            return new InstrumentProbe(true, _transport.Description, details, Log);
        }
        catch (CommunicationException ex)
        {
            details.Add(ex.Message);
            return new InstrumentProbe(false, "nicht erreichbar", details, Log);
        }
        finally
        {
            if (Adapter == RtuAdapter) _transport.Close();
        }
    }

    public override void Dispose()
    {
        _transport.Dispose();
        base.Dispose();
    }
}
