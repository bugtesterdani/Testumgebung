using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Testumgebung.Instruments;

/// <summary>Fehler eines Messgeräts (Befehl abgelehnt, Fehlerwarteschlange, unerwartete Antwort, Verbindung).</summary>
public sealed class InstrumentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gelesener Abschnitt; <see cref="End"/> = Nachrichtenende vom Bus gemeldet (EOI/END bei VISA).</summary>
public readonly record struct IoChunk(byte[] Data, bool End);

/// <summary>
/// Byte-Transport zu einem Messgerät. SCPI-Nachrichten, Terminierung und IEEE-488.2-Blöcke behandelt <see cref="ScpiSession"/>.
/// </summary>
public interface IInstrumentIo : IDisposable
{
    string Resource { get; }

    Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Liest den nächsten Abschnitt (mindestens ein Byte). Zeitlimit → <see cref="TimeoutException"/>.</summary>
    Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Gerät zurücksetzen (Device Clear): laufende Ein-/Ausgabe verwerfen.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// SCPI über rohen TCP-Socket (Keysight/Agilent: Port 5025, „SOCKET“-Ressource). Kein VISA nötig.
/// Ressource: <c>TCPIP::192.168.1.20::5025::SOCKET</c> oder <c>192.168.1.20:5025</c>.
/// </summary>
public sealed class TcpSocketIo : IInstrumentIo
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly byte[] _buffer = new byte[65536];

    public TcpSocketIo(string resource, string host, int port)
    {
        Resource = resource;
        _host = host;
        _port = port;
    }

    public string Resource { get; }

    private async Task<NetworkStream> StreamAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_stream is not null) return _stream;
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Keine Verbindung zu {_host}:{_port} innerhalb {timeout.TotalSeconds:0.#} s.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new InstrumentException($"Verbindung zu {_host}:{_port} fehlgeschlagen: {ex.Message}", ex);
        }
        _client = client;
        _stream = client.GetStream();
        return _stream;
    }

    public async Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = await StreamAsync(timeout, cancellationToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await stream.WriteAsync(data, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Drop();
            throw new TimeoutException("Zeitlimit beim Senden.");
        }
        catch (IOException ex)
        {
            Drop();
            throw new InstrumentException("Verbindung unterbrochen: " + ex.Message, ex);
        }
    }

    public async Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = await StreamAsync(timeout, cancellationToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        int n;
        try
        {
            n = await stream.ReadAsync(_buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Drop();
            throw new TimeoutException($"Zeitlimit {timeout.TotalSeconds:0.###} s beim Lesen von {Resource}.");
        }
        catch (OperationCanceledException)
        {
            Drop(); // Antwort kann noch unterwegs sein – Verbindung neu aufbauen
            throw;
        }
        catch (IOException ex)
        {
            Drop();
            throw new InstrumentException("Verbindung unterbrochen: " + ex.Message, ex);
        }
        if (n == 0)
        {
            Drop();
            throw new InstrumentException($"{Resource}: Verbindung vom Gerät geschlossen.");
        }
        return new IoChunk(_buffer[..n], false);
    }

    /// <summary>Socket-Verbindungen kennen kein Device Clear: Verbindung neu aufbauen verwirft alte Antworten.</summary>
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Drop();
        return Task.CompletedTask;
    }

    private void Drop()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose() => Drop();
}

/// <summary>
/// VISA (Keysight IO Libraries Suite oder NI-VISA) für USB-TMC, GPIB, LAN (VXI-11/HiSLIP):
/// <c>USB0::0x2A8D::0x1301::MY1234::INSTR</c>, <c>GPIB0::22::INSTR</c>, <c>TCPIP0::192.168.1.20::hislip0::INSTR</c>.
/// Die VISA-Bibliothek wird erst bei der ersten Verwendung geladen (visa64.dll bzw. visa32.dll).
/// </summary>
public sealed class VisaIo : IInstrumentIo
{
    private const uint AttrTimeout = 0x3FFF001A;
    private const uint AttrTermCharEnabled = 0x3FFF0038;
    private const uint AttrSendEndEnabled = 0x3FFF0016;
    private const int SuccessTermChar = 0x3FFF0005;
    private const int SuccessMaxCount = 0x3FFF0006;
    private const int ErrorTimeout = unchecked((int)0xBFFF0015);
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(250);

