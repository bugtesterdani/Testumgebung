/**
 * @file
 * @brief Trennt Telnet-Steuersequenzen von Nutzdaten und beantwortet die unterstützten Aushandlungsbefehle.
 */

using System.Diagnostics;

namespace Testumgebung.Instruments.Communication;

/// <summary>Telnet-NVT: IAC-Escaping, Ablehnen optionaler Funktionen, Entfernen von Aushandlungen.</summary>
public sealed class TelnetTransport(IStreamTransport inner) : IStreamTransport
{
    private int _state;
    private byte _verb;
    private bool _cr;
    public string Description => "Telnet " + inner.Description;
    public bool IsOpen => inner.IsOpen;
    public Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var escaped = new List<byte>();
        foreach (var span in data.Span) { escaped.Add(span); if (span == 255) escaped.Add(span); }
        return inner.WriteAsync(escaped.ToArray(), cancellationToken);
    }
    public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (buffer.Length == 0) return 0;
        var clock = Stopwatch.StartNew();
        var raw = new byte[buffer.Length];
        while (clock.Elapsed < timeout)
        {
            var count = await inner.ReadAsync(raw, timeout - clock.Elapsed, cancellationToken).ConfigureAwait(false);
            if (count == 0) return 0;
            var output = new List<byte>();
            var replies = new List<byte>();
            for (var index = 0; index < count; index++)
            {
                Consume(raw[index], output, replies);
            }
            if (replies.Count > 0) await inner.WriteAsync(replies.ToArray(), cancellationToken).ConfigureAwait(false);
            if (output.Count > 0) { output.ToArray().CopyTo(buffer); return output.Count; }
        }
        return 0;
    }
    private void Consume(byte dataByte, List<byte> output, List<byte> replies)
    {
        switch (_state)
        {
            case 0:
                if (dataByte == 255) { _state = 1; break; }
                if (!(_cr && dataByte == 0)) output.Add(dataByte);
                _cr = dataByte == 13;
                break;
            case 1:
                ConsumeCommand(dataByte, output);
                break;
            case 2:
                if (_verb is 251 or 253) replies.AddRange(new byte[] { 255, _verb == 251 ? (byte)254 : (byte)252, dataByte });
                _state = 0;
                break;
            case 3: if (dataByte == 255) _state = 4; break;
            case 4: _state = dataByte == 240 ? 0 : 3; break;
        }
    }

    private void ConsumeCommand(byte commandByte, List<byte> output)
    {
        if (commandByte == 255) { output.Add(255); _state = 0; }
        else if (commandByte is 251 or 252 or 253 or 254) { _verb = commandByte; _state = 2; }
        else _state = commandByte == 250 ? 3 : 0;
    }

    public void DiscardInput() { inner.DiscardInput(); _state = 0; _cr = false; }
    public void Close() { inner.Close(); _state = 0; _cr = false; }
    public void Dispose() => inner.Dispose();
}
