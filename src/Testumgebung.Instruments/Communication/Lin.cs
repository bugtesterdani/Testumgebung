using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>Treiberanbindung eines LIN-Interfaces im Master-Betrieb.</summary>
public interface ILinBackend : IDisposable
{
    string Description { get; }

    void Open(int baudrate);

    /// <summary>Header senden und Slave-Antwort lesen; null, wenn kein Slave antwortet.</summary>
    byte[]? Request(byte id, int length, LinChecksum checksum, int timeoutMs);

    void Publish(byte id, byte[] data, LinChecksum checksum);

    string Status();

    void Close();
}

/// <summary>
/// LIN-Master (PEAK PLIN-USB/PLIN-PCIe oder virtueller Bus). Optionen: Baud=19200, Sicherer Zustand (Frames „0x10 00 00“, getrennt
/// durch „|“), beim virtuellen Bus Antworten=„0x21=01 02 03 04;0x22=@0x10“ (Slave-Antworten bzw. Echo des zuletzt gesendeten Frames).
/// </summary>
public sealed class LinDevice : CommDevice, ILinAdapter
{
    public const string PlinAdapter = "LIN – PEAK PLIN-API";
    public const string VirtualAdapter = "LIN – virtueller Bus (ohne Hardware)";

    private readonly ILinBackend _backend;
    private readonly int _baud;
    private bool _open;

    public LinDevice(string adapter, string resource, InstrumentOptions options, ILinBackend? backend = null)
        : base(adapter, adapter == PlinAdapter ? "PLIN-API" : "virtuell", resource, options)
    {
        _baud = (int)(options.Number("Baud") ?? 19200);
        if (_baud is < 1000 or > 20000) throw new InstrumentException($"LIN-Baudrate {_baud}: 1000–20000.");
        _backend = backend ?? adapter switch
        {
            PlinAdapter => new PlinBackend(resource),
            VirtualAdapter => new VirtualLinBackend(options.Text("Antworten") ?? ""),
            _ => throw new InstrumentException($"Unbekannter LIN-Adapter „{adapter}“."),
        };
    }

    public override string Name => $"{Adapter} {_backend.Description}";

    public override IReadOnlyList<InstrumentChannel> Channels { get; } =
        [new("Bus", "", 0, 0, ChannelAccess.ReadWrite, "LIN-Bus als Master (Bausteine LIN lesen/senden)")];

    private void EnsureOpen()
    {
        if (_open) return;
        try
        {
            _backend.Open(_baud);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new CommunicationException($"{Adapter}: Treiber nicht installiert ({ex.Message}).", ex);
        }
        _open = true;
        Trace("=", $"geöffnet {_backend.Description}, {_baud} Bd");
    }

    public Task<byte[]> RequestAsync(string channel, byte frameId, int length, LinChecksum checksum, TimeSpan timeout, CancellationToken cancellationToken)
        => LockedAsync(() =>
        {
            if (Lin.Validate(frameId, length) is { } error) throw new CommunicationException(error);
            EnsureOpen();
            Trace("→", $"Header 0x{frameId:X2} (PID 0x{Lin.ProtectedId(frameId):X2})");
            var data = _backend.Request(frameId, length, Lin.Effective(frameId, checksum), (int)Math.Max(1, timeout.TotalMilliseconds));
            if (data is null)
            {
                Trace("←", "keine Antwort");
                throw new CommunicationTimeoutException($"LIN 0x{frameId:X2}: kein Slave antwortet ({_backend.Status()}).", []);
            }
            Trace("←", Convert.ToHexString(data));
            return Task.FromResult(data);
        }, cancellationToken);

    public Task SendAsync(string channel, byte frameId, byte[] data, LinChecksum checksum, CancellationToken cancellationToken)
        => LockedAsync(() =>
        {
            if (Lin.Validate(frameId, data.Length) is { } error) throw new CommunicationException(error);
            EnsureOpen();
            Trace("→", $"0x{frameId:X2} {Convert.ToHexString(data)}");
            _backend.Publish(frameId, data, Lin.Effective(frameId, checksum));
            return Task.FromResult(0);
        }, cancellationToken);

