using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>Byte-Transport zum Prüfling (Seriell, TCP, UDP, Testschleife). Ein Aufrufer zur Zeit.</summary>
public interface IStreamTransport : IDisposable
{
    /// <summary>Beschreibung der Verbindung für Protokoll und Fehlermeldungen.</summary>
    string Description { get; }

    /// <summary>Ob der Transport derzeit geöffnet ist.</summary>
    bool IsOpen { get; }

    /// <summary>Öffnet die konfigurierte Verbindung.</summary>
    /// <param name="cancellationToken">Abbruchsignal für den Verbindungsaufbau.</param>
    Task OpenAsync(CancellationToken cancellationToken);

    /// <summary>Sendet die übergebenen Bytes ohne automatische Text- oder Protokollumwandlung.</summary>
    /// <param name="data">Zu sendende Bytes.</param>
    /// <param name="cancellationToken">Abbruchsignal für den Schreibvorgang.</param>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Liest verfügbare Bytes; wartet höchstens <paramref name="timeout"/>. 0 = nichts empfangen.</summary>
    /// <param name="buffer">Zielpuffer; nur die zurückgegebene Anzahl Bytes ist gültig.</param>
    /// <param name="timeout">Maximale Wartezeit dieses Leseaufrufs.</param>
    /// <param name="cancellationToken">Abbruchsignal für das Lesen.</param>
    /// <returns>Anzahl der in den Puffer geschriebenen Bytes.</returns>
    Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Verwirft gepufferte Eingangsdaten des Transports.</summary>
    void DiscardInput();

    /// <summary>Schließt die Verbindung und gibt ihre Verbindungsressourcen frei.</summary>
    void Close();
}

/// <summary>Empfang nach <see cref="ReceiveRule"/> über einen Transport, mit Restpuffer zwischen den Aufrufen.</summary>
public sealed class StreamReceiver(IStreamTransport transport)
{
    private readonly List<byte> _pending = [];

    public void Discard()
    {
        _pending.Clear();
        transport.DiscardInput();
    }

    public async Task<byte[]> ReceiveAsync(ReceiveRule rule, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + rule.Timeout;
        var buffer = new byte[4096];
        var lastData = DateTime.UtcNow;
        while (true)
        {
            if (Complete(rule) is { } done) return done;
            if (rule.Terminator is null && rule.Length is null && _pending.Count > 0 && DateTime.UtcNow - lastData >= rule.EffectiveIdle)
            {
                var all = _pending.ToArray();
                _pending.Clear();
                return all;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                var partial = _pending.ToArray();
                _pending.Clear();
                throw new CommunicationTimeoutException($"Keine vollständige Antwort innerhalb von {rule.Timeout.TotalMilliseconds:0} ms ({transport.Description}).", partial);
            }
            var slice = rule.Terminator is null && rule.Length is null && _pending.Count > 0
                ? TimeSpan.FromTicks(Math.Min(remaining.Ticks, rule.EffectiveIdle.Ticks))
                : TimeSpan.FromTicks(Math.Min(remaining.Ticks, TimeSpan.FromMilliseconds(250).Ticks));
            var n = await transport.ReadAsync(buffer, slice, cancellationToken).ConfigureAwait(false);
            if (n > 0)
            {
                _pending.AddRange(buffer.AsSpan(0, n).ToArray());
                lastData = DateTime.UtcNow;
                if (_pending.Count > rule.MaxBytes)
                {
                    var partial = _pending.ToArray();
                    _pending.Clear();
                    throw new CommunicationException($"Antwort länger als {rule.MaxBytes} Bytes ohne Abschluss ({transport.Description}).");
                }
            }
        }
    }

    private byte[]? Complete(ReceiveRule rule)
    {
        if (rule.Length is { } len)
        {
            if (_pending.Count < len) return null;
            var r = _pending.Take(len).ToArray();
            _pending.RemoveRange(0, len);
            return r;
        }
        if (rule.Terminator is { Length: > 0 } t)
        {
            var index = IndexOf(_pending, t);
            if (index < 0) return null;
            var r = _pending.Take(index).ToArray();
            _pending.RemoveRange(0, index + t.Length);
            return r;
        }
        return null;
    }

