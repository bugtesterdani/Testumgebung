/**
 * @file
 * @brief Setzt Diagnose- und Transportprotokolle auf CAN um; Rahmenübertragung und Diagnosebewertung bleiben getrennte Ebenen.
 */

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Testumgebung.Contracts;

// ====================================================================== ISO-TP (ISO 15765-2)

/// <summary>
/// Adressierung und Parameter einer ISO-TP-Verbindung (normale Adressierung): Anfrage auf <see cref="TxId"/>, Antwort auf
/// <see cref="RxId"/>. <see cref="Fd"/> = CAN-FD-Rahmen bis 64 Bytes.
/// </summary>
public sealed record IsoTpSettings(uint TxId, uint RxId, bool Extended = false, bool Fd = false, byte Padding = 0xCC, byte BlockSize = 0, byte SeparationTime = 0)
{
    /// <summary>Warten auf Flusssteuerung bzw. nächsten Folgerahmen (N_Bs/N_Cr).</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Erweiterte bzw. gemischte Adressierung: erstes Datenbyte jedes gesendeten Rahmens (Zieladresse N_TA bzw. Adresserweiterung
    /// N_AE). Null = normale Adressierung.
    /// </summary>
    public byte? TxAddress { get; init; }

    /// <summary>Erstes Datenbyte, das empfangene Rahmen tragen müssen (andere werden ignoriert). Null = normale Adressierung.</summary>
    public byte? RxAddress { get; init; }

    /// <summary>Nutzbare Bytes je Rahmen nach dem Adressbyte.</summary>
    public int FrameSize => (Fd ? 64 : 8) - (TxAddress is null ? 0 : 1);

    /// <summary>Größe einer Folgeantwort bzw. eines empfangenen Rahmens ohne Adressbyte.</summary>
    internal int RxFrameSize => (Fd ? 64 : 8) - (RxAddress is null ? 0 : 1);

    /// <summary>„0x7E0/0x7E8“ zur Anzeige.</summary>
    public override string ToString()
        => $"0x{TxId:X}{(TxAddress is { } t ? $"[{t:X2}]" : "")}→0x{RxId:X}{(RxAddress is { } r ? $"[{r:X2}]" : "")}";
}

/// <summary>ISO-TP: Segmentierung (Einzel-, Erst-, Folgerahmen) mit Flusssteuerung über eine CAN-Schnittstelle.</summary>
public static class IsoTp
{
    private static CanFilter Rx(IsoTpSettings s) => new(s.RxId, 0x1FFFFFFF, s.Extended);

    private static byte[] Pad(IsoTpSettings s, List<byte> data)
    {
        if (s.TxAddress is { } address) data.Insert(0, address);
        var target = s.Fd ? CanFrame.FdLengths.First(l => l >= Math.Max(8, data.Count)) : 8;
        while (data.Count < target) data.Add(s.Padding);
        return [.. data];
    }

    private static Task SendFrame(ICanAdapter can, string channel, IsoTpSettings s, List<byte> data, CancellationToken token)
        => can.SendAsync(channel, new CanFrame(s.TxId, Pad(s, data), s.Extended, s.Fd, s.Fd), token);