    private readonly IVisaApi _api;
    private readonly object _gate = new();
    private uint _rm;
    private uint _vi;
    private bool _open;

    public VisaIo(string resource) : this(resource, new NativeVisaApi()) { }

    internal VisaIo(string resource, IVisaApi api) { Resource = resource; _api = api; }

    public string Resource { get; }

    private uint Session()
    {
        lock (_gate)
        {
            if (_open) return _vi;
            _api.Check(0, _api.OpenDefaultRM(out _rm), "viOpenDefaultRM");
            var status = _api.Open(_rm, Resource, 0, 2000, out _vi);
            if (status < 0)
            {
                var text = _api.Describe(_rm, status);
                _api.Close(_rm);
                throw new InstrumentException($"VISA: {Resource} nicht erreichbar ({text}).");
            }
            try
            {
                _api.SetAttribute(_vi, AttrTermCharEnabled, 0);   // Ende über EOI/END, Binärblöcke dürfen \n enthalten
                _api.SetAttribute(_vi, AttrSendEndEnabled, 1);
            }
            catch
            {
                _api.Close(_vi);
                _api.Close(_rm);
                _vi = _rm = 0;
                throw;
            }
            _open = true;
            return _vi;
        }
    }

    public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vi = Session();
        _api.SetAttribute(vi, AttrTimeout, (ulong)Math.Max(1, timeout.TotalMilliseconds));
        var status = _api.Write(vi, data, (uint)data.Length, out var written);
        if (status == ErrorTimeout) throw new TimeoutException($"VISA: Zeitlimit beim Senden an {Resource}.");
        _api.Check(vi, status, "viWrite");
        if (written != data.Length) throw new InstrumentException($"VISA: nur {written} von {data.Length} Bytes gesendet.");
    }, CancellationToken.None);

    /// <summary>Liest in Zeitscheiben, damit ein Abbruch spätestens nach <see cref="Slice"/> greift.</summary>
    public Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vi = Session();
        var buffer = new byte[65536];
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException($"VISA: Zeitlimit {timeout.TotalSeconds:0.###} s beim Lesen von {Resource}.");
            _api.SetAttribute(vi, AttrTimeout, (ulong)Math.Max(1, Math.Min(remaining.TotalMilliseconds, Slice.TotalMilliseconds)));
            var status = _api.Read(vi, buffer, (uint)buffer.Length, out var count);
            if (status == ErrorTimeout && count == 0) continue;
            if (status < 0 && status != ErrorTimeout) _api.Check(vi, status, "viRead");
            if (count == 0) continue;
            var end = status is 0 or SuccessTermChar;
            return new IoChunk(buffer[..(int)count], end && status != SuccessMaxCount);
        }
    }, CancellationToken.None);

    public Task ClearAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!_open) return;
        _api.Check(_vi, _api.Clear(_vi), "viClear");
    }, CancellationToken.None);

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_open) return;
            _api.Close(_vi);
            _api.Close(_rm);
            _open = false;
        }
    }

    /// <summary>Gefundene VISA-Ressourcen (z. B. „?*INSTR“). Leere Liste ohne VISA-Installation.</summary>
    public static IReadOnlyList<string> Find(string expression = "?*INSTR") => Visa.Find(expression);

    public static bool IsAvailable => Visa.IsAvailable;
}