    private static int IndexOf(List<byte> data, byte[] pattern)
    {
        for (var i = 0; i + pattern.Length <= data.Count; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }
}

// ---------------------------------------------------------------------- Seriell (Win32)

/// <summary>Einstellungen einer seriellen Schnittstelle (Optionen der Gerätekonfiguration).</summary>
public sealed record SerialSettings(string Port, int Baud = 9600, int DataBits = 8, char Parity = 'N', double StopBits = 1, string Handshake = "Kein", bool Rs485Rts = false)
{
    /// <summary>Optionen: Baud, Datenbits, Parität (N/E/O/M/S), Stoppbits (1/1.5/2), Handshake (Kein/RTS-CTS/XON-XOFF), RS485 (RTS-Umschaltung).</summary>
    public static SerialSettings From(string port, InstrumentOptions o)
    {
        var parity = ParseParity(o);
        var settings = new SerialSettings(port.Trim().ToUpperInvariant(), (int)(o.Number("Baud") ?? 9600), (int)(o.Number("Datenbits") ?? 8), parity,
            o.Number("Stoppbits") ?? 1, o.Text("Handshake") ?? "Kein", o.Flag("RS485"));
        if (settings.DataBits is < 5 or > 8) throw new InstrumentException("Datenbits 5–8.");
        if (settings.StopBits is not (1 or 1.5 or 2)) throw new InstrumentException("Stoppbits 1, 1.5 oder 2.");
        return settings;
    }

    private static char ParseParity(InstrumentOptions o)
    {
        var parity = (o.Text("Parität") ?? o.Text("Paritaet") ?? "N").ToUpperInvariant()[0];
        parity = parity switch { 'K' => 'N', 'G' => 'E', 'U' => 'O', _ => parity };
        if (!"NEOMS".Contains(parity)) throw new InstrumentException($"Parität „{parity}“: N, E, O, M oder S.");
        return parity;
    }

    public override string ToString()
        => $"{Port} {Baud.ToString(CultureInfo.InvariantCulture)} {DataBits}{Parity}{StopBits.ToString(CultureInfo.InvariantCulture)}{(Rs485Rts ? " RS-485" : "")}";

    /// <summary>Zeit für ein Zeichen (Start + Daten + Parität + Stopp).</summary>
    public TimeSpan CharTime => TimeSpan.FromSeconds((1 + DataBits + (Parity == 'N' ? 0 : 1) + StopBits) / Baud);
}

/// <summary>
/// Serielle Schnittstelle über die Win32-API (COMx, auch USB-Seriell-Adapter). RS-485: RTS-Umschaltung (RTS_CONTROL_TOGGLE)
/// oder Adapter mit automatischer Richtungsumschaltung. Nur Windows.
/// </summary>
public sealed class SerialTransport(SerialSettings settings) : IStreamTransport
{
    private readonly ISerialApi _api = new Win32SerialApi();

    internal SerialTransport(SerialSettings settings, ISerialApi api) : this(settings) => _api = api;

    private SafeFileHandle? _handle;
    private readonly object _gate = new();