    /// <summary>Nächster Rahmen auf der Antwort-ID mit passendem Adressbyte; liefert die Daten ohne Adressbyte.</summary>
    private static async Task<byte[]> ReceiveFrameAsync(ICanAdapter can, string channel, IsoTpSettings s, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        for (var skipped = 0; ; skipped++)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || skipped > 1000) throw new CommunicationTimeoutException($"ISO-TP {s}: kein Rahmen.", []);
            var frame = await can.ReceiveAsync(channel, Rx(s), remaining, token).ConfigureAwait(false);
            if (s.RxAddress is not { } address) return frame.Data;
            if (frame.Data.Length > 0 && frame.Data[0] == address) return frame.Data[1..];
        }
    }

    /// <summary>Nutzdaten senden (bis 4 GiB laut Norm; praktisch durch die Gegenstelle begrenzt).</summary>
    public static async Task SendAsync(ICanAdapter can, string channel, IsoTpSettings s, byte[] payload, CancellationToken token)
    {
        if (payload.Length == 0) throw new CommunicationException("ISO-TP: leere Nachricht.");
        var classic = s.FrameSize - (s.Fd ? 57 : 1);   // klassischer Einzelrahmen: 7 bzw. 6 Bytes
        var single = s.Fd ? s.FrameSize - 2 : classic;
        if (payload.Length <= classic)
        {
            await SendFrame(can, channel, s, [(byte)payload.Length, .. payload], token).ConfigureAwait(false);
            return;
        }
        if (payload.Length <= single)
        {
            await SendFrame(can, channel, s, [0x00, (byte)payload.Length, .. payload], token).ConfigureAwait(false);
            return;
        }

        List<byte> first = payload.Length <= 4095
            ? [(byte)(0x10 | payload.Length >> 8), (byte)payload.Length]
            : [0x10, 0x00, (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length];
        var offset = s.FrameSize - first.Count;
        first.AddRange(payload.Take(offset));
        await SendFrame(can, channel, s, first, token).ConfigureAwait(false);

        var sequence = 1;
        while (offset < payload.Length)
        {
            var (blockSize, separation) = await WaitFlowControlAsync(can, channel, s, token).ConfigureAwait(false);
            var sent = 0;
            while (offset < payload.Length && (blockSize == 0 || sent < blockSize))
            {
                if (sent > 0 || sequence > 1) await Separate(separation, token).ConfigureAwait(false);
                var chunk = payload.Skip(offset).Take(s.FrameSize - 1);
                await SendFrame(can, channel, s, [(byte)(0x20 | sequence & 0x0F), .. chunk], token).ConfigureAwait(false);
                offset += s.FrameSize - 1;
                sequence++;
                sent++;
            }
        }
    }

    private static Task Separate(byte st, CancellationToken token)
        => st switch
        {
            0 => Task.CompletedTask,
            <= 0x7F => Task.Delay(st, token),
            >= 0xF1 and <= 0xF9 => Task.Delay(1, token),
            _ => Task.Delay(127, token),
        };

    private static async Task<(int BlockSize, byte Separation)> WaitFlowControlAsync(ICanAdapter can, string channel, IsoTpSettings s, CancellationToken token)
    {
        for (var waits = 0; waits < 10; waits++)
        {
            byte[] fc;
            try
            {
                fc = await ReceiveFrameAsync(can, channel, s, s.FrameTimeout, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException)
            {
                throw new CommunicationTimeoutException($"ISO-TP {s}: keine Flusssteuerung der Gegenstelle (N_Bs {s.FrameTimeout.TotalMilliseconds:0} ms).", []);
            }
            if (fc.Length < 3 || fc[0] >> 4 != 3) continue; // anderer Rahmen auf der Antwort-ID
            switch (fc[0] & 0x0F)
            {
                case 0: return (fc[1], fc[2]);
                case 1: continue; // WAIT
                case 2: throw new CommunicationException($"ISO-TP {s}: Gegenstelle meldet Überlauf (Nachricht zu lang).");
                default: throw new CommunicationException($"ISO-TP {s}: ungültige Flusssteuerung {Convert.ToHexString(fc)}.");
            }
        }
        throw new CommunicationException($"ISO-TP {s}: Gegenstelle hält die Übertragung an (zu viele WAIT).");
    }

    /// <summary>Nächste vollständige Nachricht empfangen; sendet die Flusssteuerung (Blockgröße und Pause laut Einstellungen, 0 = alle ohne Pause).</summary>
    public static async Task<byte[]> ReceiveAsync(ICanAdapter can, string channel, IsoTpSettings s, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(1);
            byte[] d;
            try
            {
                d = await ReceiveFrameAsync(can, channel, s, remaining, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException ex)
            {
                throw new CommunicationTimeoutException($"ISO-TP {s}: keine Antwort innerhalb von {timeout.TotalMilliseconds:0} ms.", ex.Partial);
            }
            if (d.Length == 0) continue;
            switch (d[0] >> 4)
            {
                case 0:
                    {
                        var length = d[0] & 0x0F;
                        var start = 1;
                        if (length == 0 && d.Length > 8) { length = d[1]; start = 2; }
                        if (length == 0 || start + length > d.Length) throw new CommunicationException($"ISO-TP {s}: ungültiger Einzelrahmen {Convert.ToHexString(d)}.");
                        return d.AsSpan(start, length).ToArray();
                    }
                case 1:
                    return await ReceiveSegmentedAsync(can, channel, s, d, token).ConfigureAwait(false);
                default:
                    continue; // Folgerahmen/Flusssteuerung ohne Anfang: verwerfen
            }
        }
    }

    private static async Task<byte[]> ReceiveSegmentedAsync(ICanAdapter can, string channel, IsoTpSettings s, byte[] d, CancellationToken token)
    {
        var length = (d[0] & 0x0F) << 8 | d[1];
        var start = 2;
        if (length == 0) { length = d[2] << 24 | d[3] << 16 | d[4] << 8 | d[5]; start = 6; }
        if (length < 8) throw new CommunicationException($"ISO-TP {s}: Erstrahmen mit Länge {length}.");
        var result = new List<byte>(length);
        result.AddRange(d.Skip(start).Take(length));
        await SendFrame(can, channel, s, [0x30, s.BlockSize, s.SeparationTime], token).ConfigureAwait(false);
        var expected = 1;
        while (result.Count < length)
        {
            byte[] cf;
            try
            {
                cf = await ReceiveFrameAsync(can, channel, s, s.FrameTimeout, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException)
            {
                throw new CommunicationTimeoutException($"ISO-TP {s}: Folgerahmen {expected} fehlt ({result.Count}/{length} Bytes).", [.. result]);
            }
            if (cf.Length == 0 || cf[0] >> 4 != 2) continue;
            if ((cf[0] & 0x0F) != (expected & 0x0F))
                throw new CommunicationException($"ISO-TP {s}: Folgerahmen {cf[0] & 0x0F} statt {expected & 0x0F} (Reihenfolge).");
            result.AddRange(cf.Skip(1).Take(length - result.Count));
            if (s.BlockSize > 0 && expected % s.BlockSize == 0 && result.Count < length)
                await SendFrame(can, channel, s, [0x30, s.BlockSize, s.SeparationTime], token).ConfigureAwait(false);
            expected++;
        }
        return [.. result];
    }
}

// ====================================================================== UDS (ISO 14229)

/// <summary>Negative Antwort des Steuergeräts (0x7F Dienst NRC).</summary>
public sealed class UdsNegativeResponseException(byte service, byte code)
    : CommunicationException($"Negative Antwort auf Dienst 0x{service:X2}: NRC 0x{code:X2} {Uds.Describe(code)}")
{
    public byte Service { get; } = service;
    public byte Code { get; } = code;
}

/// <summary>UDS-Anfragen über ISO-TP: Wartezeiten P2/P2*, „Antwort folgt“ (NRC 0x78), negative Antworten.</summary>
public static class Uds
{
    private static readonly IReadOnlyDictionary<byte, string> NegativeResponseNames = new Dictionary<byte, string>
    {
        [0x10] = "generalReject (allgemein abgelehnt)",
        [0x11] = "serviceNotSupported (Dienst nicht unterstützt)",
        [0x12] = "subFunctionNotSupported (Unterfunktion nicht unterstützt)",
        [0x13] = "incorrectMessageLengthOrInvalidFormat (Länge/Format falsch)",
        [0x14] = "responseTooLong (Antwort zu lang)",
        [0x21] = "busyRepeatRequest (beschäftigt, wiederholen)",
        [0x22] = "conditionsNotCorrect (Bedingungen nicht erfüllt)",
        [0x24] = "requestSequenceError (falsche Reihenfolge)",
        [0x25] = "noResponseFromSubnetComponent (Teilnehmer antwortet nicht)",
        [0x26] = "failurePreventsExecutionOfRequestedAction (Fehler verhindert Ausführung)",
        [0x31] = "requestOutOfRange (außerhalb des Bereichs)",
        [0x33] = "securityAccessDenied (Zugriff verweigert)",
        [0x35] = "invalidKey (falscher Schlüssel)",
        [0x36] = "exceedNumberOfAttempts (zu viele Versuche)",
        [0x37] = "requiredTimeDelayNotExpired (Wartezeit läuft)",
        [0x70] = "uploadDownloadNotAccepted (Übertragung abgelehnt)",
        [0x71] = "transferDataSuspended (Übertragung unterbrochen)",
        [0x72] = "generalProgrammingFailure (Programmierfehler)",
        [0x73] = "wrongBlockSequenceCounter (Blockzähler falsch)",
        [0x78] = "requestCorrectlyReceived-ResponsePending (Antwort folgt)",
        [0x7E] = "subFunctionNotSupportedInActiveSession (Unterfunktion in Sitzung nicht erlaubt)",
        [0x7F] = "serviceNotSupportedInActiveSession (Dienst in Sitzung nicht erlaubt)",
    };

    public static string Describe(byte nrc)
    {
        if (NegativeResponseNames.TryGetValue(nrc, out var name)) return name;
        return nrc is >= 0x81 and <= 0x8F
            ? "Bedingung nicht erfüllt (Drehzahl, Spannung, Temperatur …)"
            : "herstellerspezifisch";
    }

    /// <summary>
    /// Anfrage senden und positive Antwort liefern (beginnend mit Dienst + 0x40). Bei „Unterdrücke positive Antwort“
    /// (Unterfunktion mit Bit 7) wird nur kurz auf eine negative Antwort gewartet und dann eine leere Antwort geliefert.
    /// </summary>
    /// <summary>
    /// Sicherheitszugang (Dienst 0x27): Seed anfordern (<paramref name="level"/>, ungerade), Schlüssel mit
    /// <paramref name="key"/> berechnen und senden (<paramref name="level"/>+1). Ein Seed aus lauter Nullen gilt als „bereits
    /// freigeschaltet“ und wird ohne Schlüssel akzeptiert. Liefert den berechneten Schlüssel.
    /// </summary>
    public static async Task<byte[]> SecurityAccessAsync(IDiagnosticTransport transport, byte level, Func<byte[], byte[]> key, TimeSpan p2, TimeSpan p2Star,
        CancellationToken token)
    {
        if (level == 0 || level % 2 == 0) throw new CommunicationException($"Sicherheitszugang: Seed-Stufe 0x{level:X2} muss ungerade sein (0x01, 0x03, …).");
        var seedResponse = await RequestAsync(transport, [0x27, level], p2, p2Star, token).ConfigureAwait(false);
        if (seedResponse.Length < 2) throw new CommunicationException("Sicherheitszugang: leere Seed-Antwort.");
        var seed = seedResponse[2..];
        if (seed.Length == 0 || seed.All(b => b == 0)) return []; // bereits freigeschaltet
        var computed = key(seed);
        await RequestAsync(transport, [0x27, (byte)(level + 1), .. computed], p2, p2Star, token).ConfigureAwait(false);
        return computed;
    }

    public static Task<byte[]> RequestAsync(ICanAdapter can, string channel, IsoTpSettings s, byte[] request, TimeSpan p2, TimeSpan p2Star,
        CancellationToken token)
        => RequestAsync(new IsoTpTransport(can, channel, s), request, p2, p2Star, token);

    /// <summary>UDS über einen beliebigen Diagnosetransport (ISO-TP auf CAN, DoIP auf Ethernet).</summary>
    public static async Task<byte[]> RequestAsync(IDiagnosticTransport transport, byte[] request, TimeSpan p2, TimeSpan p2Star, CancellationToken token)
    {
        if (request.Length == 0) throw new CommunicationException("UDS: leere Anfrage.");
        var suppress = request.Length >= 2 && (request[1] & 0x80) != 0 && request[0] is 0x10 or 0x11 or 0x3E or 0x85 or 0x28 or 0x31 or 0x27;
        transport.DiscardInput();
        await transport.SendAsync(request, token).ConfigureAwait(false);
        var wait = p2;
        var pending = 0;
        while (true)
        {
            byte[] response;
            try
            {
                response = await transport.ReceiveAsync(wait, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException) when (suppress && pending == 0)
            {
                return [];
            }
            catch (CommunicationTimeoutException ex)
            {
                throw new CommunicationTimeoutException(
                    $"UDS 0x{request[0]:X2} an {transport}: keine Antwort innerhalb von {wait.TotalMilliseconds:0} ms{(pending > 0 ? $" nach {pending}× „Antwort folgt“" : "")}.", ex.Partial);
            }
            switch (ClassifyResponse(request[0], response))
            {
                case UdsResponseKind.Pending:
                    if (++pending > 100) throw new CommunicationException("UDS: Steuergerät meldet über 100× „Antwort folgt“.");
                    wait = p2Star;
                    continue;
                case UdsResponseKind.Positive:
                    return response;
                default:
                    continue;
            }
        }
    }
    private enum UdsResponseKind { Positive, Pending, OtherService }

    private static UdsResponseKind ClassifyResponse(byte service, byte[] response)
    {
        if (response.Length >= 3 && response[0] == 0x7F && response[1] == service)
        {
            if (response[2] == 0x78) return UdsResponseKind.Pending;
            throw new UdsNegativeResponseException(service, response[2]);
        }
        if (response.Length > 0 && response[0] == (byte)(service + 0x40)) return UdsResponseKind.Positive;
        if (response.Length >= 1 && response[0] == 0x7F) return UdsResponseKind.OtherService;
        throw new CommunicationException($"UDS: unerwartete Antwort {Convert.ToHexString(response)} auf 0x{service:X2}.");
    }

}

/// <summary>Transport für Diagnosenachrichten (eine Nachricht = eine UDS-Anfrage bzw. -Antwort).</summary>
public interface IDiagnosticTransport
{
    Task SendAsync(byte[] message, CancellationToken cancellationToken);

    /// <summary>Nächste Nachricht vom Steuergerät; <see cref="CommunicationTimeoutException"/> nach dem Zeitlimit.</summary>
    Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken);

    void DiscardInput();
}

/// <summary>ISO-TP auf einer CAN-Schnittstelle.</summary>
public sealed class IsoTpTransport(ICanAdapter can, string channel, IsoTpSettings settings) : IDiagnosticTransport
{
    public Task SendAsync(byte[] message, CancellationToken cancellationToken) => IsoTp.SendAsync(can, channel, settings, message, cancellationToken);

    public Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken) => IsoTp.ReceiveAsync(can, channel, settings, timeout, cancellationToken);

    public void DiscardInput() => can.DiscardInput(channel);

    public override string ToString() => settings.ToString();
}

/// <summary>Gängige Schlüsselberechnungen für den Sicherheitszugang (herstellerspezifische Algorithmen als Ausdruck je Byte oder CodeBlock).</summary>
public static class SecurityKey
{
    public enum Method
    {
        /// <summary>Schlüssel = Seed XOR Konstante (byteweise, Konstante wird wiederholt).</summary>
        Xor,
        /// <summary>Schlüssel = Seed + Konstante (byteweise, mod 256).</summary>
        Add,
        /// <summary>Schlüssel = bitweise invertierter Seed.</summary>
        Invert,
        /// <summary>Seed als großes Big-Endian-Wort: (Seed + Konstante) rotiert, dann XOR – einfache Prüfroutine für Simulation/Tests.</summary>
        AddRotateXor,
    }

    public static byte[] Compute(Method method, byte[] seed, byte[] parameter)
    {
        switch (method)
        {
            case Method.Invert:
                return seed.Select(b => (byte)~b).ToArray();
            case Method.Xor:
                if (parameter.Length == 0) throw new CommunicationException("Sicherheitszugang XOR: Konstante fehlt.");
                return seed.Select((b, i) => (byte)(b ^ parameter[i % parameter.Length])).ToArray();
            case Method.Add:
                if (parameter.Length == 0) throw new CommunicationException("Sicherheitszugang Addition: Konstante fehlt.");
                return seed.Select((b, i) => (byte)(b + parameter[i % parameter.Length])).ToArray();
            default:
                var key = new byte[seed.Length];
                var add = parameter.Length > 0 ? parameter[0] : (byte)0;
                for (var i = 0; i < seed.Length; i++)
                {
                    var v = (byte)(seed[i] + add);
                    v = (byte)((v << 1 | v >> 7) & 0xFF);
                    key[i] = (byte)(v ^ (parameter.Length > 1 ? parameter[1] : 0x5A));
                }
                return key;
        }
    }
}

// ====================================================================== J1939 (SAE J1939-21)

/// <summary>Empfangene J1939-Parametergruppe (auch über Transportprotokoll BAM bzw. RTS/CTS zusammengesetzt).</summary>
public sealed record J1939Message(uint Pgn, byte Source, byte? Destination, int Priority, byte[] Data, string Transport)
{
    public override string ToString()
        => $"PGN 0x{Pgn:X4} ({Pgn}) von 0x{Source:X2}{(Destination is { } d ? $" an 0x{d:X2}" : "")}{(Transport.Length > 0 ? $" [{Transport}]" : "")}: {string.Join(" ", Data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))}";
}

/// <summary>J1939: 29-Bit-ID aus Priorität, PGN, Ziel- und Quelladresse; Anforderung (PGN 59904); Transportprotokoll.</summary>
public static class J1939
{
    public const uint RequestPgn = 0xEA00;
    public const uint TpCmPgn = 0xEC00;
    public const uint TpDtPgn = 0xEB00;
    public const byte Global = 0xFF;

    private const byte CmRts = 16, CmCts = 17, CmEndOfMsgAck = 19, CmBam = 32, CmAbort = 255;

    /// <summary>PDU1 (PF &lt; 240): Zieladresse im PS-Feld, PGN-Byte 0 = 0.</summary>
    public static bool IsPdu1(uint pgn) => (pgn >> 8 & 0xFF) < 240;

    public static uint PgnOf(uint id)
    {
        var pgn = id >> 8 & 0x3FFFF;
        return IsPdu1(pgn) ? pgn & 0x3FF00 : pgn;
    }

    public static byte SourceOf(uint id) => (byte)id;

    public static byte? DestinationOf(uint id) => IsPdu1(id >> 8 & 0x3FFFF) ? (byte)(id >> 8) : null;

    public static int PriorityOf(uint id) => (int)(id >> 26 & 7);

    public static uint Id(uint pgn, byte source, byte destination = Global, int priority = 6)
    {
        if (pgn > 0x3FFFF) throw new ArgumentException($"PGN 0x{pgn:X} größer als 18 Bit.");
        var p = IsPdu1(pgn) ? pgn & 0x3FF00 | destination : pgn;
        return (uint)(priority & 7) << 26 | p << 8 | source;
    }

    /// <summary>Empfangsfilter für eine PGN (Priorität und bei PDU1 die Zieladresse egal), optional nur von einer Quelle.</summary>
    public static CanFilter Filter(uint pgn, byte? source)
    {
        var mask = IsPdu1(pgn) ? 0x03FF0000u : 0x03FFFF00u;
        if (source is not null) mask |= 0xFF;
        return new CanFilter(Id(pgn, source ?? 0, 0, 0), mask, true);
    }

    private static byte[] Pgn3(uint pgn) => [(byte)pgn, (byte)(pgn >> 8), (byte)(pgn >> 16)];

    /// <summary>Anforderung einer PGN (PGN 59904) an eine Adresse oder global.</summary>
    public static Task RequestAsync(ICanAdapter can, string channel, uint pgn, byte source, byte destination, CancellationToken token)
        => can.SendAsync(channel, new CanFrame(Id(RequestPgn, source, destination, 6), Pgn3(pgn), true), token);

    /// <summary>
    /// Parametergruppe senden; mehr als 8 Bytes über das Transportprotokoll: global als BAM (Abstand <paramref name="bamGap"/>,
    /// Norm 50–200 ms), an eine Adresse mit RTS/CTS und Abschlussbestätigung.
    /// </summary>
    public static async Task SendAsync(ICanAdapter can, string channel, uint pgn, byte[] data, byte source, byte destination, int priority,
        CancellationToken token, TimeSpan? bamGap = null)
    {
        if (data.Length <= 8)
        {
            await can.SendAsync(channel, new CanFrame(Id(pgn, source, destination, priority), data, true), token).ConfigureAwait(false);
            return;
        }
        if (data.Length > 1785) throw new CommunicationException("J1939-Transportprotokoll: höchstens 1785 Bytes.");
        var packets = (data.Length + 6) / 7;
        var size = new[] { (byte)data.Length, (byte)(data.Length >> 8) };
        byte[] Packet(int n) => [(byte)n, .. Enumerable.Range(0, 7).Select(i => (n - 1) * 7 + i < data.Length ? data[(n - 1) * 7 + i] : (byte)0xFF)];

        if (destination == Global)
        {
            await can.SendAsync(channel, new CanFrame(Id(TpCmPgn, source, Global, 7), [CmBam, .. size, (byte)packets, 0xFF, .. Pgn3(pgn)], true), token).ConfigureAwait(false);
            for (var n = 1; n <= packets; n++)
            {
                await Task.Delay(bamGap ?? TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
                await can.SendAsync(channel, new CanFrame(Id(TpDtPgn, source, Global, 7), Packet(n), true), token).ConfigureAwait(false);
            }
            return;
        }

        await can.SendAsync(channel, new CanFrame(Id(TpCmPgn, source, destination, 7), [CmRts, .. size, (byte)packets, 0xFF, .. Pgn3(pgn)], true), token).ConfigureAwait(false);
        var fromPeer = new CanFilter(Id(TpCmPgn, destination, source, 0), 0x03FFFFFF, true);
        var timeout = TimeSpan.FromMilliseconds(1250); // T3
        while (true)
        {
            CanFrame cm;
            try
            {
                cm = await can.ReceiveAsync(channel, fromPeer, timeout, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException)
            {
                throw new CommunicationTimeoutException($"J1939 RTS/CTS an 0x{destination:X2}: keine Antwort (T3).", []);
            }
            if (cm.Data.Length < 8) continue;
            switch (cm.Data[0])
            {
                case CmCts:
                    var count = cm.Data[1];
                    var next = cm.Data[2];
                    for (var n = next; n < next + count && n <= packets; n++)
                        await can.SendAsync(channel, new CanFrame(Id(TpDtPgn, source, destination, 7), Packet(n), true), token).ConfigureAwait(false);
                    break;
                case CmEndOfMsgAck:
                    return;
                case CmAbort:
                    throw new CommunicationException($"J1939: Gegenstelle 0x{destination:X2} bricht die Übertragung ab (Grund {cm.Data[1]}).");
            }
        }
    }

    /// <summary>
    /// Nächste Parametergruppe empfangen – einzeln oder über das Transportprotokoll (BAM; RTS/CTS, wenn an
    /// <paramref name="ownAddress"/> gerichtet: CTS und Abschlussbestätigung werden gesendet).
    /// </summary>
    public static async Task<J1939Message> ReceiveAsync(ICanAdapter can, string channel, uint pgn, byte? source, byte ownAddress, TimeSpan timeout,
        CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<CanFilter> filters = [Filter(pgn, source), Filter(TpCmPgn, source)];
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(1);
            CanFrame frame;
            try
            {
                frame = await can.ReceiveAnyAsync(channel, filters, remaining, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException ex)
            {
                throw new CommunicationTimeoutException($"J1939: PGN 0x{pgn:X4}{(source is { } s ? $" von 0x{s:X2}" : "")} nicht innerhalb von {timeout.TotalMilliseconds:0} ms.", ex.Partial);
            }
            var framePgn = PgnOf(frame.Id);
            if (framePgn == pgn)
                return new J1939Message(pgn, SourceOf(frame.Id), DestinationOf(frame.Id), PriorityOf(frame.Id), frame.Data, "");
            if (framePgn != TpCmPgn || frame.Data.Length < 8) continue;
            var announced = (uint)(frame.Data[5] | frame.Data[6] << 8 | frame.Data[7] << 16);
            if (announced != pgn) continue;
            var size = frame.Data[1] | frame.Data[2] << 8;
            var packets = frame.Data[3];
            var sender = SourceOf(frame.Id);
            var destination = DestinationOf(frame.Id);
            if (frame.Data[0] == CmBam)
                return new J1939Message(pgn, sender, null, PriorityOf(frame.Id),
                    await CollectAsync(can, channel, sender, null, size, packets, token).ConfigureAwait(false), "BAM");
            if (frame.Data[0] == CmRts && destination == ownAddress)
            {
                await can.SendAsync(channel, new CanFrame(Id(TpCmPgn, ownAddress, sender, 7), [CmCts, packets, 1, 0xFF, 0xFF, .. Pgn3(pgn)], true), token).ConfigureAwait(false);
                var data = await CollectAsync(can, channel, sender, ownAddress, size, packets, token).ConfigureAwait(false);
                await can.SendAsync(channel, new CanFrame(Id(TpCmPgn, ownAddress, sender, 7),
                    [CmEndOfMsgAck, (byte)size, (byte)(size >> 8), packets, 0xFF, .. Pgn3(pgn)], true), token).ConfigureAwait(false);
                return new J1939Message(pgn, sender, ownAddress, PriorityOf(frame.Id), data, "RTS/CTS");
            }
        }
    }

    private static async Task<byte[]> CollectAsync(ICanAdapter can, string channel, byte sender, byte? destination, int size, int packets, CancellationToken token)
    {
        var filter = destination is { } d ? new CanFilter(Id(TpDtPgn, sender, d, 0), 0x03FFFFFF, true) : Filter(TpDtPgn, sender);
        var buffer = new byte[packets * 7];
        var received = new bool[packets];
        for (var k = 0; k < packets; k++)
        {
            CanFrame dt;
            try
            {
                dt = await can.ReceiveAsync(channel, filter, TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false); // T1
            }
            catch (CommunicationTimeoutException)
            {
                throw new CommunicationTimeoutException($"J1939-Transportprotokoll: Paket {k + 1} von {packets} fehlt (T1).", buffer.Take(k * 7).ToArray());
            }
            if (dt.Data.Length < 8 || dt.Data[0] is 0 || dt.Data[0] > packets) throw new CommunicationException($"J1939-Transportprotokoll: ungültiges Datenpaket {dt.DataHex}.");
            dt.Data.AsSpan(1, 7).CopyTo(buffer.AsSpan((dt.Data[0] - 1) * 7));
            received[dt.Data[0] - 1] = true;
        }
        if (received.Any(r => !r)) throw new CommunicationException("J1939-Transportprotokoll: Pakete doppelt bzw. fehlend.");
        return buffer.AsSpan(0, Math.Min(size, buffer.Length)).ToArray();
    }

    public const uint AddressClaimPgn = 0xEE00;
    public const byte NullAddress = 0xFE;

    public static byte[] NameBytes(ulong name) => BitConverter.IsLittleEndian ? BitConverter.GetBytes(name) : [.. BitConverter.GetBytes(name).Reverse()];

    public static ulong NameOf(ReadOnlySpan<byte> data)
    {
        ulong v = 0;
        for (var i = Math.Min(8, data.Length) - 1; i >= 0; i--) v = v << 8 | data[i];
        return v;
    }

    /// <summary>
    /// Adresse beanspruchen (J1939-81): Address Claimed (PGN 60928) mit eigenem NAME senden und 250 ms auf Gegenansprüche warten.
    /// Ein Teilnehmer mit kleinerem NAME gewinnt – dann wird „Cannot Claim“ (Quelle 0xFE) gesendet und eine Ausnahme ausgelöst.
    /// </summary>
    public static async Task ClaimAddressAsync(ICanAdapter can, string channel, byte address, ulong name, CancellationToken token, TimeSpan? wait = null)
    {
        if (address >= NullAddress) throw new CommunicationException($"J1939: Adresse 0x{address:X2} ist nicht beanspruchbar (0x00–0xFD).");
        can.DiscardInput(channel);
        await can.SendAsync(channel, new CanFrame(Id(AddressClaimPgn, address, Global, 6), NameBytes(name), true), token).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.FromMilliseconds(250));
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            CanFrame claim;
            try
            {
                claim = await can.ReceiveAsync(channel, Filter(AddressClaimPgn, address), remaining, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException)
            {
                return; // kein Widerspruch
            }
            var other = NameOf(claim.Data);
            if (other == name) continue; // eigenes Echo
            if (other < name)
            {
                await can.SendAsync(channel, new CanFrame(Id(AddressClaimPgn, NullAddress, Global, 6), NameBytes(name), true), token).ConfigureAwait(false);
                throw new CommunicationException($"J1939: Adresse 0x{address:X2} gehört einem Teilnehmer mit höherer Priorität (NAME {other:X16}) – „Cannot Claim“ gesendet.");
            }
            // Gegenstelle mit größerem NAME muss weichen: Anspruch wiederholen
            await can.SendAsync(channel, new CanFrame(Id(AddressClaimPgn, address, Global, 6), NameBytes(name), true), token).ConfigureAwait(false);
        }
    }

    /// <summary>Alle Teilnehmer abfragen (Request auf PGN 60928, global) und ihre Ansprüche einsammeln.</summary>
    public static async Task<IReadOnlyList<(byte Address, ulong Name)>> RequestClaimsAsync(ICanAdapter can, string channel, byte source, TimeSpan collect,
        CancellationToken token)
    {
        can.DiscardInput(channel);
        await RequestAsync(can, channel, AddressClaimPgn, source, Global, token).ConfigureAwait(false);
        var result = new List<(byte, ulong)>();
        var deadline = DateTime.UtcNow + collect;
        for (var n = 0; n < 512; n++)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                var f = await can.ReceiveAsync(channel, Filter(AddressClaimPgn, null), remaining, token).ConfigureAwait(false);
                var entry = (SourceOf(f.Id), NameOf(f.Data));
                if (!result.Contains(entry)) result.Add(entry);
            }
            catch (CommunicationTimeoutException)
            {
                break;
            }
        }
        return [.. result.OrderBy(r => r.Item1)];
    }

    /// <summary>„0xFEF1“, „65265“ oder „FEF1h“ → PGN.</summary>
    public static uint ParsePgn(string text)
    {
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            : t.EndsWith('h') ? uint.TryParse(t[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)
            : uint.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (!ok || v > 0x3FFFF) throw new FormatException($"PGN „{text}“ ungültig (z. B. 0xFEF1 oder 65265).");
        return v;
    }
}

// ====================================================================== LIN (ISO 17987 / LIN 2.x)

/// <summary>LIN: geschützte ID (Paritätsbits) und Prüfsumme.</summary>
public static class Lin
{
    public static byte ProtectedId(byte id)
    {
        if (id > 0x3F) throw new ArgumentException($"LIN-ID {id} größer als 63.");
        int Bit(int n) => id >> n & 1;
        var p0 = Bit(0) ^ Bit(1) ^ Bit(2) ^ Bit(4);
        var p1 = 1 ^ (Bit(1) ^ Bit(3) ^ Bit(4) ^ Bit(5));
        return (byte)(id | p0 << 6 | p1 << 7);
    }

    /// <summary>Diagnoseframes (0x3C, 0x3D) verwenden immer die klassische Prüfsumme.</summary>
    public static LinChecksum Effective(byte id, LinChecksum checksum) => id is 0x3C or 0x3D ? LinChecksum.Classic : checksum;

    public static byte Checksum(byte id, ReadOnlySpan<byte> data, LinChecksum checksum)
    {
        var sum = Effective(id, checksum) == LinChecksum.Enhanced ? ProtectedId(id) : 0;
        foreach (var b in data)
        {
            sum += b;
            if (sum > 0xFF) sum -= 0xFF;
        }
        return (byte)~sum;
    }

    public static string? Validate(byte id, int length)
        => id > 0x3F ? $"LIN-ID 0x{id:X2} größer als 0x3F." : length is < 1 or > 8 ? $"LIN-Frame: Länge {length} (1–8 Bytes)." : null;
}

// ====================================================================== DoIP (ISO 13400-2)

/// <summary>DoIP-Adressen: logische Adresse des Testers (Quelle) und des Steuergeräts (Ziel).</summary>
public sealed record DoIpSettings(ushort TesterAddress = 0x0E00, ushort EcuAddress = 0x1000, byte ActivationType = 0x00, byte Version = 0x02)
{
    /// <summary>Vor der ersten Anfrage Routing aktivieren (Routing Activation Request 0x0005).</summary>
    public bool ActivateRouting { get; init; } = true;

    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public override string ToString() => $"DoIP 0x{TesterAddress:X4}→0x{EcuAddress:X4}";
}

/// <summary>
/// UDS über DoIP auf einer TCP-Verbindung (Gerät „TCP-Client“, Port 13400): generischer Kopf (Version, Typ, Länge),
/// Routing-Aktivierung, Diagnosenachricht 0x8001 mit Quittung 0x8002/0x8003, Alive-Check wird beantwortet.
/// </summary>
public sealed class DoIpTransport(IByteStreamAdapter stream, string channel, DoIpSettings settings) : IDiagnosticTransport
{
    public const ushort RoutingActivationRequest = 0x0005, RoutingActivationResponse = 0x0006, AliveCheckRequest = 0x0007, AliveCheckResponse = 0x0008,
        DiagnosticMessage = 0x8001, DiagnosticAck = 0x8002, DiagnosticNack = 0x8003, GenericNack = 0x0000;

    private bool _activated;

    public static byte[] Frame(byte version, ushort type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = version;
        frame[1] = (byte)~version;
        frame[2] = (byte)(type >> 8);
        frame[3] = (byte)type;
        frame[4] = (byte)(payload.Length >> 24);
        frame[5] = (byte)(payload.Length >> 16);
        frame[6] = (byte)(payload.Length >> 8);
        frame[7] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    /// <summary>Eine DoIP-Nachricht lesen (Kopf, dann Nutzdaten).</summary>
    public static async Task<(ushort Type, byte[] Payload)> ReadAsync(IByteStreamAdapter stream, string channel, TimeSpan timeout, CancellationToken token)
    {
        var header = await stream.ReceiveAsync(channel, new ReceiveRule(null, 8, timeout), token).ConfigureAwait(false);
        if ((byte)~header[0] != header[1]) throw new CommunicationException($"DoIP: ungültiger Kopf {Convert.ToHexString(header)} (Version/Inverse).");
        var type = (ushort)(header[2] << 8 | header[3]);
        var length = header[4] << 24 | header[5] << 16 | header[6] << 8 | header[7];
        if (length is < 0 or > 1 << 22) throw new CommunicationException($"DoIP: Nutzlast {length} Bytes zu groß.");
        var payload = length == 0 ? [] : await stream.ReceiveAsync(channel, new ReceiveRule(null, length, timeout, MaxBytes: length), token).ConfigureAwait(false);
        return (type, payload);
    }

    private static byte[] U16(ushort v) => [(byte)(v >> 8), (byte)v];

    public static string DescribeActivation(byte code) => code switch
    {
        0x00 => "unbekannte Quelladresse",
        0x01 => "alle Sockets belegt",
        0x02 => "Quelladresse an anderem Socket aktiv",
        0x03 => "Quelladresse bereits registriert",
        0x04 => "Authentifizierung fehlt",
        0x05 => "Bestätigung abgelehnt",
        0x06 => "Aktivierungsart nicht unterstützt",
        0x07 => "TLS-Verbindung erforderlich",
        0x10 => "erfolgreich",
        0x11 => "erfolgreich, Bestätigung erforderlich",
        _ => "herstellerspezifisch",
    };

    public static string DescribeNack(byte code) => code switch
    {
        0x02 => "ungültige Quelladresse",
        0x03 => "unbekannte Zieladresse",
        0x04 => "Nachricht zu groß",
        0x05 => "kein Speicher",
        0x06 => "Ziel nicht erreichbar",
        0x07 => "unbekanntes Netz",
        0x08 => "Transportprotokollfehler",
        _ => "herstellerspezifisch",
    };

    private async Task ActivateAsync(CancellationToken token)
    {
        await stream.SendAsync(channel, Frame(settings.Version, RoutingActivationRequest,
            [.. U16(settings.TesterAddress), settings.ActivationType, 0, 0, 0, 0]), token).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + settings.ControlTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new CommunicationTimeoutException($"{settings}: keine Antwort auf die Routing-Aktivierung.", []);
            var (type, payload) = await ReadAsync(stream, channel, remaining, token).ConfigureAwait(false);
            if (type == AliveCheckRequest) { await AnswerAliveAsync(token).ConfigureAwait(false); continue; }
            if (type == GenericNack) throw new CommunicationException($"{settings}: allgemeine Ablehnung (Code 0x{(payload.Length > 0 ? payload[0] : 0):X2}).");
            if (type != RoutingActivationResponse) continue;
            if (payload.Length < 5) throw new CommunicationException($"{settings}: Routing-Antwort zu kurz.");
            var code = payload[4];
            if (code is not (0x10 or 0x11)) throw new CommunicationException($"{settings}: Routing abgelehnt – 0x{code:X2} {DescribeActivation(code)}.");
            _activated = true;
            return;
        }
    }

    private Task AnswerAliveAsync(CancellationToken token)
        => stream.SendAsync(channel, Frame(settings.Version, AliveCheckResponse, U16(settings.TesterAddress)), token);

    public async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        if (settings.ActivateRouting && !_activated) await ActivateAsync(cancellationToken).ConfigureAwait(false);
        await stream.SendAsync(channel, Frame(settings.Version, DiagnosticMessage, [.. U16(settings.TesterAddress), .. U16(settings.EcuAddress), .. message]),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new CommunicationTimeoutException($"{settings}: keine Antwort innerhalb von {timeout.TotalMilliseconds:0} ms.", []);
            (ushort Type, byte[] Payload) m;
            try
            {
                m = await ReadAsync(stream, channel, remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException ex)
            {
                throw new CommunicationTimeoutException($"{settings}: keine Antwort innerhalb von {timeout.TotalMilliseconds:0} ms.", ex.Partial);
            }
            switch (m.Type)
            {
                case AliveCheckRequest:
                    await AnswerAliveAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                case DiagnosticAck:
                    continue;
                case DiagnosticNack:
                    var code = m.Payload.Length > 4 ? m.Payload[4] : (byte)0;
                    throw new CommunicationException($"{settings}: Diagnosenachricht abgelehnt – 0x{code:X2} {DescribeNack(code)}.");
                case GenericNack:
                    throw new CommunicationException($"{settings}: allgemeine Ablehnung (Code 0x{(m.Payload.Length > 0 ? m.Payload[0] : 0):X2}).");
                case DiagnosticMessage when m.Payload.Length > 4:
                    var source = (ushort)(m.Payload[0] << 8 | m.Payload[1]);
                    if (source != settings.EcuAddress) continue; // anderes Steuergerät
                    return m.Payload[4..];
                default:
                    continue;
            }
        }
    }

    public void DiscardInput() => stream.DiscardInput(channel);

    public override string ToString() => settings.ToString();
}

/// <summary>Antwort eines Steuergeräts/Gateways auf die Fahrzeugsuche (Vehicle Announcement / Identification Response).</summary>
public sealed record DoIpVehicle(string Vin, ushort LogicalAddress, byte[] Eid, byte[] Gid, byte FurtherAction)
{
    public string EidText => Convert.ToHexString(Eid);
    public override string ToString() => $"VIN {Vin}, Adresse 0x{LogicalAddress:X4}, EID {EidText}";
}

/// <summary>Datagrammkanal für die DoIP-Fahrzeugsuche (UDP 13400); getrennt vom Transport, damit er testbar bleibt.</summary>
public interface IDatagramChannel
{
    Task SendAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>Nächstes empfangenes Datagramm; <see cref="CommunicationTimeoutException"/> nach dem Zeitlimit.</summary>
    Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>DoIP-Fahrzeugsuche (ISO 13400-2): Vehicle Identification Request (0x0001) senden, Announcements/Responses (0x0004) sammeln.</summary>
public static class DoIpDiscovery
{
    public const ushort VehicleIdentificationRequest = 0x0001;
    public const ushort VehicleAnnouncement = 0x0004;

    public static byte[] Request(byte version = 0x02) => DoIpTransport.Frame(version, VehicleIdentificationRequest, []);

    public static DoIpVehicle? Parse(byte[] frame)
    {
        if (frame.Length < 8 || (byte)~frame[0] != frame[1]) return null;
        var type = (ushort)(frame[2] << 8 | frame[3]);
        if (type != VehicleAnnouncement) return null;
        var p = frame.AsSpan(8);
        if (p.Length < 32) return null;
        var vin = System.Text.Encoding.ASCII.GetString(p[..17]).TrimEnd('\0', ' ');
        var address = (ushort)(p[17] << 8 | p[18]);
        return new DoIpVehicle(vin, address, p.Slice(19, 6).ToArray(), p.Slice(25, 6).ToArray(), p[31]);
    }

    /// <summary>Anfrage senden und bis zum Zeitlimit alle antwortenden Fahrzeuge sammeln (je logische Adresse einmal).</summary>
    public static async Task<IReadOnlyList<DoIpVehicle>> DiscoverAsync(IDatagramChannel channel, TimeSpan timeout, byte version, CancellationToken token)
    {
        await channel.SendAsync(Request(version), token).ConfigureAwait(false);
        var found = new List<DoIpVehicle>();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            byte[] datagram;
            try
            {
                datagram = await channel.ReceiveAsync(remaining, token).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException)
            {
                break;
            }
            if (Parse(datagram) is { } v && found.All(f => f.LogicalAddress != v.LogicalAddress)) found.Add(v);
        }
        return found;
    }
}

/// <summary>UDP-Datagrammkanal für die DoIP-Fahrzeugsuche (Broadcast oder gezielt, Port 13400).</summary>
public sealed class UdpDatagramChannel : IDatagramChannel, IDisposable
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _target;

    public UdpDatagramChannel(string host, int port)
    {
        _client = new UdpClient { EnableBroadcast = true };
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var broadcast = host is "255.255.255.255" or "" || host.EndsWith(".255", StringComparison.Ordinal);
        var address = broadcast && !IPAddress.TryParse(host, out _) ? IPAddress.Broadcast
            : IPAddress.TryParse(host, out var ip) ? ip : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _target = new IPEndPoint(address, port);
    }

    /// <summary>Lokaler Port (für Tests: an welchen Port geantwortet werden muss).</summary>
    public int LocalPort => ((IPEndPoint)_client.Client.LocalEndPoint!).Port;

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
        => await _client.SendAsync(data, _target, cancellationToken).ConfigureAwait(false);

    public async Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var result = await _client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CommunicationTimeoutException("DoIP-Fahrzeugsuche: keine weitere Antwort.", []);
        }
    }

    public void Dispose() => _client.Dispose();
}

