/**
 * @file
 * @brief Liest und schreibt das Chunk-basierte Ergebnisformat; beschädigte Teilinhalte werden getrennt diagnostiziert.
 */

using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Testumgebung.Contracts;

namespace Testumgebung.Persistence.Results;

/// <summary>Ergebnisdatei nicht lesbar (Kopf beschädigt, falsches Format, neuere Version).</summary>
public sealed class ResultFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Ergebnis des Lesens: Inhalt plus gefundene Integritätsprobleme (beschädigte Chunks werden übersprungen).</summary>
public sealed record ResultFileContent(ResultRun Run, IReadOnlyList<string> Problems, int FormatVersion, long FileSize)
{
    public bool IsIntact => Problems.Count == 0;
}

/// <summary>
/// Binäre Ergebnisdatei <c>.tres</c>, Format v1 (siehe docs/adr/0004):
/// <code>
/// Datei   = "TRES" u16 Formatversion u16 reserviert  Chunk*  Trailer
/// Chunk   = Typ[4] u8 Codec u32 gespeicherte Länge u32 Rohlänge u32 CRC32(gespeichert)  Daten
/// Trailer = u64 Position des INDX-Chunks "TEND"
/// </code>
/// Alle Zahlen little-endian. Chunks: HEAD (Kopf), STRU (Ablaufstruktur), EVNT (Ereignisse, mit Pfad-/Einheitentabelle),
/// WAVE (Kurvenblock, Samples als float64 LE), ATTA (Anhang), INDX (Positionen, Längen, Prüfsummen).
/// Metadaten sind MessagePack; Codec 0 = unkomprimiert, 1 = Deflate. Unbekannte Chunktypen werden beim Lesen übersprungen.
/// </summary>
public static class ResultFile
{
    public const string Extension = ".tres";
    public const ushort FormatVersion = 1;
    public static ReadOnlySpan<byte> Magic => "TRES"u8;
    public static ReadOnlySpan<byte> TrailerMagic => "TEND"u8;

    /// <summary>Samples je Kurvenblock (große Kurven werden blockweise geschrieben und gelesen).</summary>
    public const int SamplesPerChunk = 65536;

    /// <summary>Ereignisse je EVNT-Chunk.</summary>
    public const int EventsPerChunk = 2000;

    /// <summary>Typ, Codec, gespeicherte Länge, Rohlänge, CRC.</summary>
    public const int ChunkHeaderSize = 4 + 1 + 4 + 4 + 4;
    private const byte CodecNone = 0;
    private const byte CodecDeflate = 1;

    // ------------------------------------------------------------------ Schreiben

