/**
 * @file
 * @brief Bindet serielle Textkommunikation an die gemeinsame Instrument-IO-Schnittstelle.
 */

using System.Text;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Instruments;

/// <summary>SCPI über einen COM-Port, auch mit USB-RS232-Adapter; keine VISA-Laufzeit erforderlich.</summary>
public sealed class SerialInstrumentIo : IInstrumentIo
{
    private readonly IStreamTransport _transport;
    private readonly string _ending;
    public SerialInstrumentIo(string port, InstrumentOptions options)
        : this(port, options, useRs485Rts: false) { }

    public SerialInstrumentIo(string port, InstrumentOptions options, bool useRs485Rts)
        : this(port, new SerialTransport(CreateSettings(port, options, useRs485Rts)), options.Text("Zeilenende") ?? "LF") { }

    private static SerialSettings CreateSettings(string port, InstrumentOptions options, bool useRs485Rts)
    {
        var settings = SerialSettings.From(port, options);
        return useRs485Rts ? settings with { Rs485Rts = true } : settings;
    }
    internal SerialInstrumentIo(string port, IStreamTransport transport, string ending)
    {
        Resource = port; _transport = transport;
        _ending = ending.ToUpperInvariant() switch { "LF" => "\n", "CRLF" => "\r\n", "CR" => "\r", _ => throw new InstrumentException("Zeilenende muss LF, CRLF oder CR sein.") };
    }
    public string Resource { get; }
    public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var text = Encoding.ASCII.GetString(data).TrimEnd('\n');
        return WriteExactAsync(Encoding.ASCII.GetBytes(text + _ending), timeout, cancellationToken);
    }

    internal async Task WriteExactAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); stop.CancelAfter(timeout);
            await _transport.OpenAsync(stop.Token).ConfigureAwait(false);
            await _transport.WriteAsync(data, stop.Token).ConfigureAwait(false);
        }
        catch (Testumgebung.Contracts.CommunicationException ex) { throw new InstrumentException(ex.Message, ex); }
    }
    public async Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[4096];
            var count = await _transport.ReadAsync(buffer, timeout, cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new TimeoutException("Keine Antwort am COM-Port " + Resource);
            return new IoChunk(buffer[..count], buffer[count - 1] == (byte)_ending[^1]);
        }
        catch (Testumgebung.Contracts.CommunicationException ex) { throw new InstrumentException(ex.Message, ex); }
    }
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _transport.Close(); return Task.CompletedTask;
    }
    public void Dispose() => _transport.Dispose();
}