    public string Description => settings.ToString();
    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        if (IsOpen) return Task.CompletedTask;
        if (!OperatingSystem.IsWindows()) throw new CommunicationException("Serielle Schnittstellen werden nur unter Windows unterstützt.");
        var handle = _api.CreateFileW(@"\\.\" + settings.Port, Native.GenericRead | Native.GenericWrite, 0, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new CommunicationException($"{settings.Port} lässt sich nicht öffnen: {new Win32Exception(Marshal.GetLastWin32Error()).Message} (belegt oder nicht vorhanden?).");
        try
        {
            var dcb = new Native.Dcb { DCBlength = (uint)Marshal.SizeOf<Native.Dcb>() };
            if (!_api.GetCommState(handle, ref dcb)) throw Win32("GetCommState");
            dcb.BaudRate = (uint)settings.Baud;
            dcb.ByteSize = (byte)settings.DataBits;
            dcb.Parity = settings.Parity switch { 'O' => 1, 'E' => 2, 'M' => 3, 'S' => 4, _ => 0 };
            dcb.StopBits = settings.StopBits switch { 1.5 => 1, 2 => 2, _ => 0 };
            ConfigureHandshake(ref dcb);
            if (!_api.SetCommState(handle, ref dcb)) throw Win32($"SetCommState ({settings})");
            if (!_api.PurgeComm(handle, Native.PurgeRxClear | Native.PurgeTxClear)) throw Win32("PurgeComm");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        _handle = handle;
        return Task.CompletedTask;
    }

    private void ConfigureHandshake(ref Native.Dcb dcb)
    {
        uint flags = 1; // fBinary
        if (settings.Parity != 'N') flags |= 1 << 1;
        var handshake = settings.Handshake.ToUpperInvariant();
        uint rts = 1; // RTS_CONTROL_ENABLE
        if (handshake.Contains("RTS", StringComparison.Ordinal)) { flags |= 1 << 2; rts = 2; } // CTS-Fluss, RTS_CONTROL_HANDSHAKE
        if (settings.Rs485Rts) rts = 3;                                                          // RTS_CONTROL_TOGGLE
        if (handshake.Contains("XON", StringComparison.Ordinal)) { flags |= (1 << 8) | (1 << 9); dcb.XonChar = 0x11; dcb.XoffChar = 0x13; dcb.XonLim = 2048; dcb.XoffLim = 512; }
        flags |= 1u << 4;           // DTR_CONTROL_ENABLE
        flags |= rts << 12;
        dcb.Flags = flags;
    }

    private static CommunicationException Win32(string what) => new($"{what}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private SafeFileHandle Handle => _handle is { IsInvalid: false, IsClosed: false } h ? h : throw new CommunicationException($"{settings.Port} ist nicht geöffnet.");

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var h = Handle;
        lock (_gate)
        {
            var timeouts = new Native.CommTimeouts { WriteTotalTimeoutConstant = 2000, WriteTotalTimeoutMultiplier = 10 };
            _api.SetCommTimeouts(h, ref timeouts);
            var bytes = data.ToArray();
            using var reg = cancellationToken.Register(() => _api.CancelIoEx(h, IntPtr.Zero));
            if (!_api.WriteFile(h, bytes, bytes.Length, out var written, IntPtr.Zero) || written != bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Win32($"Schreiben auf {settings.Port}");
            }
        }
    }, cancellationToken);

    public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var h = Handle;
        lock (_gate)
        {
            // Rückkehr, sobald ein Byte da ist, spätestens nach dem Zeitlimit (MAXDWORD-Intervall + Multiplikator).
            var timeouts = new Native.CommTimeouts
            {
                ReadIntervalTimeout = uint.MaxValue,
                ReadTotalTimeoutMultiplier = uint.MaxValue,
                ReadTotalTimeoutConstant = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue - 1),
            };
            _api.SetCommTimeouts(h, ref timeouts);
            var temp = new byte[buffer.Length];
            using var reg = cancellationToken.Register(() => _api.CancelIoEx(h, IntPtr.Zero));
            if (!_api.ReadFile(h, temp, temp.Length, out var read, IntPtr.Zero))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw Win32($"Lesen von {settings.Port}");
            }
            temp.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }
    }, cancellationToken);

    public void DiscardInput()
    {
        if (IsOpen) _api.PurgeComm(_handle!, Native.PurgeRxClear);
    }

    public void Close()
    {
        _handle?.Dispose();
        _handle = null;
    }

    public void Dispose() => Close();

    /// <summary>Vorhandene COM-Ports laut Registrierung (HARDWARE\DEVICEMAP\SERIALCOMM).</summary>
    public static IReadOnlyList<string> Ports()
    {
        if (!OperatingSystem.IsWindows()) return [];
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null) return [];
        var ports = new List<string>();
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name) is string port) ports.Add(port);
        return ports.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToList();
    }

    internal static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint OpenExisting = 3;
        public const uint PurgeTxClear = 0x0004;
        public const uint PurgeRxClear = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        public struct Dcb
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CommTimeouts
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCommState(SafeFileHandle handle, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCommState(SafeFileHandle handle, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PurgeComm(SafeFileHandle handle, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    }
}

internal interface ISerialApi
{
    SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    bool GetCommState(SafeFileHandle handle, ref SerialTransport.Native.Dcb dcb);
    bool SetCommState(SafeFileHandle handle, ref SerialTransport.Native.Dcb dcb);
    bool SetCommTimeouts(SafeFileHandle handle, ref SerialTransport.Native.CommTimeouts timeouts);
    bool PurgeComm(SafeFileHandle handle, uint flags);
    bool ReadFile(SafeFileHandle handle, byte[] buffer, int count, out int read, IntPtr overlapped);
    bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, IntPtr overlapped);
    bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}

