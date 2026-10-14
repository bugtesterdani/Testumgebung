/**
 * @file
 * @brief Adaptiert CAN-Hardwarebibliotheken und deren unterschiedliche Rahmenlayouts an einen gemeinsamen CAN-Vertrag.
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>Bitraten eines CAN-Busses.</summary>
public sealed record CanBusSettings(int Bitrate, bool Fd, int DataBitrate, string? FdParameters, bool AcceptVirtual)
{
    /// <summary>Optionen: Bitrate (125k/250k/500k/1M), FD (ja), Datenbitrate (2M/4M/5M/8M), FD-Parameter (PCAN-Zeichenkette), Virtuell (Kvaser).</summary>
    public static CanBusSettings From(InstrumentOptions o)
        => new(ParseRate(o.Text("Bitrate") ?? "500k"), o.Flag("FD"), ParseRate(o.Text("Datenbitrate") ?? "2M"), o.Text("FD-Parameter"), o.Flag("Virtuell"));

    public static int ParseRate(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace("bit/s", "").Replace("bps", "").Trim();
        var factor = t.EndsWith('m') ? 1_000_000 : t.EndsWith('k') ? 1000 : 1;
        if (factor > 1) t = t[..^1];
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new InstrumentException($"Bitrate „{text}“ ungültig (z. B. 500k, 1M).");
        return (int)(v * factor);
    }

    public override string ToString() => $"{Bitrate / 1000} kbit/s{(Fd ? $", FD {DataBitrate / 1000} kbit/s" : "")}";
}

/// <summary>Treiberanbindung eines CAN-Interfaces (PEAK, Kvaser, virtueller Bus).</summary>
public interface ICanBackend : IDisposable
{
    string Description { get; }

    void Open(CanBusSettings settings);

    void Send(CanFrame frame);

    /// <summary>Nächsten Rahmen lesen; null nach <paramref name="timeoutMs"/> ohne Rahmen.</summary>
    CanFrame? Read(int timeoutMs);

    string Status();

    void Close();
}

/// <summary>
/// CAN-/CAN-FD-Gerät. Ein Hintergrundleser puffert empfangene Rahmen (bis 10 000); Empfangen sucht den nächsten passenden
/// Rahmen ab dem letzten Verwerfen. Optionen siehe <see cref="CanBusSettings"/> und „Sicherer Zustand“ (Rahmen „0x100 00 00“,
/// getrennt durch „|“).
/// </summary>
public sealed class CanDevice : CommDevice, ICanAdapter
{
    public const string PcanAdapter = "CAN – PEAK PCAN-Basic";
    public const string KvaserAdapter = "CAN – Kvaser CANlib";
    public const string VirtualAdapter = "CAN – virtueller Bus (ohne Hardware)";
    public const string VectorAdapter = "CAN – Vector XL Driver Library";

    private readonly ICanBackend _backend;
    private readonly CanBusSettings _settings;
    private readonly List<CanFrame> _buffer = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _bufferGate = new();
    private CancellationTokenSource? _reader;
    private Task? _readerTask;
    private string? _readerError;

    public CanDevice(string adapter, string resource, InstrumentOptions options, ICanBackend? backend = null)
        : base(adapter, adapter switch { PcanAdapter => "PCAN-Basic", KvaserAdapter => "CANlib", VectorAdapter => "XL API", _ => "virtuell" }, resource, options)
    {
        _settings = CanBusSettings.From(options);
        _backend = backend ?? adapter switch
        {
            PcanAdapter => new PcanBackend(resource),
            KvaserAdapter => new KvaserBackend(resource),
            VectorAdapter => new VectorXlBackend(resource),
            VirtualAdapter => new VirtualCanBackend(resource),
            _ => throw new InstrumentException($"Unbekannter CAN-Adapter „{adapter}“."),
        };
    }

    public override string Name => $"{Adapter} {_backend.Description}";

    public override IReadOnlyList<InstrumentChannel> Channels { get; } =
        [new("Bus", "", 0, 0, ChannelAccess.ReadWrite, "CAN-Bus (Bausteine CAN senden/empfangen)")];

    private void EnsureOpen()
    {
        lock (_bufferGate)
        {
            if (_readerTask is { IsCompleted: false }) return;
            if (_readerError is { } error)
            {
                _readerError = null;
                Trace("!", "Leser neu gestartet nach: " + error);
            }
            try
            {
                _backend.Open(_settings);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new CommunicationException($"{Adapter}: Treiber nicht installiert ({ex.Message}).", ex);
            }
            Trace("=", $"geöffnet {_backend.Description}, {_settings}");
            _reader = new CancellationTokenSource();
            var token = _reader.Token;
            _readerTask = Task.Factory.StartNew(() => ReadLoop(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void ReadLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_backend.Read(50) is not { } frame) continue;
                lock (_bufferGate)
                {
                    _buffer.Add(frame with { Time = DateTimeOffset.Now });
                    if (_buffer.Count > 10_000) _buffer.RemoveRange(0, _buffer.Count - 10_000);
                }
                _signal.Release();
            }
        }
        catch (Exception ex) when (ex is CommunicationException or InvalidOperationException or ObjectDisposedException)
        {
            _readerError = ex.Message;
            Trace("!", ex.Message);
            _signal.Release();
        }
    }

    public Task SendAsync(string channel, CanFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Validate() is { } error) throw new CommunicationException(error);
        if (frame.Fd && !_settings.Fd) throw new CommunicationException($"{Adapter}: CAN-FD-Rahmen, aber Option „FD“ ist nicht gesetzt.");
        EnsureOpen();
        Trace("→", frame.ToString());
        _backend.Send(frame);
        return Task.CompletedTask;
    }

    public Task<CanFrame> ReceiveAsync(string channel, CanFilter filter, TimeSpan timeout, CancellationToken cancellationToken)
        => ReceiveAnyAsync(channel, [filter], timeout, cancellationToken);

    public async Task<CanFrame> ReceiveAnyAsync(string channel, IReadOnlyList<CanFilter> filters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureOpen();
        var filter = filters[0];
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_bufferGate)
            {
                var index = _buffer.FindIndex(f => filters.Any(x => x.Matches(f)));
                if (index >= 0)
                {
                    var frame = _buffer[index];
                    _buffer.RemoveAt(index);
                    Trace("←", frame.ToString());
                    return frame;
                }
                if (_readerError is { } error) throw new CommunicationException($"{Adapter}: {error}");
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Trace("←", $"Zeitüberschreitung (ID 0x{filter.Id:X})");
                throw new CommunicationTimeoutException($"Kein CAN-Rahmen mit ID 0x{filter.Id:X} innerhalb von {timeout.TotalMilliseconds:0} ms ({_backend.Status()}).", []);
            }
            await _signal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    public void DiscardInput(string channel)
    {
        lock (_bufferGate) _buffer.Clear();
    }

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        if (Options.Text("Sicherer Zustand") is not { } frames) return;
        foreach (var text in frames.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
            var idText = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
            var id = uint.Parse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            await SendAsync("", new CanFrame(id, parts.Length > 1 ? CommText.ParseHex(parts[1]) : [], id > 0x7FF), cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();
        try
        {
            EnsureOpen();
            details.Add($"Bus: {_settings}");
            details.Add("Status: " + _backend.Status());
            int count;
            lock (_bufferGate) count = _buffer.Count;
            details.Add($"{count} Rahmen im Empfangspuffer");
            return Task.FromResult(new InstrumentProbe(true, _backend.Description, details, Log));
        }
        catch (CommunicationException ex)
        {
            details.Add(ex.Message);
            return Task.FromResult(new InstrumentProbe(false, "nicht erreichbar", details, Log));
        }
    }

    public override void Dispose()
    {
        _reader?.Cancel();
        try
        {
            _readerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Leser endet mit dem Schließen
        }
        _backend.Dispose();
        _reader?.Dispose();
        base.Dispose();
    }
}

// ---------------------------------------------------------------------- virtueller Bus (Tests, Vorführung)

/// <summary>Virtueller CAN-Bus im Prozess: alle Teilnehmer mit derselben Ressource sehen die Rahmen der anderen.</summary>
public sealed class VirtualCanBackend(string bus) : ICanBackend
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<VirtualCanBackend, byte>> Buses = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Channels.Channel<CanFrame> _inbox = System.Threading.Channels.Channel.CreateUnbounded<CanFrame>();
    private bool _open;

    public string Description => $"virtueller Bus „{bus}“";

    public void Open(CanBusSettings settings)
    {
        Buses.GetOrAdd(bus, _ => new ConcurrentDictionary<VirtualCanBackend, byte>())[this] = 0;
        _open = true;
    }

    public void Send(CanFrame frame)
    {
        if (!_open) throw new CommunicationException("Virtueller Bus nicht geöffnet.");
        foreach (var peer in Buses[bus].Keys.Where(p => !ReferenceEquals(p, this))) peer._inbox.Writer.TryWrite(frame);
    }

    public CanFrame? Read(int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return _inbox.Reader.ReadAsync(cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public string Status() => _open ? $"{Buses[bus].Count} Teilnehmer" : "geschlossen";

    public void Close()
    {
        if (Buses.TryGetValue(bus, out var members)) members.TryRemove(this, out _);
        _open = false;
    }

    public void Dispose() => Close();
}

// ---------------------------------------------------------------------- PEAK PCAN-Basic

/// <summary>
/// PEAK-System PCAN-Basic (PCANBasic.dll, im PEAK-Treiberpaket). Ressource: PCAN_USBBUS1…16, PCAN_PCIBUS1…16, PCAN_LANBUS1…16
/// oder Handle (0x51). Klassisch über Bitratencode, FD über Bitraten-Zeichenkette (Option „FD-Parameter“ überschreibt).
/// </summary>
public sealed class PcanBackend(string resource) : ICanBackend
{
    private const uint ErrOk = 0;
    private const uint ErrQrcvEmpty = 0x20;
    private const byte MsgStandard = 0x00, MsgRtr = 0x01, MsgExtended = 0x02, MsgFd = 0x04, MsgBrs = 0x08, MsgStatus = 0x80, MsgErrFrame = 0x40;

    private readonly ushort _handle = ParseHandle(resource);
    private readonly IPcanApi _api = new PcanApi();
    internal PcanBackend(string resource, IPcanApi api) : this(resource) => _api = api;
    private bool _fd;
    private bool _open;

    public string Description => $"PCAN {resource} (0x{_handle:X})";

    public static ushort ParseHandle(string resource)
    {
        var r = resource.Trim().ToUpperInvariant();
        if (r.StartsWith("0X", StringComparison.Ordinal) && ushort.TryParse(r[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
        static int N(string s, string prefix) => int.TryParse(s[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16
            ? n : throw new InstrumentException($"PCAN-Kanal „{s}“: Nummer 1–16.");
        if (r.StartsWith("PCAN_USBBUS", StringComparison.Ordinal)) { var n = N(r, "PCAN_USBBUS"); return (ushort)(n <= 8 ? 0x51 + n - 1 : 0x509 + n - 9); }
        if (r.StartsWith("PCAN_PCIBUS", StringComparison.Ordinal)) { var n = N(r, "PCAN_PCIBUS"); return (ushort)(n <= 8 ? 0x41 + n - 1 : 0x409 + n - 9); }
        if (r.StartsWith("PCAN_LANBUS", StringComparison.Ordinal)) { var n = N(r, "PCAN_LANBUS"); return (ushort)(0x801 + n - 1); }
        throw new InstrumentException($"PCAN-Kanal „{resource}“ unbekannt (z. B. PCAN_USBBUS1).");
    }

    public static ushort BitrateCode(int bitrate) => bitrate switch
    {
        1_000_000 => 0x0014,
        800_000 => 0x0016,
        500_000 => 0x001C,
        250_000 => 0x011C,
        125_000 => 0x031C,
        100_000 => 0x432F,
        50_000 => 0x472F,
        20_000 => 0x532F,
        10_000 => 0x672F,
        _ => throw new InstrumentException($"PCAN: Bitrate {bitrate} nicht vordefiniert (10k–1M)."),
    };

    /// <summary>FD-Bitraten-Zeichenkette für 80-MHz-Takt (Abtastpunkt 80 %).</summary>
    public static string FdBitrate(int nominal, int data)
    {
        var (nbrp, nt1, nt2) = nominal switch
        {
            1_000_000 => (2, 31, 8),
            500_000 => (2, 63, 16),
            250_000 => (4, 63, 16),
            125_000 => (8, 63, 16),
            _ => throw new InstrumentException($"PCAN FD: Bitrate {nominal} ohne Vorgabe – Option „FD-Parameter“ angeben."),
        };
        var (dbrp, dt1, dt2) = data switch
        {
            1_000_000 => (2, 31, 8),
            2_000_000 => (2, 15, 4),
            4_000_000 => (1, 15, 4),
            5_000_000 => (2, 5, 2),
            8_000_000 => (1, 6, 3),
            _ => throw new InstrumentException($"PCAN FD: Datenbitrate {data} ohne Vorgabe – Option „FD-Parameter“ angeben."),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"f_clock_mhz=80,nom_brp={nbrp},nom_tseg1={nt1},nom_tseg2={nt2},nom_sjw={nt2},data_brp={dbrp},data_tseg1={dt1},data_tseg2={dt2},data_sjw={dt2}");
    }

    public void Open(CanBusSettings settings)
    {
        if (_open) return;
        _fd = settings.Fd;
        var status = _fd
            ? _api.CAN_InitializeFD(_handle, settings.FdParameters ?? FdBitrate(settings.Bitrate, settings.DataBitrate))
            : _api.CAN_Initialize(_handle, BitrateCode(settings.Bitrate), 0, 0, 0);
        Check(status, "Initialisieren");
        _open = true;
    }

    public void Send(CanFrame frame)
    {
        byte type = (byte)((frame.Extended ? MsgExtended : MsgStandard) | (frame.Remote ? MsgRtr : 0));
        if (_fd)
        {
            var msg = new Native.MsgFd { ID = frame.Id, MSGTYPE = (byte)(type | (frame.Fd ? MsgFd : 0) | (frame.BitRateSwitch ? MsgBrs : 0)), DLC = Dlc(frame.Data.Length), DATA = new byte[64] };
            frame.Data.CopyTo(msg.DATA, 0);
            Check(_api.CAN_WriteFD(_handle, ref msg), "Senden");
        }
        else
        {
            var msg = new Native.Msg { ID = frame.Id, MSGTYPE = type, LEN = (byte)frame.Data.Length, DATA = new byte[8] };
            frame.Data.CopyTo(msg.DATA, 0);
            Check(_api.CAN_Write(_handle, ref msg), "Senden");
        }
    }

    public CanFrame? Read(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            CanFrame? frame;
            uint status;
            if (_fd)
            {
                status = _api.CAN_ReadFD(_handle, out var msg, out _);
                frame = status == ErrOk && (msg.MSGTYPE & (MsgStatus | MsgErrFrame)) == 0
                    ? new CanFrame(msg.ID, msg.DATA.AsSpan(0, CanFrame.FdLengths[msg.DLC & 0x0F]).ToArray(), (msg.MSGTYPE & MsgExtended) != 0,
                        (msg.MSGTYPE & MsgFd) != 0, (msg.MSGTYPE & MsgBrs) != 0, (msg.MSGTYPE & MsgRtr) != 0)
                    : null;
            }
            else
            {
                status = _api.CAN_Read(_handle, out var msg, out _);
                frame = status == ErrOk && (msg.MSGTYPE & (MsgStatus | MsgErrFrame)) == 0
                    ? new CanFrame(msg.ID, msg.DATA.AsSpan(0, Math.Min((int)msg.LEN, 8)).ToArray(), (msg.MSGTYPE & MsgExtended) != 0, Remote: (msg.MSGTYPE & MsgRtr) != 0)
                    : null;
            }
            if (frame is not null) return frame;
            if (status != ErrOk && status != ErrQrcvEmpty && (status & ErrQrcvEmpty) == 0) Check(status, "Lesen");
            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(1);
        }
    }

    private static byte Dlc(int length) => (byte)Array.IndexOf(CanFrame.FdLengths, length);

    public string Status()
    {
        if (!_open) return "geschlossen";
        var status = _api.CAN_GetStatus(_handle);
        return status == ErrOk ? "OK" : Text(status);
    }

    private string Text(uint status)
    {
        var sb = new StringBuilder(256);
        return _api.CAN_GetErrorText(status, 0x07, sb) == ErrOk ? sb.ToString() : $"Status 0x{status:X}";
    }

    private void Check(uint status, string what)
    {
        if (status != ErrOk) throw new CommunicationException($"PCAN {what} ({resource}): {Text(status)}");
    }

    public void Close()
    {
        if (!_open) return;
        _api.CAN_Uninitialize(_handle);
        _open = false;
    }

    public void Dispose() => Close();

    internal interface IPcanApi
    {
        uint CAN_Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt);
        uint CAN_InitializeFD(ushort channel, string bitrateFd);
        uint CAN_Uninitialize(ushort channel);
        uint CAN_GetStatus(ushort channel);
        uint CAN_Read(ushort channel, out Native.Msg message, out Native.Timestamp timestamp);
        uint CAN_ReadFD(ushort channel, out Native.MsgFd message, out ulong timestamp);
        uint CAN_Write(ushort channel, ref Native.Msg message);
        uint CAN_WriteFD(ushort channel, ref Native.MsgFd message);
        uint CAN_GetErrorText(uint error, ushort language, StringBuilder buffer);
    }

    private sealed class PcanApi : IPcanApi
    {
        public uint CAN_Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt) => Native.CAN_Initialize(channel, btr0Btr1, hwType, ioPort, interrupt);
        public uint CAN_InitializeFD(ushort channel, string bitrateFd) => Native.CAN_InitializeFD(channel, bitrateFd);
        public uint CAN_Uninitialize(ushort channel) => Native.CAN_Uninitialize(channel);
        public uint CAN_GetStatus(ushort channel) => Native.CAN_GetStatus(channel);
        public uint CAN_Read(ushort channel, out Native.Msg message, out Native.Timestamp timestamp) => Native.CAN_Read(channel, out message, out timestamp);
        public uint CAN_ReadFD(ushort channel, out Native.MsgFd message, out ulong timestamp) => Native.CAN_ReadFD(channel, out message, out timestamp);
        public uint CAN_Write(ushort channel, ref Native.Msg message) => Native.CAN_Write(channel, ref message);
        public uint CAN_WriteFD(ushort channel, ref Native.MsgFd message) => Native.CAN_WriteFD(channel, ref message);
        public uint CAN_GetErrorText(uint error, ushort language, StringBuilder buffer) => Native.CAN_GetErrorText(error, language, buffer);
    }

    internal static class Native
    {
        private const string Dll = "PCANBasic.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public uint ID;
            public byte MSGTYPE;
            public byte LEN;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] DATA;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MsgFd
        {
            public uint ID;
            public byte MSGTYPE;
            public byte DLC;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] DATA;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Timestamp
        {
            public uint millis;
            public ushort millis_overflow;
            public ushort micros;
        }

        [DllImport(Dll)] public static extern uint CAN_Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern uint CAN_InitializeFD(ushort channel, string bitrateFd);
        [DllImport(Dll)] public static extern uint CAN_Uninitialize(ushort channel);
        [DllImport(Dll)] public static extern uint CAN_GetStatus(ushort channel);
        [DllImport(Dll)] public static extern uint CAN_Read(ushort channel, out Msg message, out Timestamp timestamp);
        [DllImport(Dll)] public static extern uint CAN_ReadFD(ushort channel, out MsgFd message, out ulong timestamp);
        [DllImport(Dll)] public static extern uint CAN_Write(ushort channel, ref Msg message);
        [DllImport(Dll)] public static extern uint CAN_WriteFD(ushort channel, ref MsgFd message);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern uint CAN_GetErrorText(uint error, ushort language, StringBuilder buffer);
    }
}

// ---------------------------------------------------------------------- Kvaser CANlib

/// <summary>Kvaser CANlib (canlib32.dll, Kvaser Drivers for Windows). Ressource: Kanalnummer „0“, „1“ …; Option „Virtuell“ für virtuelle Kanäle.</summary>
public sealed class KvaserBackend(string resource) : ICanBackend
{
    private const int canOK = 0, canERR_NOMSG = -2;
    private const int OpenAcceptVirtual = 0x20, OpenCanFd = 0x400;
    private const uint MsgRtr = 0x1, MsgStd = 0x2, MsgExt = 0x4, MsgErrorFrame = 0x20, FdmsgFdf = 0x010000, FdmsgBrs = 0x020000;
    private readonly IKvaserApi _api = new KvaserApi();
    internal KvaserBackend(string resource, IKvaserApi api) : this(resource) => _api = api;

    private readonly int _channel = int.TryParse(resource.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 0
        ? c : throw new InstrumentException($"Kvaser-Kanal „{resource}“: Kanalnummer 0, 1, …");
    private int _handle = -1;

    public string Description => $"Kvaser Kanal {_channel}";

    public static int Bitrate(int bitrate) => bitrate switch
    {
        1_000_000 => -1,
        500_000 => -2,
        250_000 => -3,
        125_000 => -4,
        100_000 => -5,
        62_500 => -6,
        50_000 => -7,
        83_333 => -8,
        10_000 => -9,
        _ => throw new InstrumentException($"Kvaser: Bitrate {bitrate} nicht vordefiniert."),
    };

    public static int FdNominal(int bitrate) => bitrate switch
    {
        500_000 => -1000,
        1_000_000 => -1001,
        _ => throw new InstrumentException($"Kvaser FD: Nominalbitrate {bitrate} – 500k oder 1M."),
    };

    public static int FdData(int bitrate) => bitrate switch
    {
        500_000 => -1000,
        1_000_000 => -1001,
        2_000_000 => -1002,
        4_000_000 => -1003,
        8_000_000 => -1004,
        _ => throw new InstrumentException($"Kvaser FD: Datenbitrate {bitrate} – 500k, 1M, 2M, 4M oder 8M."),
    };

    public void Open(CanBusSettings settings)
    {
        if (_handle >= 0) return;
        _api.canInitializeLibrary();
        var flags = (settings.AcceptVirtual ? OpenAcceptVirtual : 0) | (settings.Fd ? OpenCanFd : 0);
        var handle = _api.canOpenChannel(_channel, flags);
        if (handle < 0) Check(handle, "Kanal öffnen");
        try
        {
            if (settings.Fd)
            {
                Check(_api.canSetBusParams(handle, FdNominal(settings.Bitrate), 0, 0, 0, 0, 0), "Bitrate");
                Check(_api.canSetBusParamsFd(handle, FdData(settings.DataBitrate), 0, 0, 0), "Datenbitrate");
            }
            else
            {
                Check(_api.canSetBusParams(handle, Bitrate(settings.Bitrate), 0, 0, 0, 0, 0), "Bitrate");
            }
            Check(_api.canBusOn(handle), "Bus ein");
        }
        catch
        {
            _api.canClose(handle);
            throw;
        }
        _handle = handle;
    }

    public void Send(CanFrame frame)
    {
        var flags = (frame.Extended ? MsgExt : MsgStd) | (frame.Remote ? MsgRtr : 0) | (frame.Fd ? FdmsgFdf : 0) | (frame.BitRateSwitch ? FdmsgBrs : 0);
        var data = new byte[Math.Max(8, frame.Data.Length)];
        frame.Data.CopyTo(data, 0);
        Check(_api.canWrite(_handle, (int)frame.Id, data, (uint)frame.Data.Length, flags), "Senden");
    }

    public CanFrame? Read(int timeoutMs)
    {
        var data = new byte[64];
        var status = _api.canReadWait(_handle, out var id, data, out var dlc, out var flags, out _, (uint)timeoutMs);
        if (status == canERR_NOMSG) return null;
        Check(status, "Lesen");
        if ((flags & MsgErrorFrame) != 0) return null;
        var fd = (flags & FdmsgFdf) != 0;
        var length = (int)Math.Min(dlc, fd ? 64u : 8u);
        return new CanFrame((uint)id, data.AsSpan(0, length).ToArray(), (flags & MsgExt) != 0, fd, (flags & FdmsgBrs) != 0, (flags & MsgRtr) != 0);
    }

    public string Status() => _handle >= 0 ? "Bus ein" : "geschlossen";

    private void Check(int status, string what)
    {
        if (status >= canOK) return;
        var sb = new StringBuilder(256);
        _api.canGetErrorText(status, sb, (uint)sb.Capacity);
        throw new CommunicationException($"Kvaser {what} (Kanal {_channel}): {sb} ({status})");
    }

    public void Close()
    {
        if (_handle < 0) return;
        _api.canBusOff(_handle);
        _api.canClose(_handle);
        _handle = -1;
    }

    public void Dispose() => Close();

    internal interface IKvaserApi
    {
        void canInitializeLibrary();
        int canOpenChannel(int channel, int flags);
        int canSetBusParams(int handle, int freq, uint tseg1, uint tseg2, uint sjw, uint noSamp, uint syncmode);
        int canSetBusParamsFd(int handle, int freqBrs, uint tseg1Brs, uint tseg2Brs, uint sjwBrs);
        int canBusOn(int handle);
        int canBusOff(int handle);
        int canClose(int handle);
        int canWrite(int handle, int id, byte[] msg, uint dlc, uint flag);
        int canReadWait(int handle, out int id, byte[] msg, out uint dlc, out uint flag, out uint time, uint timeout);
        int canGetErrorText(int err, StringBuilder buf, uint bufsiz);
    }

    private sealed class KvaserApi : IKvaserApi
    {
        private static bool _initialized;
        public void canInitializeLibrary()
        {
            if (_initialized) return;
            Native.canInitializeLibrary();
            _initialized = true;
        }
        public int canOpenChannel(int channel, int flags) => Native.canOpenChannel(channel, flags);
        public int canSetBusParams(int handle, int freq, uint tseg1, uint tseg2, uint sjw, uint noSamp, uint syncmode) => Native.canSetBusParams(handle, freq, tseg1, tseg2, sjw, noSamp, syncmode);
        public int canSetBusParamsFd(int handle, int freqBrs, uint tseg1Brs, uint tseg2Brs, uint sjwBrs) => Native.canSetBusParamsFd(handle, freqBrs, tseg1Brs, tseg2Brs, sjwBrs);
        public int canBusOn(int handle) => Native.canBusOn(handle);
        public int canBusOff(int handle) => Native.canBusOff(handle);
        public int canClose(int handle) => Native.canClose(handle);
        public int canWrite(int handle, int id, byte[] msg, uint dlc, uint flag) => Native.canWrite(handle, id, msg, dlc, flag);
        public int canReadWait(int handle, out int id, byte[] msg, out uint dlc, out uint flag, out uint time, uint timeout) => Native.canReadWait(handle, out id, msg, out dlc, out flag, out time, timeout);
        public int canGetErrorText(int err, StringBuilder buf, uint bufsiz) => Native.canGetErrorText(err, buf, bufsiz);
    }

    private static class Native
    {
        private const string Dll = "canlib32.dll";

        [DllImport(Dll)] public static extern void canInitializeLibrary();
        [DllImport(Dll)] public static extern int canOpenChannel(int channel, int flags);
        [DllImport(Dll)] public static extern int canSetBusParams(int handle, int freq, uint tseg1, uint tseg2, uint sjw, uint noSamp, uint syncmode);
        [DllImport(Dll)] public static extern int canSetBusParamsFd(int handle, int freqBrs, uint tseg1Brs, uint tseg2Brs, uint sjwBrs);
        [DllImport(Dll)] public static extern int canBusOn(int handle);
        [DllImport(Dll)] public static extern int canBusOff(int handle);
        [DllImport(Dll)] public static extern int canClose(int handle);
        [DllImport(Dll)] public static extern int canWrite(int handle, int id, byte[] msg, uint dlc, uint flag);
        [DllImport(Dll)] public static extern int canReadWait(int handle, out int id, byte[] msg, out uint dlc, out uint flag, out uint time, uint timeout);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int canGetErrorText(int err, StringBuilder buf, uint bufsiz);
    }
}

// ---------------------------------------------------------------------- Vector XL Driver Library

/// <summary>
/// Vector XL Driver Library (vxlapi64.dll aus dem Vector-Treiberpaket bzw. XL Driver Library). Ressource: Anwendungskanal aus
/// „Vector Hardware Config“ als „Testumgebung:0“ (Anwendungsname:Kanal), „0“ (Anwendung „Testumgebung“) oder „virtuell:0“
/// (virtueller Vector-Kanal). Klassisches CAN und CAN FD (über xlCanTransmitEx/xlCanReceive mit XLcanTxEvent/XLcanRxEvent).
/// </summary>
public sealed class VectorXlBackend(string resource) : ICanBackend
{
    private const short XlSuccess = 0, XlErrQueueIsEmpty = 10;
    private const uint BusTypeCan = 1, InterfaceVersion = 3, InterfaceVersionFd = 4, ActivateResetClock = 8;
    private const byte TagReceiveMsg = 1;
    private const ushort FlagErrorFrame = 0x01, FlagRemote = 0x10, FlagTxCompleted = 0x40;
    private const uint ExtendedId = 0x80000000;
    private const int EventSize = 48;
    // CAN-FD (XL_CAN_EV_*): Ereignisstrukturen mit 32 Bit Kopf und 64 Byte Daten
    private const ushort XlCanEvTxMsg = 0x0440, XlCanEvRxOk = 0x0400, XlCanEvTxOk = 0x0404;
    private const int FdTxSize = 128, FdRxSize = 128;
    private const uint FdFlagExtended = 0x0001, FdFlagFd = 0x0001, FdFlagBrs = 0x0002, FdFlagRtr = 0x0010;  // XL_CAN_TXMSG_FLAG_* / XL_CAN_RXMSG_FLAG_*
    private bool _fd;
    private sealed class DriverUsers { public int Count; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IVectorApi, DriverUsers> DriverCounts = new();
    private readonly IVectorApi _api = VectorApi.Instance;
    internal VectorXlBackend(string resource, IVectorApi api) : this(resource) => _api = api;
    private static readonly object DriverGate = new();

    private readonly (string App, uint Channel, bool Virtual) _target = Parse(resource);
    private int _port = -1;
    private ulong _mask;
    private bool _driverOpen;

    public string Description => _target.Virtual ? $"Vector virtuell Kanal {_target.Channel}" : $"Vector {_target.App}:{_target.Channel}";

    public static (string App, uint Channel, bool Virtual) Parse(string resource)
    {
        var parts = resource.Trim().Split(':', 2, StringSplitOptions.TrimEntries);
        string app = parts.Length == 2 ? parts[0] : "Testumgebung";
        var channelText = parts.Length == 2 ? parts[1] : parts[0];
        if (!uint.TryParse(channelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
            throw new InstrumentException($"Vector-Kanal „{resource}“: z. B. Testumgebung:0, 0 oder virtuell:0.");
        var isVirtual = app.Equals("virtuell", StringComparison.OrdinalIgnoreCase) || app.Equals("virtual", StringComparison.OrdinalIgnoreCase);
        return (app, channel, isVirtual);
    }

    public void Open(CanBusSettings settings)
    {
        if (_port >= 0) return;
        _fd = settings.Fd;
        lock (DriverGate)
        {
            if (!_driverOpen)
            {
                if (DriverCounts.GetOrCreateValue(_api).Count == 0) Check(_api.xlOpenDriver(), "Treiber öffnen");
                DriverCounts.GetOrCreateValue(_api).Count++;
                _driverOpen = true;
            }
        }
        try
        {
            uint hwType, hwIndex, hwChannel;
            if (_target.Virtual) (hwType, hwIndex, hwChannel) = (1, 0, _target.Channel); // XL_HWTYPE_VIRTUAL
            else Check(_api.xlGetApplConfig(_target.App, _target.Channel, out hwType, out hwIndex, out hwChannel, BusTypeCan),
                $"Anwendungskanal {_target.App}:{_target.Channel} (in Vector Hardware Config zuordnen)");
            _mask = _api.xlGetChannelMask((int)hwType, (int)hwIndex, (int)hwChannel);
            if (_mask == 0) throw new CommunicationException($"Vector XL: Kanal {Description} nicht gefunden (Hardware angeschlossen?).");
            var permission = _mask;
            Check(_api.xlOpenPort(out var port, "Testumgebung", _mask, ref permission, 4096, _fd ? InterfaceVersionFd : InterfaceVersion, BusTypeCan), "Port öffnen");
            _port = port;
            if (permission == _mask)
            {
                if (_fd)
                {
                    var cfg = new Native.CanFdConf { ArbitrationBitRate = (uint)settings.Bitrate, DataBitRate = (uint)settings.DataBitrate, SjwAbr = 1, Tseg1Abr = 63, Tseg2Abr = 16, SjwDbr = 1, Tseg1Dbr = 15, Tseg2Dbr = 4 };
                    Check(_api.xlCanFdSetConfiguration(_port, _mask, ref cfg), "FD-Bitrate");
                }
                else Check(_api.xlCanSetChannelBitrate(_port, _mask, (uint)settings.Bitrate), "Bitrate");
            }
            Check(_api.xlActivateChannel(_port, _mask, BusTypeCan, ActivateResetClock), "Kanal aktivieren");
        }
        catch
        {
            Close();
            throw;
        }
    }

    private static byte DlcOf(int length) => (byte)Array.IndexOf(CanFrame.FdLengths, CanFrame.FdLengths.First(l => l >= length));

    public void Send(CanFrame frame)
    {
        if (frame.Fd && !_fd) throw new CommunicationException("Vector XL: FD-Rahmen, aber Kanal ohne Option „FD“.");
        if (_fd)
        {
            // XLcanTxEvent: tag(2) transId(2) channelIndex(1) pad(3) + XL_CAN_TX_MSG { canId(4) msgFlags(4) dlc(1) reserved(7) data[64] }
            var ev = new byte[FdTxSize];
            BitConverter.TryWriteBytes(ev.AsSpan(0), XlCanEvTxMsg);
            BitConverter.TryWriteBytes(ev.AsSpan(16), frame.Id | (frame.Extended ? ExtendedId : 0));
            uint msgFlags = (frame.Fd ? FdFlagFd : 0) | (frame.Fd && frame.BitRateSwitch ? FdFlagBrs : 0) | (frame.Remote ? FdFlagRtr : 0);
            BitConverter.TryWriteBytes(ev.AsSpan(20), msgFlags);
            ev[24] = DlcOf(frame.Data.Length);
            frame.Data.CopyTo(ev.AsSpan(32));
            uint sent = 0;
            Check(_api.xlCanTransmitEx(_port, _mask, 1, ref sent, ev), "Senden (FD)");
            return;
        }
        var classic = new byte[EventSize];
        classic[0] = 10; // XL_TRANSMIT_MSG
        BitConverter.TryWriteBytes(classic.AsSpan(16), frame.Id | (frame.Extended ? ExtendedId : 0));
        BitConverter.TryWriteBytes(classic.AsSpan(20), (ushort)(frame.Remote ? FlagRemote : 0));
        BitConverter.TryWriteBytes(classic.AsSpan(22), (ushort)frame.Data.Length);
        frame.Data.CopyTo(classic.AsSpan(32));
        uint count = 1;
        Check(_api.xlCanTransmit(_port, _mask, ref count, classic), "Senden");
    }

    public CanFrame? Read(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (_fd)
            {
                var rx = new byte[FdRxSize];
                var status = _api.xlCanReceive(_port, rx);
                if (status == XlSuccess)
                {
                    var tag = BitConverter.ToUInt16(rx, 0);
                    if (tag is XlCanEvRxOk)
                    {
                        // XLcanRxEvent: tag(2) channelIndex(1) … 32-Byte-Kopf, dann canId(4) msgFlags(4) crc(4) … dlc bei Offset 40, data ab 44
                        var id = BitConverter.ToUInt32(rx, 32);
                        var msgFlags = BitConverter.ToUInt32(rx, 36);
                        var dlc = rx[40];
                        var length = dlc < CanFrame.FdLengths.Length ? CanFrame.FdLengths[dlc] : 0;
                        return new CanFrame(id & 0x1FFFFFFF, rx.AsSpan(44, Math.Min(length, FdRxSize - 44)).ToArray(), (id & ExtendedId) != 0,
                            (msgFlags & FdFlagFd) != 0, (msgFlags & FdFlagBrs) != 0, (msgFlags & FdFlagRtr) != 0);
                    }
                    if (Environment.TickCount64 >= deadline) return null;
                    continue;
                }
                if (status != XlErrQueueIsEmpty) Check(status, "Lesen (FD)");
            }
            else
            {
                var ev = new byte[EventSize];
                uint count = 1;
                var status = _api.xlReceive(_port, ref count, ev);
                if (status == XlSuccess && count > 0 && ev[0] == TagReceiveMsg)
                {
                    var id = BitConverter.ToUInt32(ev, 16);
                    var flags = BitConverter.ToUInt16(ev, 20);
                    var dlc = Math.Min((int)BitConverter.ToUInt16(ev, 22), 8);
                    if ((flags & (FlagErrorFrame | FlagTxCompleted)) == 0)
                        return new CanFrame(id & 0x1FFFFFFF, ev.AsSpan(32, dlc).ToArray(), (id & ExtendedId) != 0, Remote: (flags & FlagRemote) != 0);
                    if (Environment.TickCount64 >= deadline) return null;
                    continue;
                }
                if (status != XlSuccess && status != XlErrQueueIsEmpty) Check(status, "Lesen");
            }
            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(1);
        }
    }

    public string Status() => _port >= 0 ? "Kanal aktiv" : "geschlossen";

    private void Check(short status, string what)
    {
        if (status == XlSuccess) return;
        var text = Marshal.PtrToStringAnsi(_api.xlGetErrorString(status)) ?? $"Status {status}";
        throw new CommunicationException($"Vector XL {what} ({Description}): {text}");
    }

    public void Close()
    {
        if (_port >= 0)
        {
            _api.xlDeactivateChannel(_port, _mask);
            _api.xlClosePort(_port);
            _port = -1;
        }
        lock (DriverGate)
        {
            if (!_driverOpen) return;
            _driverOpen = false;
            if (--DriverCounts.GetOrCreateValue(_api).Count == 0) _api.xlCloseDriver();
        }
    }

    public void Dispose() => Close();

    internal interface IVectorApi
    {
        short xlOpenDriver();
        short xlCloseDriver();
        short xlGetApplConfig(string appName, uint appChannel, out uint hwType, out uint hwIndex, out uint hwChannel, uint busType);
        ulong xlGetChannelMask(int hwType, int hwIndex, int hwChannel);
        short xlOpenPort(out int portHandle, string userName, ulong accessMask, ref ulong permissionMask, uint rxQueueSize, uint interfaceVersion, uint busType);
        short xlCanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate);
        short xlActivateChannel(int portHandle, ulong accessMask, uint busType, uint flags);
        short xlDeactivateChannel(int portHandle, ulong accessMask);
        short xlClosePort(int portHandle);
        short xlCanTransmit(int portHandle, ulong accessMask, ref uint messageCount, byte[] events);
        short xlReceive(int portHandle, ref uint eventCount, byte[] events);
        short xlCanTransmitEx(int portHandle, ulong accessMask, uint messageCount, ref uint countSent, byte[] events);
        short xlCanReceive(int portHandle, byte[] canRxEvent);
        short xlCanFdSetConfiguration(int portHandle, ulong accessMask, ref Native.CanFdConf config);
        IntPtr xlGetErrorString(short status);
    }

    private sealed class VectorApi : IVectorApi
    {
        internal static readonly VectorApi Instance = new();
        public short xlOpenDriver() => Native.xlOpenDriver();
        public short xlCloseDriver() => Native.xlCloseDriver();
        public short xlGetApplConfig(string appName, uint appChannel, out uint hwType, out uint hwIndex, out uint hwChannel, uint busType) => Native.xlGetApplConfig(appName, appChannel, out hwType, out hwIndex, out hwChannel, busType);
        public ulong xlGetChannelMask(int hwType, int hwIndex, int hwChannel) => Native.xlGetChannelMask(hwType, hwIndex, hwChannel);
        public short xlOpenPort(out int portHandle, string userName, ulong accessMask, ref ulong permissionMask, uint rxQueueSize, uint interfaceVersion, uint busType) => Native.xlOpenPort(out portHandle, userName, accessMask, ref permissionMask, rxQueueSize, interfaceVersion, busType);
        public short xlCanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate) => Native.xlCanSetChannelBitrate(portHandle, accessMask, bitrate);
        public short xlActivateChannel(int portHandle, ulong accessMask, uint busType, uint flags) => Native.xlActivateChannel(portHandle, accessMask, busType, flags);
        public short xlDeactivateChannel(int portHandle, ulong accessMask) => Native.xlDeactivateChannel(portHandle, accessMask);
        public short xlClosePort(int portHandle) => Native.xlClosePort(portHandle);
        public short xlCanTransmit(int portHandle, ulong accessMask, ref uint messageCount, byte[] events) => Native.xlCanTransmit(portHandle, accessMask, ref messageCount, events);
        public short xlReceive(int portHandle, ref uint eventCount, byte[] events) => Native.xlReceive(portHandle, ref eventCount, events);
        public short xlCanTransmitEx(int portHandle, ulong accessMask, uint messageCount, ref uint countSent, byte[] events) => Native.xlCanTransmitEx(portHandle, accessMask, messageCount, ref countSent, events);
        public short xlCanReceive(int portHandle, byte[] canRxEvent) => Native.xlCanReceive(portHandle, canRxEvent);
        public short xlCanFdSetConfiguration(int portHandle, ulong accessMask, ref Native.CanFdConf config) => Native.xlCanFdSetConfiguration(portHandle, accessMask, ref config);
        public IntPtr xlGetErrorString(short status) => Native.xlGetErrorString(status);
    }

    internal static class Native
    {
        private const string Dll = "vxlapi64.dll";

        [DllImport(Dll)] public static extern short xlOpenDriver();
        [DllImport(Dll)] public static extern short xlCloseDriver();
        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern short xlGetApplConfig(string appName, uint appChannel, out uint hwType, out uint hwIndex, out uint hwChannel, uint busType);
        [DllImport(Dll)] public static extern ulong xlGetChannelMask(int hwType, int hwIndex, int hwChannel);
        [DllImport(Dll, CharSet = CharSet.Ansi)]
        public static extern short xlOpenPort(out int portHandle, string userName, ulong accessMask, ref ulong permissionMask, uint rxQueueSize, uint interfaceVersion, uint busType);
        [DllImport(Dll)] public static extern short xlCanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate);
        [DllImport(Dll)] public static extern short xlActivateChannel(int portHandle, ulong accessMask, uint busType, uint flags);
        [DllImport(Dll)] public static extern short xlDeactivateChannel(int portHandle, ulong accessMask);
        [DllImport(Dll)] public static extern short xlClosePort(int portHandle);
        [DllImport(Dll)] public static extern short xlCanTransmit(int portHandle, ulong accessMask, ref uint messageCount, byte[] events);
        [DllImport(Dll)] public static extern short xlReceive(int portHandle, ref uint eventCount, byte[] events);
        [DllImport(Dll)] public static extern short xlCanTransmitEx(int portHandle, ulong accessMask, uint messageCount, ref uint countSent, byte[] events);
        [DllImport(Dll)] public static extern short xlCanReceive(int portHandle, byte[] canRxEvent);
        [DllImport(Dll)] public static extern short xlCanFdSetConfiguration(int portHandle, ulong accessMask, ref CanFdConf config);

        [StructLayout(LayoutKind.Sequential)]
        public struct CanFdConf
        {
            public uint ArbitrationBitRate;
            public uint SjwAbr;
            public uint Tseg1Abr;
            public uint Tseg2Abr;
            public uint DataBitRate;
            public uint SjwDbr;
            public uint Tseg1Dbr;
            public uint Tseg2Dbr;
            public byte Reserved;
            public byte Options;
            public byte Reserved1;
            public byte Reserved2;
        }
        [DllImport(Dll)] public static extern IntPtr xlGetErrorString(short status);
    }
}
