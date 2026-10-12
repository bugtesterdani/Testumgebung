using System.Net;
using System.Net.Sockets;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments.Communication;

/// <summary>
/// Modbus-Gegenstelle für Tests und Vorführung: beantwortet Funktionen 1–6, 15, 16 aus einem <see cref="IModbusAdapter"/>
/// (z. B. dem simulierten Registerabbild). Ausnahmen der Gegenstelle werden als Modbus-Ausnahmeantwort gesendet.
/// </summary>
public static class ModbusSlave
{
    /// <summary>PDU (Funktion + Daten) beantworten.</summary>
    public static async Task<byte[]> HandleAsync(IModbusAdapter backend, byte unit, byte[] pdu, CancellationToken ct)
    {
        var function = pdu[0];
        var u = unit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int A(int i) => (pdu[i] << 8) | pdu[i + 1];
        try
        {
            switch (function)
            {
                case 1 or 2:
                {
                    var bits = await backend.ReadBitsAsync(u, function == 1 ? ModbusArea.Coil : ModbusArea.DiscreteInput, A(1), A(3), ct).ConfigureAwait(false);
                    var packed = new byte[(bits.Length + 7) / 8];
                    for (var i = 0; i < bits.Length; i++)
                        if (bits[i]) packed[i / 8] |= (byte)(1 << (i % 8));
                    return [function, (byte)packed.Length, .. packed];
                }
                case 3 or 4:
                {
                    var regs = await backend.ReadRegistersAsync(u, function == 3 ? ModbusArea.HoldingRegister : ModbusArea.InputRegister, A(1), A(3), ct).ConfigureAwait(false);
                    return [function, (byte)(regs.Length * 2), .. regs.SelectMany(r => new[] { (byte)(r >> 8), (byte)r })];
                }
                case 5:
                    await backend.WriteBitsAsync(u, A(1), [pdu[3] == 0xFF], ct).ConfigureAwait(false);
                    return pdu[..5];
                case 6:
                    await backend.WriteRegistersAsync(u, A(1), [(ushort)A(3)], ct).ConfigureAwait(false);
                    return pdu[..5];
                case 15:
                {
                    var count = A(3);
                    var values = Enumerable.Range(0, count).Select(i => (pdu[6 + i / 8] >> (i % 8) & 1) != 0).ToArray();
                    await backend.WriteBitsAsync(u, A(1), values, ct).ConfigureAwait(false);
                    return pdu[..5];
                }
                case 16:
                {
                    var count = A(3);
                    var values = Enumerable.Range(0, count).Select(i => (ushort)A(6 + 2 * i)).ToArray();
                    await backend.WriteRegistersAsync(u, A(1), values, ct).ConfigureAwait(false);
                    return pdu[..5];
                }
                default:
                    return [(byte)(function | 0x80), 1];
            }
        }
        catch (ModbusException ex)
        {
            return [(byte)(function | 0x80), ex.Code];
        }
        catch (Exception ex) when (ex is ArgumentException or CommunicationException)
        {
            return [(byte)(function | 0x80), 4];
        }
    }
}

/// <summary>Modbus-TCP-Server auf localhost (Laborsimulator, Tests der Rahmenbildung).</summary>
public sealed class ModbusTcpSimulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IModbusAdapter _backend;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private int _disposed;

    public ModbusTcpSimulatorServer(IModbusAdapter backend, int port = 0)
    {
        _backend = backend;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoop();
    }

    public int Port { get; }

    /// <summary>Antworten unterdrücken (Zeitüberschreitung testen).</summary>
    public bool Silent { get; set; }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Serve(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                    var length = (header[4] << 8) | header[5];
                    var pdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(pdu, _cts.Token).ConfigureAwait(false);
                    if (Silent) continue;
                    var response = await ModbusSlave.HandleAsync(_backend, header[6], pdu, _cts.Token).ConfigureAwait(false);
                    byte[] frame = [header[0], header[1], 0, 0, (byte)((response.Length + 1) >> 8), (byte)(response.Length + 1), header[6], .. response];
                    await stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or EndOfStreamException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _listener.Stop();
        await _accept.ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>Modbus-RTU-Gegenstelle an einem Transport (z. B. Testschleife oder COM-Port eines Nullmodems).</summary>
public sealed class ModbusRtuResponder : IAsyncDisposable
{
    private readonly IStreamTransport _transport;
    private readonly IModbusAdapter _backend;
    private readonly byte _unit;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public ModbusRtuResponder(IStreamTransport transport, IModbusAdapter backend, byte unit = 1)
    {
        (_transport, _backend, _unit) = (transport, backend, unit);
        _loop = Loop();
    }

    /// <summary>Nächste Antwort mit falscher Prüfsumme senden (Fehlerfall testen).</summary>
    public bool CorruptNext { get; set; }

    private async Task Loop()
    {
        var receiver = new StreamReceiver(_transport);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[] frame;
                try
                {
                    // Ein Rahmen = Bytes bis zur Ruhe (3,5 Zeichen) – in der Testschleife kommt er am Stück.
                    frame = await receiver.ReceiveAsync(new ReceiveRule(null, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5)), _cts.Token).ConfigureAwait(false);
                }
                catch (CommunicationTimeoutException)
                {
                    continue;
                }
                if (frame.Length < 4 || ModbusDevice.Crc16(frame.AsSpan(0, frame.Length - 2)) != (ushort)(frame[^2] | (frame[^1] << 8))) continue;
                if (frame[0] != _unit) continue;
                var response = await ModbusSlave.HandleAsync(_backend, frame[0], frame[1..^2], _cts.Token).ConfigureAwait(false);
                byte[] body = [_unit, .. response];
                var crc = ModbusDevice.Crc16(body);
                if (CorruptNext)
                {
                    crc ^= 0xFFFF;
                    CorruptNext = false;
                }
                await _transport.WriteAsync((byte[])[.. body, (byte)crc, (byte)(crc >> 8)], _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}

/// <summary>TCP-Gegenstelle mit Antwortfunktion je Zeile (Tests, Vorführung von Textprotokollen).</summary>
public sealed class LineServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string?> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public LineServer(Func<string, string?> respond)
    {
        _respond = respond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoop();
    }

    public int Port { get; }

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Serve(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream(), CommText.Latin1);
                await using var writer = new StreamWriter(client.GetStream(), CommText.Latin1) { AutoFlush = true, NewLine = "\r\n" };
                while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
                    if (_respond(line) is { } answer) await writer.WriteAsync(answer).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _accept.ConfigureAwait(false);
        _cts.Dispose();
    }
}
