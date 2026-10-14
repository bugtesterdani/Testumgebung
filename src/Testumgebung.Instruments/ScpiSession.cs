/**
 * @file
 * @brief Serialisiert SCPI-Befehle, Antwortlesen und Fehlerprüfung einer Geräteverbindung.
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Testumgebung.Instruments;

/// <summary>Antwort auf *IDN?: Hersteller, Modell, Seriennummer, Firmware.</summary>
public sealed record InstrumentIdentity(string Manufacturer, string Model, string Serial, string Firmware)
{
    public static InstrumentIdentity Parse(string idn)
    {
        var p = idn.Trim().Split(',', StringSplitOptions.TrimEntries);
        string At(int i) => i < p.Length ? p[i] : "";
        return new InstrumentIdentity(At(0), At(1), At(2), At(3));
    }

    public override string ToString() => $"{Manufacturer} {Model} (S/N {Serial}, FW {Firmware})";
}

/// <summary>
/// SCPI-Sitzung (IEEE 488.2) über einen <see cref="IInstrumentIo"/>: eine Transaktion zur Zeit, Zeitlimit je Befehl,
/// Fehlerwarteschlange (SYST:ERR?) nach Einstellbefehlen, IEEE-Blöcke (#&lt;n&gt;&lt;Länge&gt;&lt;Daten&gt;), Zahlen invariant.
/// Abbruch oder Zeitlimit während einer Antwort: Transport zurücksetzen (Device Clear bzw. neue Verbindung),
/// damit eine verspätete Antwort nie dem nächsten Befehl zugeordnet wird.
/// </summary>
public sealed class ScpiSession : IDisposable
{
    private readonly Func<IInstrumentIo> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _log = new();
    private IInstrumentIo? _io;
    private bool _dirty;

    public ScpiSession(Func<IInstrumentIo> connect, TimeSpan? timeout = null)
    {
        _connect = connect;
        Timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public ScpiSession(string resource, TimeSpan? timeout = null) : this(() => InstrumentIo.Open(resource), timeout)
    {
        Resource = resource;
    }

    public ScpiSession(string resource, Func<IInstrumentIo> connect, TimeSpan? timeout = null) : this(connect, timeout)
    { Resource = resource; }

    public string Resource { get; } = "";

    public TimeSpan Timeout { get; set; }

    /// <summary>Nach Einstellbefehlen die Fehlerwarteschlange prüfen (empfohlen; kostet eine Abfrage).</summary>
    public bool CheckErrors { get; set; } = true;

    /// <summary>Chroma meldet den leeren Fehlerpuffer als Text statt als numerischen SCPI-Code.</summary>
    public bool TextErrorReplies { get; set; }

    /// <summary>Letzte Befehle und Antworten (Treiberprotokoll für die Diagnose).</summary>
    public IReadOnlyList<string> Log => _log.ToArray();

    public event Action<string>? Logged;

    private void Note(string line)
    {
        _log.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}");
        while (_log.Count > 300) _log.TryDequeue(out _);
        Logged?.Invoke(line);
    }

    private IInstrumentIo Io => _io ??= _connect();

    /// <summary>Befehl senden; bei <see cref="CheckErrors"/> anschließend Fehlerwarteschlange lesen.</summary>
    public async Task WriteAsync(string command, CancellationToken ct = default)
    {
        // Eine SCPI-Transaktion umfasst auch ihre Antwort bzw. Fehlerabfrage.
        // Sonst könnten parallele Aufrufer die Antwort des jeweils anderen lesen.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            await SendAsync(command, ct).ConfigureAwait(false);
            if (CheckErrors) await ThrowOnErrorsAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Binärblock senden (z. B. Arbiträrkurve): <paramref name="prefix"/> + #-Block + LF.</summary>
    public async Task WriteBlockAsync(string prefix, byte[] block, CancellationToken ct = default)
    {
        var digits = block.Length.ToString(CultureInfo.InvariantCulture);
        var header = Encoding.ASCII.GetBytes($"{prefix}#{digits.Length}{digits}");
        var message = new byte[header.Length + block.Length + 1];
        header.CopyTo(message, 0);
        block.CopyTo(message, header.Length);
        message[^1] = (byte)'\n';
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            Note($"→ {prefix}#{digits.Length}{digits}<{block.Length} Bytes>");
            await Guard(() => Io.WriteAsync(message, Timeout, ct)).ConfigureAwait(false);
            if (CheckErrors) await ThrowOnErrorsAsync(prefix + "<Block>", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Abfrage mit Textantwort (ohne Zeilenende).</summary>
    public async Task<string> QueryAsync(string command, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            await SendAsync(command, ct).ConfigureAwait(false);
            var reply = await ReceiveAsync(timeout ?? Timeout, ct).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(reply).TrimEnd('\r', '\n');
            Note("← " + (text.Length > 200 ? text[..200] + "…" : text));
            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<double> QueryDoubleAsync(string command, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var text = await QueryAsync(command, ct, timeout).ConfigureAwait(false);
        var first = text.Split(',')[0].Trim().Trim('"');
        if (!double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new InstrumentException($"{command}: unerwartete Antwort „{text}“.");
        if (Math.Abs(v) >= 9.9e37) throw new InstrumentException($"{command}: Messbereich überschritten (Überlauf {text}).");
        return v;
    }

    /// <summary>Abfrage mit IEEE-488.2-Block (definite length) – liefert die Nutzdaten.</summary>
    public async Task<byte[]> QueryBlockAsync(string command, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            await SendAsync(command, ct).ConfigureAwait(false);
            var reply = await ReceiveAsync(timeout ?? Timeout, ct).ConfigureAwait(false);
            var (offset, length) = BlockBounds(reply) ?? throw new InstrumentException($"{command}: kein IEEE-Block in der Antwort.");
            Note($"← #<{length} Bytes>");
            return reply[offset..(offset + length)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>IEEE-488.2-Daten ohne vollständige Antwortkopie an einen blockweisen Empfänger liefern.</summary>
    public async Task QueryBlockStreamAsync(string command, Action<int> begin, Action<ReadOnlyMemory<byte>> append,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            await SendAsync(command, ct).ConfigureAwait(false);
            var header = new List<byte>(11);
            int? length = null;
            var remaining = 0;
            while (true)
            {
                var chunk = await Io.ReadAsync(timeout ?? Timeout, ct).ConfigureAwait(false);
                var offset = 0;
                ReadStreamingHeader(chunk.Data, header, ref offset, ref length, ref remaining, begin);
                if (length is not null)
                {
                    var count = Math.Min(remaining, chunk.Data.Length - offset);
                    if (count > 0) append(chunk.Data.AsMemory(offset, count));
                    remaining -= count;
                    offset += count;
                    if (remaining == 0 && (chunk.End || (offset < chunk.Data.Length && chunk.Data[^1] == '\n')))
                    { Note($"← #<{length} Bytes, blockweise>"); return; }
                }
                if (chunk.End) throw new InstrumentException("IEEE-Block abgeschnitten.");
            }
        }
        catch
        {
            _dirty = true;
            throw;
        }
        finally { _gate.Release(); }
    }

    private static void ReadStreamingHeader(byte[] data, List<byte> header, ref int offset, ref int? length, ref int remaining, Action<int> begin)
    {
        while (length is null && offset < data.Length)
        {
            header.Add(data[offset++]);
            if (header[0] != '#' || (header.Count >= 2 && (header[1] < '1' || header[1] > '9')))
                throw new InstrumentException("Streaming benötigt einen definite-length IEEE-Block.");
            if (header.Count >= 2 && header.Count == 2 + header[1] - '0')
            {
                if (!int.TryParse(Encoding.ASCII.GetString(header.Skip(2).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 100_000_000)
                    throw new InstrumentException("Ungültige IEEE-Blocklänge.");
                length = remaining = count;
                begin(count);
            }
        }
    }

    public async Task<InstrumentIdentity> IdentifyAsync(CancellationToken ct = default)
        => InstrumentIdentity.Parse(await QueryAsync("*IDN?", ct).ConfigureAwait(false));

    /// <summary>Fehlerwarteschlange vollständig lesen (+0 = keine Fehler).</summary>
    public async Task<IReadOnlyList<string>> ReadErrorsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PrepareAsync(ct).ConfigureAwait(false);
            return await ErrorsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wartet, bis alle Befehle ausgeführt sind (*OPC?), z. B. nach einer Aufnahme.</summary>
    public async Task WaitCompleteAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var r = await QueryAsync("*OPC?", ct, timeout).ConfigureAwait(false);
        if (r.Trim() != "1") throw new InstrumentException($"*OPC? lieferte „{r}“.");
    }

    /// <summary>Verbindung neu aufbauen bzw. Device Clear (nach Abbruch/Zeitlimit automatisch).</summary>
    public async Task ResetIoAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_io is not null) await _io.ClearAsync(ct).ConfigureAwait(false);
            _dirty = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        if (!_dirty) return;
        Note("· Device Clear nach Abbruch/Zeitlimit");
        await Io.ClearAsync(ct).ConfigureAwait(false);
        _dirty = false;
    }

    private Task SendAsync(string command, CancellationToken ct)
    {
        Note("→ " + command);
        return Guard(() => Io.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), Timeout, ct));
    }

    private async Task Guard(Func<Task> io)
    {
        try
        {
            await io().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InstrumentException)
        {
            _dirty = true;
            Note("! " + ex.Message);
            throw;
        }
    }

    private async Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        var buffer = new List<byte>(256);
        try
        {
            while (true)
            {
                var chunk = await Io.ReadAsync(timeout, ct).ConfigureAwait(false);
                buffer.AddRange(chunk.Data);
                if (Complete(buffer, chunk.End)) return [.. buffer];
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or InstrumentException)
        {
            _dirty = true;
            Note("! " + ex.Message);
            throw;
        }
    }

    /// <summary>Nachricht vollständig: Block mit voller Länge bzw. Zeilenende (Socket) oder END (VISA).</summary>
    private static bool Complete(List<byte> data, bool end)
    {
        if (data.Count > 0 && data[0] == (byte)'#') return CompleteBlock(data, end);
        return end || data.Count > 0 && data[^1] == '\n';
    }

    private static bool CompleteBlock(List<byte> data, bool end)
    {
        if (!HasBlockBytes(data, 2, end, "IEEE-Blockkopf abgeschnitten.")) return false;
        var digits = data[1] - '0';
        if (digits is < 0 or > 9) throw new InstrumentException("Ungültiger IEEE-Blockkopf.");
        if (digits == 0) return end || data[^1] == '\n'; // indefinite block: bis zum Ende
        if (!HasBlockBytes(data, 2 + digits, end, "IEEE-Blockkopf abgeschnitten.")) return false;
        if (!int.TryParse(Encoding.ASCII.GetString([.. data.GetRange(2, digits)]), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw new InstrumentException("Ungültige IEEE-Blocklänge.");
        var total = 2 + digits + length;
        if (!HasBlockBytes(data, total, end, "IEEE-Block abgeschnitten.")) return false;
        return end || data.Count > total && data[total] == '\n';
    }

    private static bool HasBlockBytes(List<byte> data, int required, bool end, string error)
    {
        if (data.Count >= required) return true;
        if (end) throw new InstrumentException(error);
        return false;
    }

    internal static (int Offset, int Length)? BlockBounds(byte[] reply)
    {
        if (reply.Length < 2 || reply[0] != '#') return null;
        var digits = reply[1] - '0';
        if (digits is < 0 or > 9) return null;
        if (digits == 0) return (2, Math.Max(0, reply.Length - 2 - (reply[^1] == '\n' ? 1 : 0)));
        if (reply.Length < 2 + digits) throw new InstrumentException("IEEE-Blockkopf abgeschnitten.");
        if (!int.TryParse(Encoding.ASCII.GetString(reply, 2, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            throw new InstrumentException("Ungültige IEEE-Blocklänge.");
        if (length > reply.Length - 2 - digits) throw new InstrumentException("IEEE-Block abgeschnitten.");
        return (2 + digits, length);
    }

    private async Task<IReadOnlyList<string>> ErrorsAsync(CancellationToken ct)
    {
        var errors = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            await SendAsync("SYST:ERR?", ct).ConfigureAwait(false);
            var text = Encoding.ASCII.GetString(await ReceiveAsync(Timeout, ct).ConfigureAwait(false)).Trim();
            if (text.StartsWith("+0", StringComparison.Ordinal) || text.StartsWith("0,", StringComparison.Ordinal)) break;
            if (TextErrorReplies && text.Equals("No Error", StringComparison.OrdinalIgnoreCase)) break;
            Note("← " + text);
            errors.Add(text);
        }
        return errors;
    }

    private async Task ThrowOnErrorsAsync(string command, CancellationToken ct)
    {
        var errors = await ErrorsAsync(ct).ConfigureAwait(false);
        if (errors.Count > 0) throw new InstrumentException($"„{command}“ abgelehnt: {string.Join("; ", errors)}");
    }

    public void Dispose()
    {
        _io?.Dispose();
        _io = null;
        _gate.Dispose();
    }
}