internal sealed class Win32SerialApi : ISerialApi
{
    public SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template)
        => SerialTransport.Native.CreateFileW(name, access, share, security, creation, flags, template);
    public bool GetCommState(SafeFileHandle handle, ref SerialTransport.Native.Dcb dcb) => SerialTransport.Native.GetCommState(handle, ref dcb);
    public bool SetCommState(SafeFileHandle handle, ref SerialTransport.Native.Dcb dcb) => SerialTransport.Native.SetCommState(handle, ref dcb);
    public bool SetCommTimeouts(SafeFileHandle handle, ref SerialTransport.Native.CommTimeouts timeouts) => SerialTransport.Native.SetCommTimeouts(handle, ref timeouts);
    public bool PurgeComm(SafeFileHandle handle, uint flags) => SerialTransport.Native.PurgeComm(handle, flags);
    public bool ReadFile(SafeFileHandle handle, byte[] buffer, int count, out int read, IntPtr overlapped) => SerialTransport.Native.ReadFile(handle, buffer, count, out read, overlapped);
    public bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, IntPtr overlapped) => SerialTransport.Native.WriteFile(handle, buffer, count, out written, overlapped);
    public bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped) => SerialTransport.Native.CancelIoEx(handle, overlapped);
}

// ---------------------------------------------------------------------- TCP / UDP

internal static class Endpoints
{
    /// <summary>„192.168.1.50:5000“, „pruefling.local:502“, „TCPIP::host::port::SOCKET“.</summary>
    public static (string Host, int Port) Parse(string resource, int defaultPort)
    {
        var r = resource.Trim();
        if (r.StartsWith("TCPIP", StringComparison.OrdinalIgnoreCase))
        {
            var parts = r.Split("::", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return (parts[1], p);
            if (parts.Length >= 2) return (parts[1], defaultPort);
        }
        var colon = r.LastIndexOf(':');
        if (colon > 0 && !r.EndsWith(']') && int.TryParse(r[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            return (r[..colon].Trim('[', ']'), port);
        if (r.Length == 0) throw new CommunicationException("Adresse fehlt (z. B. 192.168.1.50:5000).");
        return (r.Trim('[', ']'), defaultPort);
    }
}

/// <summary>TCP-Client. Verbindet beim ersten Zugriff, nach Verbindungsabbruch beim nächsten erneut.</summary>
/// <summary>TLS-Einstellungen eines TCP-Clients (Option TLS=ja). Ohne Firmen-CA gilt die Systemvertrauenskette; Prüfung abschaltbar für Selbstzertifikate im Labor.</summary>
public sealed record TlsSettings(bool Enabled, string? ServerName = null, string? CaCertificatePath = null, bool VerifyCertificate = true)
{
    public static TlsSettings From(string host, InstrumentOptions o)
        => new(o.Flag("TLS"), o.Text("TLS-Servername") ?? host, o.Text("Firmen-CA"), !(o.Text("TLS-Prüfung")?.Equals("aus", StringComparison.OrdinalIgnoreCase) ?? false));
}

public sealed class TcpTransport : IStreamTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private readonly TlsSettings _tls;
    private TcpClient? _client;
    private Stream? _stream;

    public TcpTransport(string host, int port, TimeSpan connectTimeout, TlsSettings? tls = null)
    {
        _host = host;
        _port = port;
        _connectTimeout = connectTimeout;
        _tls = tls ?? new TlsSettings(false);
    }

    public string Description => $"TCP {_host}:{_port}{(_tls.Enabled ? " (TLS)" : "")}";
    public bool IsOpen => _client?.Connected == true;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (IsOpen) return;
        Close();
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_connectTimeout);
        try
        {
            await client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            _stream = _tls.Enabled ? await AuthenticateAsync(client, cts.Token).ConfigureAwait(false) : client.GetStream();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new CommunicationException($"Keine Verbindung zu {_host}:{_port}: {(ex is OperationCanceledException ? "Zeitüberschreitung" : ex.Message)}", ex);
        }
        catch (AuthenticationException ex)
        {
            client.Dispose();
            throw new CommunicationException($"TLS-Handschlag mit {_host}:{_port} fehlgeschlagen: {ex.Message}", ex);
        }
        _client = client;
    }

    private async Task<SslStream> AuthenticateAsync(TcpClient client, CancellationToken token)
    {
        X509Certificate2? ca = null;
        if (!string.IsNullOrWhiteSpace(_tls.CaCertificatePath))
        {
            try { ca = X509CertificateLoader.LoadCertificateFromFile(_tls.CaCertificatePath); }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
            {
                throw new CommunicationException($"Firmen-CA „{_tls.CaCertificatePath}“ nicht lesbar: {ex.Message}", ex);
            }
        }
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, cert, chain, errors) =>
        {
            if (!_tls.VerifyCertificate || errors == SslPolicyErrors.None) return true;
            if (ca is null || cert is null) return false;
            // gegen die Firmen-CA prüfen (Selbstzertifikate ohne öffentliche Vertrauenskette)
            using var verify = new X509Chain { ChainPolicy = { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck } };
            verify.ChainPolicy.CustomTrustStore.Add(ca);
            return verify.Build(new X509Certificate2(cert));
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _tls.ServerName,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, token).ConfigureAwait(false);
        return ssl;
    }