    public static void Write(Stream stream, ResultRun run)
    {
        var index = new List<(string Type, long Offset, int Length, uint Crc)>();
        Span<byte> head = stackalloc byte[8];
        Magic.CopyTo(head);
        BinaryPrimitives.WriteUInt16LittleEndian(head[4..], FormatVersion);
        stream.Write(head);
        var pos = 8L;

        void Chunk(string type, byte[] raw, bool compress = true)
        {
            var (codec, stored) = (CodecNone, raw);
            if (compress && raw.Length > 64)
            {
                var deflated = Deflate(raw);
                if (deflated.Length < raw.Length) (codec, stored) = (CodecDeflate, deflated);
            }
            var crc = Crc32.Compute(stored);
            var bytes = new byte[ChunkHeaderSize];
            Encoding.ASCII.GetBytes(type, bytes);
            bytes[4] = codec;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), stored.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9), raw.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(13), crc);
            stream.Write(bytes);
            stream.Write(stored);
            index.Add((type, pos, stored.Length, crc));
            pos += bytes.Length + stored.Length;
        }

        Chunk("HEAD", EncodeHeader(run));
        Chunk("STRU", EncodeStructure(run.Structure));
        var nodeIndex = run.Structure.Select((resultNode, index2) => (resultNode.Id, i: index2)).ToDictionary(entry => entry.Id, entry => entry.i, StringComparer.Ordinal);
        for (var index2 = 0; index2 < run.Events.Count || index2 == 0; index2 += EventsPerChunk)
            Chunk("EVNT", EncodeEvents(run.Events.Skip(index2).Take(EventsPerChunk).ToList(), nodeIndex));
        foreach (var waveform in run.Waveforms)
        {
            var parts = Math.Max(1, (waveform.Data.Count + SamplesPerChunk - 1) / SamplesPerChunk);
            for (var index3 = 0; index3 < parts; index3++) Chunk("WAVE", EncodeWavePart(waveform, index3, parts));
        }
        foreach (var resultAttachment in run.Attachments) Chunk("ATTA", EncodeAttachment(resultAttachment));

        var indexOffset = pos;
        var iw = new MsgPackWriter().Array(index.Count);
        foreach (var (type, offset, length, crc) in index) iw.Array(4).Str(type).Int(offset).Int(length).Int(crc);
        Chunk("INDX", iw.ToArray(), compress: false);

        Span<byte> trailer = stackalloc byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(trailer, indexOffset);
        TrailerMagic.CopyTo(trailer[8..]);
        stream.Write(trailer);
    }

    public static byte[] ToBytes(ResultRun run)
    {
        using var ms = new MemoryStream();
        Write(ms, run);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var deflateStream = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) deflateStream.Write(raw);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] stored, int rawLength)
    {
        var raw = new byte[rawLength];
        using var deflateStream = new DeflateStream(new MemoryStream(stored), CompressionMode.Decompress);
        deflateStream.ReadExactly(raw);
        return raw;
    }

    private static byte[] EncodeHeader(ResultRun run)
    {
        var msgPackWriter = new MsgPackWriter().Map(17);
        msgPackWriter.Key("runId").Str(run.RunId);
        msgPackWriter.Key("stationRunId").Str(run.StationRunId);
        msgPackWriter.Key("station").Str(run.Station);
        msgPackWriter.Key("dutId").Str(run.DutId);
        msgPackWriter.Key("order").Str(run.Order);
        msgPackWriter.Key("testName").Str(run.TestName);
        msgPackWriter.Key("testVersion").Str(run.TestVersion);
        msgPackWriter.Key("started").Int(run.Started.UtcTicks);
        msgPackWriter.Key("ended").Int(run.Ended.UtcTicks);
        msgPackWriter.Key("utcOffsetMin").Int((long)run.Started.Offset.TotalMinutes);
        msgPackWriter.Key("rating").Str(RatingText(run.Rating));
        msgPackWriter.Key("werror").Bool(run.OperatorAbort);
        msgPackWriter.Key("status").Str(run.Status.ToString());
        msgPackWriter.Key("development").Bool(run.IsDevelopmentRun);
        msgPackWriter.Key("filteredFor").Str(run.FilteredFor);
        msgPackWriter.Key("application").Str(run.Application);
        msgPackWriter.Key("schema").Str("testumgebung.ergebnis/1");
        return msgPackWriter.ToArray();
    }

    private static byte[] EncodeStructure(List<ResultNode> nodes)
    {
        var msgPackWriter = new MsgPackWriter().Array(nodes.Count);
        foreach (var resultNode in nodes) msgPackWriter.Array(6).Str(resultNode.Id).Str(resultNode.ParentId).Str(resultNode.Name).Str(resultNode.Kind).Str(resultNode.BlockType).Str(resultNode.Description);
        return msgPackWriter.ToArray();
    }

    private static byte[] EncodeEvents(List<ResultEvent> events, Dictionary<string, int> nodeIndex)
    {
        // Schema-Referenzen: Pfade und Einheiten einmalig je Chunk, Ereignisse verweisen per Index.
        var strings = new List<string>();
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        int S(string? text)
        {
            if (text is null) return -1;
            if (!lookup.TryGetValue(text, out var index))
            {
                index = strings.Count;
                strings.Add(text);
                lookup[text] = index;
            }
            return index;
        }

        var body = new MsgPackWriter().Array(events.Count);
        foreach (var resultEvent in events)
        {
            body.Array(15);
            body.Int(resultEvent.Sequence).Int((int)resultEvent.Kind);
            body.Int(resultEvent.NodeId is not null && nodeIndex.TryGetValue(resultEvent.NodeId, out var ni) ? ni : -1);
            body.Str(resultEvent.NodeId is not null && !nodeIndex.ContainsKey(resultEvent.NodeId) ? resultEvent.NodeId : null);
            body.Int(S(resultEvent.Path)).Int(resultEvent.Iteration).Int(resultEvent.Time.UtcTicks);
            body.Int(resultEvent.Rating is { } rating ? (int)rating : -1);
            WriteValue(body, resultEvent.Value);
            body.Int(S(resultEvent.Unit)).OptDouble(resultEvent.LowLimit).OptDouble(resultEvent.HighLimit).Str(resultEvent.Message).Bool(resultEvent.Cancelled);
            if (resultEvent.WaveformIds is { } ids)
            {
                body.Array(ids.Count + 1).Str(resultEvent.AttachmentId);
                foreach (var id in ids) body.Str(id);
            }
            else body.Array(1).Str(resultEvent.AttachmentId);
        }
        var msgPackWriter = new MsgPackWriter().Map(2);
        msgPackWriter.Key("strings").Array(strings.Count);
        foreach (var text2 in strings) msgPackWriter.Str(text2);
        msgPackWriter.Key("events");
        var bytes = msgPackWriter.ToArray();
        return [.. bytes, .. body.ToArray()];
    }

    /// <summary>Typisierter Wert als [Typ, Wert]: n, b, i, d, s, t (DateTime UTC-Ticks).</summary>
    private static void WriteValue(MsgPackWriter msgPackWriter, object? value)
    {
        switch (value)
        {
            case null: msgPackWriter.Nil(); break;
            case bool flag: msgPackWriter.Array(2).Str("b").Bool(flag); break;
            case long integerValue: msgPackWriter.Array(2).Str("i").Int(integerValue); break;
            case double number: msgPackWriter.Array(2).Str("d").Double(number); break;
            case string text: msgPackWriter.Array(2).Str("s").Str(text); break;
            case DateTime dt: msgPackWriter.Array(2).Str("t").Int(dt.ToUniversalTime().Ticks); break;
            default: msgPackWriter.Array(2).Str("s").Str(value.ToString()); break;
        }
    }

    private static object? ReadValue(object? value)
    {
        if (value is not object?[] { Length: 2 } items || items[0] is not string text) return null;
        return text switch
        {
            "b" => items[1] is true,
            "i" => items[1] is long integerValue ? integerValue : null,
            "d" => items[1] switch { double number => number, long integerValue2 => (double)integerValue2, _ => null },
            "s" => items[1] as string,
            "t" => items[1] is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : null,
            _ => null,
        };
    }

    private static byte[] EncodeWavePart(ResultWaveform rw, int part, int parts)
    {
        var data = rw.Data;
        var offset = part * SamplesPerChunk;
        var count = Math.Min(SamplesPerChunk, data.Count - offset);
        var msgPackWriter = new MsgPackWriter().Map(16);
        msgPackWriter.Key("id").Str(rw.Id);
        msgPackWriter.Key("event").Int(rw.EventSequence);
        msgPackWriter.Key("node").Str(rw.NodeId);
        msgPackWriter.Key("channel").Str(data.Channel);
        msgPackWriter.Key("unit").Str(data.Unit);
        msgPackWriter.Key("rate").Double(data.SampleRate);
        msgPackWriter.Key("start").Int(data.Start.UtcTicks);
        msgPackWriter.Key("quality").Str(data.Quality);
        msgPackWriter.Key("sync").Str(data.SyncGroup);
        msgPackWriter.Key("part").Int(part);
        msgPackWriter.Key("parts").Int(parts);
        msgPackWriter.Key("offset").Int(offset);
        msgPackWriter.Key("total").Int(data.Count);
        msgPackWriter.Key("sampleType").Str("f64le");
        msgPackWriter.Key("samples").Bin(ToBytes(data.Values.ReadBlock(offset, Math.Max(0, count))));
        msgPackWriter.Key("times");
        if (data.TimeValues is { } times) msgPackWriter.Bin(ToBytes(times.ReadBlock(offset, Math.Max(0, count))));
        else msgPackWriter.Nil();
        return msgPackWriter.ToArray();
    }

    private static byte[] ToBytes(ReadOnlySpan<double> values)
    {
        var bytes = new byte[values.Length * 8];
        if (BitConverter.IsLittleEndian) MemoryMarshal.AsBytes(values).CopyTo(bytes);
        else for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(index * 8), values[index]);
        return bytes;
    }

    private static double[] ToDoubles(byte[] bytes)
    {
        if (bytes.Length % 8 != 0) throw new FormatException("Samplearray hat keine ganze Anzahl float64-Werte.");
        var values = new double[bytes.Length / 8];
        for (var index = 0; index < values.Length; index++) values[index] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(index * 8));
        return values;
    }

    private static byte[] EncodeAttachment(ResultAttachment resultAttachment)
        => new MsgPackWriter().Map(4).Key("id").Str(resultAttachment.Id).Key("name").Str(resultAttachment.Name).Key("media").Str(resultAttachment.MediaType).Key("data").Bin(resultAttachment.Data).ToArray();

    internal static string RatingText(ResultRating r) => r switch
    {
        ResultRating.Pass => "PASS",
        ResultRating.Fail => "FAIL",
        ResultRating.Error => "ERROR",
        _ => "NRESULT",
    };

    private static ResultRating ParseRating(string? text) => text switch
    {
        "PASS" => ResultRating.Pass,
        "FAIL" => ResultRating.Fail,
        "ERROR" => ResultRating.Error,
        _ => ResultRating.NResult,
    };

    // ------------------------------------------------------------------ Lesen

    public static ResultFileContent Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[8];
            if (stream.Read(header) != 8 || !header.AsSpan(0, 4).SequenceEqual(Magic))
                throw new ResultFileException("Keine Ergebnisdatei (Kennung 'TRES' fehlt).");
            var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            if (version > FormatVersion) throw new ResultFileException($"Formatversion {version} wird nicht unterstützt.");
            var problems = new List<string>();
            return DecodeChunks(ReadStreamChunks(stream, problems), problems, version, stream.Length);
        }
        catch (IOException ex)
        {
            throw new ResultFileException($"Datei '{path}' nicht lesbar: {ex.Message}", ex);
        }
    }

    private sealed class WaveParts : IDisposable
    {
        public Dictionary<string, object?> First { get; }
        public SampleBufferWriter Samples { get; }
        public SampleBufferWriter? Times { get; }
        private readonly int _total;
        private readonly int _expected;
        private int _offset;
        private int _parts;
        private bool _invalid;
        public bool IsComplete => !_invalid && _offset == _total && _parts == _expected;
        public WaveParts(Dictionary<string, object?> first)
        {
            First = first;
            _total = checked((int)(first.L("total") ?? 0));
            _expected = checked((int)(first.L("parts") ?? 1));
            if (_total < 0 || _total > 50_000_000 || _expected < 1) throw new FormatException("Ungültige Kurvenlänge.");
            Samples = new SampleBufferWriter(_total);
            if (first.Bin("times") is not null) Times = new SampleBufferWriter(_total);
        }
        public void Append(Dictionary<string, object?> part)
        {
            var values = ToDoubles(part.Bin("samples") ?? []);
            if (part.L("part") != _parts || part.L("offset") != _offset || part.L("total") != _total
                || part.L("parts") != _expected || values.Length > _total - _offset)
            { _invalid = true; return; }
            var times = part.Bin("times");
            if ((Times is null) != (times is null) || (times is not null && times.Length != values.Length * 8))
            { _invalid = true; return; }
            Samples.Append(values);
            if (times is not null) Times!.Append(ToDoubles(times));
            _offset += values.Length;
            _parts++;
        }
        public void Dispose() { Samples.Dispose(); Times?.Dispose(); }
    }

    /// <summary>Prüft und dekodiert jeweils nur einen Chunk; kein vollständiges Dateiabbild im RAM.</summary>
    private static byte[]? DecodeStreamChunk(byte[] header, byte[] body, int size, int rawSize, long position, List<string> problems)
    {
        byte[]? raw = null;
        try
        {
            raw = header[4] switch
            {
                CodecNone when size == rawSize => body,
                CodecDeflate => Inflate(body, rawSize),
                _ => throw new InvalidDataException("Unbekannter Codec oder ungültige Rohlänge."),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        { problems.Add($"Chunk @ {position}: {ex.Message}"); }
        return raw;
    }

    private static void ValidateStreamIndex(byte[] raw, List<(string Type, long Offset, int Length, uint Crc)> seen, List<string> problems)
    {
        try
        {
            var entries = (object?[])MsgPackReader.Parse(raw)!;
            if (entries.Length != seen.Count) throw new FormatException("Anzahl der Indexeinträge stimmt nicht.");
            for (var index = 0; index < entries.Length; index++)
            {
                var item = seen[index];
                ValidateIndexEntry(entries[index], item);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        { problems.Add("Index beschädigt: " + ex.Message); }
    }

    private static (long End, long? IndexOffset) ReadStreamTrailer(Stream stream, long end, List<string> problems)
    {
        long? indexOffset = null;
        if (end >= 20)
        {
            stream.Position = end - 4;
            var trailer = new byte[4];
            stream.ReadExactly(trailer);
            if (trailer.AsSpan().SequenceEqual(TrailerMagic))
            {
                end -= 12;
                stream.Position = end;
                var pointer = new byte[8];
                stream.ReadExactly(pointer);
                indexOffset = BinaryPrimitives.ReadInt64LittleEndian(pointer);
            }
            else problems.Add("Dateiende/Index fehlt – Datei unvollständig, sequenziell gelesen.");
        }
        return (end, indexOffset);
    }

    private static bool ValidChunkLength(int size, int rawSize, long remaining)
        => size >= 0 && rawSize >= 0 && size <= 128 * 1024 * 1024 && rawSize <= 128 * 1024 * 1024 && size <= remaining;

    private static IEnumerable<(string Type, byte[] Raw)> ReadStreamChunks(Stream stream, List<string> problems)
    {
        var end = stream.Length;
        long? indexOffset = null;
        var indexSeen = false;
        var seen = new List<(string Type, long Offset, int Length, uint Crc)>();
        (end, indexOffset) = ReadStreamTrailer(stream, end, problems);
        stream.Position = 8;
        while (stream.Position + ChunkHeaderSize <= end)
        {
            var position = stream.Position;
            var header = new byte[ChunkHeaderSize];
            stream.ReadExactly(header);
            var type = Encoding.ASCII.GetString(header, 0, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5));
            var rawSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(9));
            if (!ValidChunkLength(size, rawSize, end - stream.Position))
            { problems.Add($"Chunk @ {position}: ungültige Länge oder abgeschnitten."); yield break; }
            var body = new byte[size];
            stream.ReadExactly(body);
            if (Crc32.Compute(body) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(13)))
            { problems.Add($"Chunk @ {position}: Prüfsumme falsch (beschädigt)."); continue; }
            var raw = DecodeStreamChunk(header, body, size, rawSize, position, problems);
            if (type == "INDX" && raw is not null)
            {
                indexSeen = true;
                if (position != indexOffset || stream.Position != end) problems.Add("Indexposition stimmt nicht mit dem Dateiende überein.");
                ValidateStreamIndex(raw, seen, problems);
            }
            else
            {
                seen.Add((type, position, size, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(13))));
                if (raw is not null) yield return (type, raw);
            }
        }
        if (stream.Position != end) problems.Add("Abgeschnittener Chunk am Dateiende.");
        if (indexOffset is not null && !indexSeen) problems.Add("Index fehlt oder ist beschädigt.");
    }

    /// <summary>
    /// Liest eine Ergebnisdatei. Über den Index werden alle Chunks auf Länge und CRC geprüft; fehlt der Index
    /// (z. B. abgeschnittene Datei), wird sequenziell gelesen. Beschädigte Chunks werden gemeldet und übersprungen.
    /// </summary>
    public static ResultFileContent Read(byte[] data)
    {
        var problems = new List<string>();
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new ResultFileException("Keine Ergebnisdatei (Kennung 'TRES' fehlt).");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        if (version > FormatVersion)
            throw new ResultFileException($"Formatversion {version} ist neuer als diese Anwendung (bis {FormatVersion}).");

        var chunks = new List<(string Type, byte[] Raw)>();
        var viaIndex = TryReadViaIndex(data, chunks, problems);
        if (!viaIndex)
        {
            chunks.Clear();
            ReadSequential(data, chunks, problems);
        }

        return DecodeChunks(chunks, problems, version, data.LongLength);
    }

    private static void ValidateIndexEntry(object? entry, (string Type, long Offset, int Length, uint Crc) item)
    {
        if (entry is not object?[] { Length: 4 } items || items[0] is not string text || text != item.Type
                            || items[1] is not long off || off != item.Offset || items[2] is not long length || length != item.Length
                            || items[3] is not long crc || crc != item.Crc)
            throw new FormatException("Index entry does not match its chunk.");
    }

    private static List<ResultWaveform> ReadWaveforms(Dictionary<string, WaveParts> parts, List<string> problems, Func<long, DateTimeOffset> Time)
    {
        var waveforms = new List<ResultWaveform>();
        foreach (var (id, list) in parts)
        {
            var first = list.First;
            if (!list.IsComplete)
            {
                problems.Add($"Kurve '{id}' unvollständig oder Blöcke ungültig – nicht übernommen.");
                list.Dispose();
                continue;
            }
            var wave = new Waveform(first.S("channel") ?? "", first.S("unit") ?? "", first.D("rate") ?? 0, Time(first.L("start") ?? 0),
                list.Samples.Complete(), null, first.S("quality"), first.S("sync"))
            { TimeValues = list.Times?.Complete() };
            list.Dispose();
            waveforms.Add(new ResultWaveform(id, first.L("event") ?? 0, first.S("node"), wave));
        }
        // Reihenfolge wie beim Schreiben (nach Ereignis, dann Kanal-Id)
        waveforms = waveforms.OrderBy(waveform => waveform.EventSequence).ThenBy(waveform => waveform.Id, StringComparer.Ordinal).ToList();

        return waveforms;
    }

    private static void CheckAttachments(List<ResultEvent> events, List<ResultAttachment> attachments, List<string> problems)
    {
        foreach (var resultEvent in events)
        {
            if (resultEvent.AttachmentId is { } aid && attachments.All(resultAttachment => resultAttachment.Id != aid)) problems.Add($"Anhang {aid[..Math.Min(12, aid.Length)]}… fehlt.");
        }

    }

    private static ResultFileContent DecodeChunks(IEnumerable<(string Type, byte[] Raw)> chunks, List<string> problems, int version, long fileSize)
    {
        using var iterator = chunks.GetEnumerator();
        if (!iterator.MoveNext() || iterator.Current.Type != "HEAD")
            throw new ResultFileException("Kopf (HEAD) fehlt oder ist beschädigt – Datei nicht interpretierbar.");
        var headChunk = iterator.Current;

        Dictionary<string, object?> head;
        try
        {
            head = MsgPackReader.Parse(headChunk.Raw) as Dictionary<string, object?> ?? throw new FormatException("Kopf ist keine Map.");
        }
        catch (FormatException ex)
        {
            throw new ResultFileException("Kopf (HEAD) ist beschädigt: " + ex.Message, ex);
        }
        var offset = TimeSpan.FromMinutes(head.L("utcOffsetMin") ?? 0);
        DateTimeOffset Time(long ticks) => new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(offset);

        var structure = new List<ResultNode>();
        var events = new List<ResultEvent>();
        var parts = new Dictionary<string, WaveParts>(StringComparer.Ordinal);
        var attachments = new List<ResultAttachment>();

        while (iterator.MoveNext())
        {
            var (type, raw) = iterator.Current;
            try
            {
                switch (type)
                {
                    case "STRU":
                        foreach (var item in (object?[])MsgPackReader.Parse(raw)!)
                        {
                            var items = (object?[])item!;
                            structure.Add(new ResultNode((string)items[0]!, items[1] as string, items[2] as string ?? "", items[3] as string ?? "", items[4] as string, items[5] as string));
                        }
                        break;
                    case "EVNT":
                        ReadEvents(raw, structure, events, Time);
                        break;
                    case "WAVE":
                        var entries = (Dictionary<string, object?>)MsgPackReader.Parse(raw)!;
                        var id = entries.Req("id");
                        if (!parts.TryGetValue(id, out var list)) parts[id] = list = new WaveParts(entries);
                        list.Append(entries);
                        break;
                    case "ATTA":
                        var at = (Dictionary<string, object?>)MsgPackReader.Parse(raw)!;
                        attachments.Add(new ResultAttachment(at.Req("id"), at.S("name") ?? "", at.S("media") ?? "", at.Bin("data") ?? []));
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or NullReferenceException or IndexOutOfRangeException or OverflowException)
            {
                problems.Add($"Chunk {type} nicht interpretierbar: {ex.Message}");
            }
        }

        var waveforms = ReadWaveforms(parts, problems, Time);

        CheckAttachments(events, attachments, problems);

        var run = ReadRunHeader();

        ResultRun ReadRunHeader()
        {
            return new ResultRun
            {
                RunId = head.Req("runId"),
                StationRunId = head.S("stationRunId") ?? "",
                Station = head.S("station") ?? "",
                DutId = head.S("dutId") ?? "",
                Order = head.S("order") ?? "",
                TestName = head.S("testName") ?? "",
                TestVersion = head.S("testVersion") ?? "",
                Started = Time(head.L("started") ?? 0),
                Ended = Time(head.L("ended") ?? 0),
                Rating = ParseRating(head.S("rating")),
                OperatorAbort = head.B("werror") || head.S("status") == "ABORT",
                IsDevelopmentRun = head.B("development"),
                FilteredFor = head.S("filteredFor") ?? "",
                Application = head.S("application") ?? "",
                Structure = structure,
                Events = events.OrderBy(resultEvent => resultEvent.Sequence).ToList(),
                Waveforms = waveforms,
                Attachments = attachments,
            };
        }

        return new ResultFileContent(run, problems, version, fileSize);
    }

    private static void ReadEvents(byte[] raw, List<ResultNode> structure, List<ResultEvent> events, Func<long, DateTimeOffset> time)
    {
        var entries = (Dictionary<string, object?>)MsgPackReader.Parse(raw)!;
        var strings = entries.A("strings").Select(source => source as string ?? "").ToArray();
        string? Str(object? entry) => entry is long integerValue && integerValue >= 0 && integerValue < strings.Length ? strings[integerValue] : null;
        foreach (var item in entries.A("events"))
        {
            var items = (object?[])item!;
            var nodeIdx = (long)items[2]!;
            var nodeId = nodeIdx >= 0 && nodeIdx < structure.Count ? structure[(int)nodeIdx].Id : items[3] as string;
            var refs = (object?[])items[14]!;
            var waves = refs.Length > 1 ? refs.Skip(1).Select(skip => (string)skip!).ToList() : null;
            events.Add(new ResultEvent(
                (long)items[0]!,
                (ResultEventKind)(long)items[1]!,
                nodeId,
                Str(items[4]) ?? "",
                (int)(long)items[5]!,
                time((long)items[6]!),
                items[7] is long revision && revision >= 0 ? (ResultRating)revision : null,
                ReadValue(items[8]),
                Str(items[9]),
                items[10] as double?,
                items[11] as double?,
                items[12] as string,
                items[13] is true,
                waves,
                refs[0] as string));
        }
    }

    private static bool TryReadViaIndex(byte[] data, List<(string, byte[])> chunks, List<string> problems)
    {
        if (data.Length < 8 + 12 || !data.AsSpan(data.Length - 4).SequenceEqual(TrailerMagic))
        {
            problems.Add("Dateiende/Index fehlt – Datei unvollständig, sequenziell gelesen.");
            return false;
        }
        var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(data.Length - 12));
        if (indexOffset < 8 || indexOffset > data.Length - 12 - ChunkHeaderSize
            || !TryReadChunk(data, indexOffset, out var type, out var raw, out var error) || type != "INDX")
        {
            problems.Add("Index beschädigt – sequenziell gelesen.");
            return false;
        }
        object?[] entries;
        try { entries = (object?[])MsgPackReader.Parse(raw)!; }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            problems.Add("Index nicht interpretierbar – sequenziell gelesen.");
            return false;
        }
        ReadIndexChunks(data, entries, chunks, problems);
        _ = error;
        return true;
    }

    private static void ReadIndexChunks(byte[] data, object?[] entries, List<(string, byte[])> chunks, List<string> problems)
    {
        foreach (var entry in entries)
        {
            if (entry is not object?[] { Length: 4 } items || items[0] is not string text || items[1] is not long off) continue;
            if (text == "INDX") continue;
            if (!TryReadChunk(data, off, out var actualType, out var body, out var error) || actualType != text)
            {
                problems.Add($"Chunk {text} @ {off}: {error ?? "Typ passt nicht zum Index"}");
                continue;
            }
            chunks.Add((text, body));
        }
    }

    private static void ReadSequential(byte[] data, List<(string, byte[])> chunks, List<string> problems)
    {
        long pos = 8;
        while (pos + ChunkHeaderSize <= data.Length)
        {
            if (data.AsSpan((int)pos, 4).SequenceEqual(TrailerMagic)) break;
            var stored = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)pos + 5));
            if (!TryReadChunk(data, pos, out var type, out var raw, out var error))
            {
                problems.Add($"Chunk @ {pos}: {error}");
                if (stored < 0 || pos + ChunkHeaderSize + stored > data.Length) break;
            }
            else if (type != "INDX") chunks.Add((type, raw));
            pos += ChunkHeaderSize + Math.Max(0, stored);
        }
    }

    private static bool TryReadChunk(byte[] data, long pos, out string type, out byte[] raw, out string? error)
    {
        type = "";
        raw = [];
        error = null;
        if (pos < 0 || pos + ChunkHeaderSize > data.Length)
        {
            error = "Position außerhalb der Datei";
            return false;
        }
        var position = (int)pos;
        type = Encoding.ASCII.GetString(data, position, 4);
        var codec = data[position + 4];
        var stored = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 5));
        var rawLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 9));
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 13));
        if (stored < 0 || rawLength < 0 || position + ChunkHeaderSize + (long)stored > data.Length)
        {
            error = "abgeschnitten";
            return false;
        }
        var body = data.AsSpan(position + ChunkHeaderSize, stored);
        if (Crc32.Compute(body) != crc)
        {
            error = "Prüfsumme falsch (beschädigt)";
            return false;
        }
        try
        {
            raw = codec switch
            {
                CodecNone => body.ToArray(),
                CodecDeflate => Inflate(body.ToArray(), rawLength),
                _ => throw new InvalidDataException($"unbekannter Codec {codec}"),
            };
            return true;
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (EndOfStreamException)
        {
            error = "Rohlänge passt nicht";
            return false;
        }
    }
}