// ====================================================================== LIN-Transportprotokoll / LIN-Diagnose (ISO 17987-2)

/// <summary>
/// UDS über das LIN-Transportprotokoll: Master-Request-Frame 0x3C (Anfrage), Slave-Response-Frame 0x3D (Antwort), beide mit
/// NAD als erstem Byte und PCI (Einzel-/Erst-/Folgerahmen). Klassische Prüfsumme. Der Master pollt die Antwort über 0x3D.
/// </summary>
public sealed class LinDiagnosticTransport(ILinAdapter lin, string channel, byte nad, TimeSpan? pollInterval = null) : IDiagnosticTransport
{
    public const byte MasterRequestFrame = 0x3C, SlaveResponseFrame = 0x3D;
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromMilliseconds(10);

    public async Task SendAsync(byte[] message, CancellationToken cancellationToken)
    {
        if (message.Length <= 6)
        {
            await lin.SendAsync(channel, MasterRequestFrame, Pad([nad, (byte)message.Length, .. message]), LinChecksum.Classic, cancellationToken).ConfigureAwait(false);
            return;
        }
        await lin.SendAsync(channel, MasterRequestFrame, [nad, (byte)(0x10 | message.Length >> 8), (byte)message.Length, .. message.Take(5)], LinChecksum.Classic, cancellationToken)
            .ConfigureAwait(false);
        var offset = 5;
        var sequence = 1;
        while (offset < message.Length)
        {
            var chunk = message.Skip(offset).Take(6).ToArray();
            await lin.SendAsync(channel, MasterRequestFrame, Pad([nad, (byte)(0x20 | sequence++ & 0x0F), .. chunk]), LinChecksum.Classic, cancellationToken).ConfigureAwait(false);
            offset += 6;
        }
    }