    private Stream Stream => _stream ?? throw new CommunicationException($"{Description} ist nicht verbunden.");

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Close();
            throw new CommunicationException($"{Description}: Verbindung unterbrochen ({ex.Message}).", ex);
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var n = await Stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                Close();
                throw new CommunicationException($"{Description}: Gegenstelle hat die Verbindung geschlossen.");
            }
            return n;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (IOException ex)
        {
            Close();
            throw new CommunicationException($"{Description}: Verbindung unterbrochen ({ex.Message}).", ex);
        }
    }

    public void DiscardInput()
    {
        if (_stream is null || _client is null) return;
        var buffer = new byte[4096];
        try
        {
            while (_client is { Available: > 0 }) _ = _stream.Read(buffer, 0, buffer.Length);
        }
        catch (IOException)
        {
            Close();
        }
    }

    public void Close()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
    }

    public void Dispose() => Close();
}

/// <summary>UDP: jedes Senden ein Datagramm an die Gegenstelle; empfangene Datagramme werden aneinandergereiht.</summary>
public sealed class UdpTransport(string host, int port, int localPort) : IStreamTransport
{
    private UdpClient? _client;
    private IPEndPoint? _remote;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private byte[]? _rest;

    public string Description => $"UDP {host}:{port}{(localPort > 0 ? $" (lokal {localPort})" : "")}";
    public bool IsOpen => _client is not null;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (IsOpen) return;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                      ?? throw new CommunicationException($"Adresse {host} nicht auflösbar.");
        _remote = new IPEndPoint(address, port);
        _client = new UdpClient(new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, localPort));
    }

    private UdpClient Client => _client ?? throw new CommunicationException($"{Description} ist nicht geöffnet.");

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => await Client.SendAsync(data, _remote!, cancellationToken).ConfigureAwait(false);

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_rest is null && !_queue.TryDequeue(out _rest))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                var result = await Client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                _rest = result.Buffer;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
        }
        var n = Math.Min(buffer.Length, _rest!.Length);
        _rest.AsSpan(0, n).CopyTo(buffer.Span);
        _rest = n == _rest.Length ? null : _rest[n..];
        return n;
    }

    public void DiscardInput()
    {
        _rest = null;
        _queue.Clear();
        while (_client is { Available: > 0 })
        {
            IPEndPoint? any = null;
            _ = _client.Receive(ref any);
        }
    }

    public void Close()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => Close();
}

/// <summary>Speicherschleife für Tests: was die Gegenseite schreibt, liest diese Seite (Paar über <see cref="Pair"/>).</summary>
public sealed class LoopbackTransport : IStreamTransport
{
    private readonly System.Threading.Channels.Channel<byte[]> _inbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private LoopbackTransport? _peer;
    private byte[]? _rest;

    public static (LoopbackTransport A, LoopbackTransport B) Pair()
    {
        var a = new LoopbackTransport();
        var b = new LoopbackTransport();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public string Description => "Testschleife";
    public bool IsOpen => true;
    public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _peer!._inbox.Writer.TryWrite(data.ToArray());
        return Task.CompletedTask;
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_rest is null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                _rest = await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
        }
        var n = Math.Min(buffer.Length, _rest.Length);
        _rest.AsSpan(0, n).CopyTo(buffer.Span);
        _rest = n == _rest.Length ? null : _rest[n..];
        return n;
    }

    public void DiscardInput()
    {
        _rest = null;
        while (_inbox.Reader.TryRead(out _)) { }
    }

    public void Close() { }

    public void Dispose() { }
}