internal interface IVisaApi
{
    int OpenDefaultRM(out uint rm);
    int Open(uint rm, string resource, uint mode, uint timeout, out uint vi);
    int Close(uint vi);
    int Read(uint vi, byte[] buffer, uint count, out uint read);
    int Write(uint vi, byte[] buffer, uint count, out uint written);
    int Clear(uint vi);
    void SetAttribute(uint vi, uint attribute, ulong value);
    string Describe(uint vi, int status);
    void Check(uint vi, int status, string call);
}

internal sealed class NativeVisaApi : IVisaApi
{
    public int OpenDefaultRM(out uint rm) => Visa.OpenDefaultRM(out rm);
    public int Open(uint rm, string resource, uint mode, uint timeout, out uint vi) => Visa.Open(rm, resource, mode, timeout, out vi);
    public int Close(uint vi) => Visa.Close(vi);
    public int Read(uint vi, byte[] buffer, uint count, out uint read) => Visa.Read(vi, buffer, count, out read);
    public int Write(uint vi, byte[] buffer, uint count, out uint written) => Visa.Write(vi, buffer, count, out written);
    public int Clear(uint vi) => Visa.Clear(vi);
    public void SetAttribute(uint vi, uint attribute, ulong value) => Visa.SetAttribute(vi, attribute, value);
    public string Describe(uint vi, int status) => Visa.Describe(vi, status);
    public void Check(uint vi, int status, string call) => Visa.Check(vi, status, call);
}

internal interface IVisaDiscoveryApi
{
    bool IsAvailable { get; }
    int OpenDefaultRM(out uint rm);
    int FindResources(uint rm, string expression, out uint list, out uint count, byte[] description);
    int FindNext(uint list, byte[] description);
    int Close(uint handle);
}

internal sealed class NativeVisaDiscoveryApi : IVisaDiscoveryApi
{
    public bool IsAvailable => Visa.IsAvailable;
    public int OpenDefaultRM(out uint rm) => Visa.OpenDefaultRM(out rm);
    public int FindResources(uint rm, string expression, out uint list, out uint count, byte[] description) => Visa.FindResources(rm, expression, out list, out count, description);
    public int FindNext(uint list, byte[] description) => Visa.FindNext(list, description);
    public int Close(uint handle) => Visa.Close(handle);
}

/// <summary>P/Invoke auf die VISA-C-Bibliothek (VPP-4.3). ViSession = ViUInt32, ViAttrState = ViUInt64 (64-Bit).</summary>
internal static class Visa
{
    private static readonly Lazy<IntPtr> Library = new(() =>
    {
        string[] names = Environment.Is64BitProcess ? ["visa64", "visa64.dll", "nivisa64", "ktvisa64"] : ["visa32", "visa32.dll"];
        foreach (var n in names)
            if (NativeLibrary.TryLoad(n, typeof(Visa).Assembly, null, out var h)) return h;
        return IntPtr.Zero;
    });

    public static bool IsAvailable => Library.Value != IntPtr.Zero;