    private static byte[] Pad(IEnumerable<byte> data)
    {
        var list = data.ToList();
        while (list.Count < 8) list.Add(0xFF);
        return [.. list];
    }

    public async Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        List<byte>? buffer = null;
        var total = 0;
        var expected = 1;
        while (true)
        {
            if (DateTime.UtcNow >= deadline) throw new CommunicationTimeoutException($"LIN-Diagnose (NAD 0x{nad:X2}): keine vollständige Antwort innerhalb von {timeout.TotalMilliseconds:0} ms.", buffer?.ToArray() ?? []);
            byte[] frame;
            try
            {
                frame = await lin.RequestAsync(channel, SlaveResponseFrame, 8, LinChecksum.Classic, _poll, cancellationToken).ConfigureAwait(false);
            }
            catch (CommunicationTimeoutException)
            {
                await Task.Delay(_poll, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (frame.Length < 2 || frame[0] != nad) continue;
            var pci = frame[1];
            if (buffer is null)
            {
                if (ReadInitialFrame(frame, ref buffer, ref total) is { } single) return single;
            }
            else if (pci >> 4 == 2 && (pci & 0x0F) == (expected & 0x0F))
            {
                buffer.AddRange(frame.Skip(2).Take(total - buffer.Count));
                expected++;
            }
            if (buffer is not null && buffer.Count >= total) return [.. buffer];
        }
    }

    private static byte[]? ReadInitialFrame(byte[] frame, ref List<byte>? buffer, ref int total)
    {
        var pci = frame[1];
        switch (pci >> 4)
        {
            case 0:
                var length = pci & 0x0F;
                if (length is < 1 or > 6 || 2 + length > frame.Length) return null;
                return frame.AsSpan(2, length).ToArray();
            case 1:
                if (frame.Length < 3) return null;
                total = (pci & 0x0F) << 8 | frame[2];
                if (total <= 6) return null;
                buffer = [.. frame.Skip(3).Take(total)];
                break;
            default:
                return null;
        }
        return null;
    }

    public void DiscardInput() { }

    public override string ToString() => $"LIN-Diagnose NAD 0x{nad:X2}";
}