    public override async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        if (Options.Text("Sicherer Zustand") is not { } frames) return;
        foreach (var text in frames.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
            var idText = parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[0][2..] : parts[0];
            var id = byte.Parse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            await SendAsync("", id, parts.Length > 1 ? CommText.ParseHex(parts[1]) : [0], LinChecksum.Enhanced, cancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();
        try
        {
            EnsureOpen();
            details.Add($"Baudrate {_baud} Bd");
            details.Add("Status: " + _backend.Status());
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
        _backend.Dispose();
        base.Dispose();
    }
}

/// <summary>Virtueller LIN-Bus mit fest eingestellten Slave-Antworten (Option „Antworten“).</summary>
public sealed class VirtualLinBackend : ILinBackend
{
    private readonly Dictionary<byte, byte[]> _responses = [];
    private readonly Dictionary<byte, byte> _echo = [];
    private readonly Dictionary<byte, byte[]> _published = [];

    public VirtualLinBackend(string responses)
    {
        foreach (var entry in responses.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq < 0) throw new InstrumentException($"LIN-Antwort „{entry}“: ID=DATEN oder ID=@ID.");
            var id = Id(entry[..eq]);
            var value = entry[(eq + 1)..].Trim();
            if (value.StartsWith('@')) _echo[id] = Id(value[1..]);
            else _responses[id] = CommText.ParseHex(value);
        }
    }

    private static byte Id(string text)
    {
        var t = text.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.Parse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : byte.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public string Description => "virtueller LIN-Bus";

    public void Open(int baudrate)
    {
    }

    public byte[]? Request(byte id, int length, LinChecksum checksum, int timeoutMs)
    {
        lock (_published)
        {
            var data = _responses.TryGetValue(id, out var r) ? r : _echo.TryGetValue(id, out var s) && _published.TryGetValue(s, out var p) ? p : null;
            return data is null ? null : data.Length == length ? data.ToArray()
                : throw new CommunicationException($"LIN 0x{id:X2}: Slave antwortet mit {data.Length} statt {length} Bytes.");
        }
    }

    public void Publish(byte id, byte[] data, LinChecksum checksum)
    {
        lock (_published) _published[id] = data.ToArray();
    }

    public string Status() => $"{_responses.Count + _echo.Count} Slave-Antwort(en)";

    public void Close()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// PEAK-System PLIN-API (PLinApi.dll, im PLIN-Treiberpaket). Ressource: Hardware-Handle „1“, „2“ … (Reihenfolge laut
/// LIN_GetAvailableHardware). Master-Betrieb; Frames werden vor jedem Zugriff als Publisher bzw. Subscriber eingetragen.
/// </summary>
public sealed class PlinBackend(string resource) : ILinBackend
{
    private readonly IPlinApi _api = new PlinApi();

    internal PlinBackend(string resource, IPlinApi api) : this(resource) => _api = api;

    private const int ErrOk = 0, ErrRcvQueueEmpty = 3;
    private const byte ModeMaster = 2;
    private const byte DirPublisher = 1, DirSubscriber = 2;
    private const byte CstClassic = 1, CstEnhanced = 2;
    private const ushort FlagResponseEnable = 0x1;

    private readonly int _index = int.TryParse(resource.Trim().Replace("PLIN", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 1
        ? i : throw new InstrumentException($"PLIN-Kanal „{resource}“: Hardware 1, 2, …");
    private byte _client;
    private ushort _hw;
    private bool _open;

    public string Description => $"PLIN {_index}{(_open ? $" (Handle {_hw})" : "")}";

    public void Open(int baudrate)
    {
        if (_open) return;
        Check(_api.LIN_RegisterClient("Testumgebung", 0, out _client), "Client anmelden");
        try
        {
            var handles = new ushort[16];
            Check(_api.LIN_GetAvailableHardware(handles, (ushort)(handles.Length * 2), out var count), "Hardware suchen");
            if (_index > count) throw new CommunicationException($"PLIN: Hardware {_index} nicht gefunden ({count} angeschlossen).");
            _hw = handles[_index - 1];
            Check(_api.LIN_ConnectClient(_client, _hw), "Verbinden");
            Check(_api.LIN_InitializeHardware(_client, _hw, ModeMaster, (ushort)baudrate), "Initialisieren (Master)");
            Check(_api.LIN_SetClientFilter(_client, _hw, ulong.MaxValue), "Filter");
            _open = true;
        }
        catch
        {
            _api.LIN_RemoveClient(_client);
            throw;
        }
    }

    private void Entry(byte id, int length, byte direction, LinChecksum checksum, byte[]? data)
    {
        var entry = new Native.FrameEntry
        {
            FrameId = id,
            Length = (byte)length,
            Direction = direction,
            ChecksumType = checksum == LinChecksum.Classic ? CstClassic : CstEnhanced,
            Flags = FlagResponseEnable,
            InitialData = new byte[8],
        };
        data?.CopyTo(entry.InitialData, 0);
        Check(_api.LIN_SetFrameEntry(_client, _hw, ref entry), "Frame eintragen");
    }

    public byte[]? Request(byte id, int length, LinChecksum checksum, int timeoutMs)
    {
        Entry(id, length, DirSubscriber, checksum, null);
        var pid = id;
        Check(_api.LIN_GetPID(ref pid), "PID");
        var msg = new Native.Msg { FrameId = pid, Length = (byte)length, Direction = DirSubscriber, ChecksumType = checksum == LinChecksum.Classic ? CstClassic : CstEnhanced, Data = new byte[8] };
        while (_api.LIN_Read(_client, out _) == ErrOk) { } // alte Meldungen verwerfen
        Check(_api.LIN_Write(_client, _hw, ref msg), "Header senden");
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var status = _api.LIN_Read(_client, out var rcv);
            if (status == ErrRcvQueueEmpty) { Thread.Sleep(1); continue; }
            Check(status, "Lesen");
            if (rcv.Type != 0 || (rcv.FrameId & 0x3F) != id) continue;
            if (rcv.ErrorFlags != 0) return null; // u. a. Slave antwortet nicht, Prüfsummenfehler
            return rcv.Data.AsSpan(0, Math.Min((int)rcv.Length, 8)).ToArray();
        }
        return null;
    }

    public void Publish(byte id, byte[] data, LinChecksum checksum)
    {
        Entry(id, data.Length, DirPublisher, checksum, data);
        var pid = id;
        Check(_api.LIN_GetPID(ref pid), "PID");
        var msg = new Native.Msg { FrameId = pid, Length = (byte)data.Length, Direction = DirPublisher, ChecksumType = checksum == LinChecksum.Classic ? CstClassic : CstEnhanced, Data = new byte[8] };
        data.CopyTo(msg.Data, 0);
        Check(_api.LIN_CalculateChecksum(ref msg), "Prüfsumme");
        Check(_api.LIN_Write(_client, _hw, ref msg), "Senden");
    }

    public string Status() => _open ? "Master aktiv" : "geschlossen";

    private void Check(int status, string what)
    {
        if (status == ErrOk) return;
        var sb = new StringBuilder(256);
        var text = _api.LIN_GetErrorText(status, 0x07, sb, sb.Capacity) == ErrOk ? sb.ToString() : $"Fehler {status}";
        throw new CommunicationException($"PLIN {what}: {text}");
    }

    public void Close()
    {
        if (!_open) return;
        _api.LIN_DisconnectClient(_client, _hw);
        _api.LIN_RemoveClient(_client);
        _open = false;
    }

    public void Dispose() => Close();

    internal interface IPlinApi
    {
        int LIN_RegisterClient(string name, uint hWnd, out byte client);
        int LIN_RemoveClient(byte client);
        int LIN_ConnectClient(byte client, ushort hw);
        int LIN_DisconnectClient(byte client, ushort hw);
        int LIN_InitializeHardware(byte client, ushort hw, byte mode, ushort baudrate);
        int LIN_GetAvailableHardware(ushort[] buffer, ushort bufferSize, out int count);
        int LIN_SetClientFilter(byte client, ushort hw, ulong mask);
        int LIN_SetFrameEntry(byte client, ushort hw, ref Native.FrameEntry entry);
        int LIN_Write(byte client, ushort hw, ref Native.Msg message);
        int LIN_Read(byte client, out Native.RcvMsg message);
        int LIN_GetPID(ref byte frameId);
        int LIN_CalculateChecksum(ref Native.Msg message);
        int LIN_GetErrorText(int error, byte language, StringBuilder buffer, int bufferSize);
    }

    private sealed class PlinApi : IPlinApi
    {
        public int LIN_RegisterClient(string name, uint hWnd, out byte client) => Native.LIN_RegisterClient(name, hWnd, out client);
        public int LIN_RemoveClient(byte client) => Native.LIN_RemoveClient(client);
        public int LIN_ConnectClient(byte client, ushort hw) => Native.LIN_ConnectClient(client, hw);
        public int LIN_DisconnectClient(byte client, ushort hw) => Native.LIN_DisconnectClient(client, hw);
        public int LIN_InitializeHardware(byte client, ushort hw, byte mode, ushort baudrate) => Native.LIN_InitializeHardware(client, hw, mode, baudrate);
        public int LIN_GetAvailableHardware(ushort[] buffer, ushort bufferSize, out int count) => Native.LIN_GetAvailableHardware(buffer, bufferSize, out count);
        public int LIN_SetClientFilter(byte client, ushort hw, ulong mask) => Native.LIN_SetClientFilter(client, hw, mask);
        public int LIN_SetFrameEntry(byte client, ushort hw, ref Native.FrameEntry entry) => Native.LIN_SetFrameEntry(client, hw, ref entry);
        public int LIN_Write(byte client, ushort hw, ref Native.Msg message) => Native.LIN_Write(client, hw, ref message);
        public int LIN_Read(byte client, out Native.RcvMsg message) => Native.LIN_Read(client, out message);
        public int LIN_GetPID(ref byte frameId) => Native.LIN_GetPID(ref frameId);
        public int LIN_CalculateChecksum(ref Native.Msg message) => Native.LIN_CalculateChecksum(ref message);
        public int LIN_GetErrorText(int error, byte language, StringBuilder buffer, int bufferSize) => Native.LIN_GetErrorText(error, language, buffer, bufferSize);
    }

    internal static class Native
    {
        private const string Dll = "PLinApi.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public byte FrameId;
            public byte Length;
            public byte Direction;
            public byte ChecksumType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data;
            public byte Checksum;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RcvMsg
        {
            public byte Type;
            public byte FrameId;
            public byte Length;
            public byte Direction;
            public byte ChecksumType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data;
            public byte Checksum;
            public int ErrorFlags;
            public ulong TimeStamp;
            public ushort Hw;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameEntry
        {
            public byte FrameId;
            public byte Length;
            public byte Direction;
            public byte ChecksumType;
            public ushort Flags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] InitialData;
        }

        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int LIN_RegisterClient(string name, uint hWnd, out byte client);
        [DllImport(Dll)] public static extern int LIN_RemoveClient(byte client);
        [DllImport(Dll)] public static extern int LIN_ConnectClient(byte client, ushort hw);
        [DllImport(Dll)] public static extern int LIN_DisconnectClient(byte client, ushort hw);
        [DllImport(Dll)] public static extern int LIN_InitializeHardware(byte client, ushort hw, byte mode, ushort baudrate);
        [DllImport(Dll)] public static extern int LIN_GetAvailableHardware([Out] ushort[] buffer, ushort bufferSize, out int count);
        [DllImport(Dll)] public static extern int LIN_SetClientFilter(byte client, ushort hw, ulong mask);
        [DllImport(Dll)] public static extern int LIN_SetFrameEntry(byte client, ushort hw, ref FrameEntry entry);
        [DllImport(Dll)] public static extern int LIN_Write(byte client, ushort hw, ref Msg message);
        [DllImport(Dll)] public static extern int LIN_Read(byte client, out RcvMsg message);
        [DllImport(Dll)] public static extern int LIN_GetPID(ref byte frameId);
        [DllImport(Dll)] public static extern int LIN_CalculateChecksum(ref Msg message);
        [DllImport(Dll, CharSet = CharSet.Ansi)] public static extern int LIN_GetErrorText(int error, byte language, StringBuilder buffer, int bufferSize);
    }
}