    private static T Fn<T>(string name) where T : Delegate
    {
        if (Library.Value == IntPtr.Zero)
            throw new InstrumentException("Keine VISA-Bibliothek gefunden (Keysight IO Libraries Suite oder NI-VISA installieren) – LAN-Geräte alternativ über „TCPIP::<host>::5025::SOCKET“.");
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library.Value, name));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenDefaultRMFn(out uint rm);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenFn(uint rm, [MarshalAs(UnmanagedType.LPStr)] string rsrc, uint mode, uint timeout, out uint vi);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CloseFn(uint vi);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadFn(uint vi, byte[] buf, uint count, out uint ret);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteFn(uint vi, byte[] buf, uint count, out uint ret);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetAttribute64Fn(uint vi, uint attr, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetAttribute32Fn(uint vi, uint attr, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ClearFn(uint vi);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindRsrcFn(uint rm, [MarshalAs(UnmanagedType.LPStr)] string expr, out uint list, out uint count, byte[] desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindNextFn(uint list, byte[] desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StatusDescFn(uint vi, int status, byte[] desc);

    public static int OpenDefaultRM(out uint rm) => Fn<OpenDefaultRMFn>("viOpenDefaultRM")(out rm);
    public static int Open(uint rm, string rsrc, uint mode, uint timeout, out uint vi) => Fn<OpenFn>("viOpen")(rm, rsrc, mode, timeout, out vi);
    public static int Close(uint vi) => Fn<CloseFn>("viClose")(vi);
    public static int Read(uint vi, byte[] buf, uint count, out uint ret) => Fn<ReadFn>("viRead")(vi, buf, count, out ret);
    public static int Write(uint vi, byte[] buf, uint count, out uint ret) => Fn<WriteFn>("viWrite")(vi, buf, count, out ret);
    public static int Clear(uint vi) => Fn<ClearFn>("viClear")(vi);

    public static void SetAttribute(uint vi, uint attr, ulong value)
    {
        var status = Environment.Is64BitProcess
            ? Fn<SetAttribute64Fn>("viSetAttribute")(vi, attr, value)
            : Fn<SetAttribute32Fn>("viSetAttribute")(vi, attr, (uint)value);
        Check(vi, status, "viSetAttribute");
    }

    public static string Describe(uint vi, int status)
    {
        var desc = new byte[512];
        try
        {
            Fn<StatusDescFn>("viStatusDesc")(vi, status, desc);
            return Encoding.ASCII.GetString(desc).TrimEnd('\0') + $" (0x{status:X8})";
        }
        catch (EntryPointNotFoundException)
        {
            return $"Status 0x{status:X8}";
        }
    }

    public static void Check(uint vi, int status, string call)
    {
        if (status < 0) throw new InstrumentException($"VISA {call}: {Describe(vi, status)}");
    }

    internal static int FindResources(uint rm, string expression, out uint list, out uint count, byte[] description)
        => Fn<FindRsrcFn>("viFindRsrc")(rm, expression, out list, out count, description);

    internal static int FindNext(uint list, byte[] description) => Fn<FindNextFn>("viFindNext")(list, description);

    public static IReadOnlyList<string> Find(string expression) => Find(expression, new NativeVisaDiscoveryApi());

    internal static IReadOnlyList<string> Find(string expression, IVisaDiscoveryApi api)
    {
        if (!api.IsAvailable) return [];
        var result = new List<string>();
        if (api.OpenDefaultRM(out var rm) < 0) return result;
        try
        {
            var desc = new byte[256];
            if (api.FindResources(rm, expression, out var list, out var count, desc) < 0) return result;
            try
            {
                result.Add(Encoding.ASCII.GetString(desc).TrimEnd('\0'));
                for (var i = 1; i < count; i++)
                {
                    Array.Clear(desc);
                    if (api.FindNext(list, desc) < 0) break;
                    result.Add(Encoding.ASCII.GetString(desc).TrimEnd('\0'));
                }
            }
            finally { api.Close(list); }
        }
        finally
        {
            api.Close(rm);
        }
        return result;
    }
}

/// <summary>Ressourcenstring → Transport. „…::SOCKET“ und „host:port“ ohne VISA, alles andere über VISA.</summary>
public static class InstrumentIo
{
    public static IInstrumentIo Open(string resource)
    {
        var r = resource.Trim();
        var parts = r.Split("::", StringSplitOptions.TrimEntries);
        if (parts.Length == 4 && parts[0].StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase)
                              && parts[3].Equals("SOCKET", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out var port))
            return new TcpSocketIo(r, parts[1], port);
        if (!r.Contains("::", StringComparison.Ordinal) && r.LastIndexOf(':') is var colon and > 0
            && int.TryParse(r[(colon + 1)..], out var p2))
            return new TcpSocketIo(r, r[..colon], p2);
        return new VisaIo(r);
    }
}
