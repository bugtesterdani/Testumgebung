using System.Buffers.Binary;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class ResultStreamCorruptionTests
{
    [Theory]
    [InlineData("negative-size")]
    [InlineData("negative-raw")]
    [InlineData("oversize")]
    [InlineData("oversize-raw")]
    [InlineData("truncated")]
    [InlineData("crc")]
    [InlineData("codec")]
    [InlineData("raw-mismatch")]
    [InlineData("deflate")]
    [InlineData("partial-header")]
    [InlineData("missing-index")]
    [InlineData("index-position")]
    [InlineData("index-count")]
    [InlineData("index-entry")]
    public void Damaged_stream_preserves_head_and_reports_problem(string damage)
    {
        var head = Head(); var body = new MsgPackWriter().Array(0).ToArray();
        var chunk = Chunk("STRU", body); var index = Chunk("INDX", new MsgPackWriter().Array(0).ToArray());
        var pointer = 8 + head.Length + chunk.Length;
        switch (damage)
        {
            case "negative-size": BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(5), -1); break;
            case "negative-raw": BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(9), -1); break;
            case "oversize": BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(5), 128 * 1024 * 1024 + 1); break;
            case "oversize-raw": BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(9), 128 * 1024 * 1024 + 1); break;
            case "truncated": BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(5), 1000); break;
            case "crc": chunk[^1] ^= 1; break;
            case "codec": chunk[4] = 255; break;
            case "raw-mismatch": BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(9), 2); break;
            case "deflate": chunk = Chunk("STRU", [255, 255, 255], 1, 1); break;
            case "partial-header": chunk = [1, 2, 3]; index = []; break;
            case "missing-index": index = []; break;
            case "index-position": pointer++; break;
            case "index-entry": index = Chunk("INDX", new MsgPackWriter().Array(2).Nil().Nil().ToArray()); break;
        }
        var data = FileBytes([head, chunk, index], pointer);
        WithFile(data, path =>
        {
            var read = ResultFile.Read(path);
            Assert.Equal("run", read.Run.RunId); Assert.False(read.IsIntact); Assert.NotEmpty(read.Problems);
        });
    }

    [Theory]
    [InlineData("STRU")]
    [InlineData("EVNT")]
    [InlineData("WAVE")]
    [InlineData("ATTA")]
    public void Invalid_metadata_chunks_are_reported_and_skipped(string type)
    {
        WithFile(FileBytes([Head(), Chunk(type, new MsgPackWriter().Int(5).ToArray())]), path =>
        {
            var read = ResultFile.Read(path);
            Assert.Equal("run", read.Run.RunId);
            Assert.Contains(read.Problems, p => p.Contains($"Chunk {type} nicht interpretierbar"));
            Assert.Empty(read.Run.Structure); Assert.Empty(read.Run.Events); Assert.Empty(read.Run.Waveforms); Assert.Empty(read.Run.Attachments);
        });
    }

    [Theory]
    [InlineData("short")]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("no-head")]
    [InlineData("head-scalar")]
    [InlineData("head-invalid")]
    public void Unreadable_header_is_rejected(string damage)
    {
        var data = FileBytes([Head()]);
        if (damage == "short") data = data[..3];
        if (damage == "magic") data[0] = 0;
        if (damage == "version") data[4] = 99;
        if (damage == "no-head") data = FileBytes([Chunk("STRU", [0x90])]);
        if (damage == "head-scalar") data = FileBytes([Chunk("HEAD", [5])]);
        if (damage == "head-invalid") data = FileBytes([Chunk("HEAD", [0xc1])]);
        WithFile(data, path => Assert.Throws<ResultFileException>(() => ResultFile.Read(path)));
    }

    [Fact]
    public void Unknown_chunks_and_missing_trailer_keep_valid_data()
    {
        WithFile(FileBytes([Head(), Chunk("FUTR", [0xc1])]), path =>
        {
            var read = ResultFile.Read(path); Assert.Equal("run", read.Run.RunId);
            Assert.Single(read.Problems); Assert.Contains("Dateiende/Index fehlt", read.Problems[0]);
        });
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tres");
        Assert.Contains("nicht lesbar", Assert.Throws<ResultFileException>(() => ResultFile.Read(missing)).Message);
    }

    [Theory]
    [InlineData("total-overflow")]
    [InlineData("parts-overflow")]
    [InlineData("negative-total")]
    [InlineData("oversize-total")]
    [InlineData("zero-parts")]
    [InlineData("wrong-part")]
    [InlineData("wrong-offset")]
    [InlineData("wrong-total")]
    [InlineData("wrong-parts")]
    [InlineData("too-many-samples")]
    [InlineData("wrong-times-size")]
    [InlineData("missing-times")]
    [InlineData("unexpected-times")]
    [InlineData("missing-second-part")]
    public void Invalid_waveform_parts_are_skipped_without_losing_the_run_header(string damage)
    {
        byte[] Part(long total = 2, long parts = 2, long part = 0, long offset = 0, int samples = 1, int? times = null)
        {
            var writer = new MsgPackWriter().Map(times is null ? 6 : 7).Key("id").Str("wave")
                .Key("total").Int(total).Key("parts").Int(parts).Key("part").Int(part).Key("offset").Int(offset)
                .Key("samples").Bin(new byte[samples * 8]);
            if (times is { } count) writer.Key("times").Bin(new byte[count * 8]);
            return Chunk("WAVE", writer.ToArray());
        }
        var first = Part(times: damage == "missing-times" ? 1 : null);
        var second = Part(part: 1, offset: 1);
        switch (damage)
        {
            case "total-overflow": first = Part(total: (long)int.MaxValue + 1); break;
            case "parts-overflow": first = Part(parts: (long)int.MaxValue + 1); break;
            case "negative-total": first = Part(total: -1); break;
            case "oversize-total": first = Part(total: 50_000_001); break;
            case "zero-parts": first = Part(parts: 0); break;
            case "wrong-part": second = Part(part: 2, offset: 1); break;
            case "wrong-offset": second = Part(part: 1, offset: 0); break;
            case "wrong-total": second = Part(total: 3, part: 1, offset: 1); break;
            case "wrong-parts": second = Part(parts: 3, part: 1, offset: 1); break;
            case "too-many-samples": second = Part(part: 1, offset: 1, samples: 2); break;
            case "wrong-times-size": first = Part(times: 2); second = Part(part: 1, offset: 1, times: 1); break;
            case "unexpected-times": second = Part(part: 1, offset: 1, times: 1); break;
            case "missing-second-part": second = []; break;
        }
        WithFile(FileBytes([Head(), first, second]), path =>
        {
            var read = ResultFile.Read(path);
            Assert.Equal("run", read.Run.RunId); Assert.Empty(read.Run.Waveforms); Assert.NotEmpty(read.Problems);
        });
    }

    [Theory]
    [InlineData("negative-size")]
    [InlineData("negative-raw")]
    [InlineData("truncated")]
    [InlineData("crc")]
    [InlineData("codec")]
    [InlineData("deflate")]
    [InlineData("index-before-header")]
    [InlineData("index-after-end")]
    [InlineData("index-wrong-type")]
    [InlineData("index-scalar")]
    [InlineData("index-invalid")]
    [InlineData("entry-before-start")]
    [InlineData("entry-after-end")]
    [InlineData("entry-wrong-type")]
    public void Memory_reader_preserves_valid_header_when_index_or_body_is_damaged(string damage)
    {
        var head = Head(); var body = Chunk("STRU", [0x90]);
        var bodyOffset = 8 + head.Length;
        long entryOffset = bodyOffset; var entryType = "STRU";
        switch (damage)
        {
            case "negative-size": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), -1); break;
            case "negative-raw": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(9), -1); break;
            case "truncated": BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(5), 10000); break;
            case "crc": body[^1] ^= 1; break;
            case "codec": body[4] = 255; break;
            case "deflate": body = Chunk("STRU", [255, 255, 255], 1, 1); break;
            case "entry-before-start": entryOffset = -1; break;
            case "entry-after-end": entryOffset = 1_000_000; break;
            case "entry-wrong-type": entryType = "EVNT"; break;
        }
        var entries = new MsgPackWriter().Array(2).Array(4).Str("HEAD").Int(8).Int(head.Length).Int(0)
            .Array(4).Str(entryType).Int(entryOffset).Int(body.Length).Int(0).ToArray();
        var index = Chunk("INDX", entries);
        long pointer = bodyOffset + body.Length;
        switch (damage)
        {
            case "index-before-header": pointer = 7; break;
            case "index-after-end": pointer = 1_000_000; break;
            case "index-wrong-type": index = Chunk("FUTR", entries); break;
            case "index-scalar": index = Chunk("INDX", [5]); break;
            case "index-invalid": index = Chunk("INDX", [0xc1]); break;
        }
        var read = ResultFile.Read(FileBytes([head, body, index], pointer));
        Assert.Equal("run", read.Run.RunId); Assert.False(read.IsIntact); Assert.NotEmpty(read.Problems);
        Assert.Empty(read.Run.Structure);
    }

    [Fact]
    public void Memory_reader_skips_unknown_index_entries_and_accepts_valid_entries()
    {
        var head = Head(); var body = Chunk("STRU", [0x90]); var pointer = 8 + head.Length + body.Length;
        var index = Chunk("INDX", new MsgPackWriter().Array(7)
            .Nil().Array(0).Array(4).Int(7).Int(8).Int(0).Int(0)
            .Array(4).Str("HEAD").Str("bad offset").Int(0).Int(0)
            .Array(4).Str("INDX").Int(pointer).Int(0).Int(0)
            .Array(4).Str("HEAD").Int(8).Int(head.Length).Int(0)
            .Array(4).Str("STRU").Int(8 + head.Length).Int(body.Length).Int(0).ToArray());
        var read = ResultFile.Read(FileBytes([head, body, index], pointer));
        Assert.Equal("run", read.Run.RunId); Assert.True(read.IsIntact); Assert.Empty(read.Run.Structure);
        foreach (var data in new[] { Array.Empty<byte>(), "WRONGHDR"u8.ToArray(), "TRES\x63\0\0\0"u8.ToArray(), FileBytes([Chunk("STRU", [0x90])]) })
            Assert.Throws<ResultFileException>(() => ResultFile.Read(data));
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("type-kind")]
    [InlineData("type-value")]
    [InlineData("offset-kind")]
    [InlineData("offset-value")]
    [InlineData("length-kind")]
    [InlineData("length-value")]
    [InlineData("crc-kind")]
    [InlineData("crc-value")]
    [InlineData("valid")]
    public void Stream_index_checks_every_field_against_actual_chunk(string damage)
    {
        var head = Head(); var entry = new MsgPackWriter();
        if (damage == "shape") entry.Nil();
        else
        {
            entry.Array(4);
            if (damage == "type-kind") entry.Int(0); else entry.Str(damage == "type-value" ? "EVNT" : "HEAD");
            if (damage == "offset-kind") entry.Str("8"); else entry.Int(damage == "offset-value" ? 9 : 8);
            if (damage == "length-kind") entry.Str("10"); else entry.Int(head.Length - ResultFile.ChunkHeaderSize + (damage == "length-value" ? 1 : 0));
            if (damage == "crc-kind") entry.Str("0"); else entry.Int(BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(13)) + (damage == "crc-value" ? 1L : 0));
        }
        var payload = new MsgPackWriter().Array(1).ToArray().Concat(entry.ToArray()).ToArray(); var pointer = 8 + head.Length;
        WithFile(FileBytes([head, Chunk("INDX", payload)], pointer), path =>
        {
            var read = ResultFile.Read(path); Assert.Equal("run", read.Run.RunId); Assert.Equal(damage == "valid", read.IsIntact);
            if (damage != "valid") Assert.Contains(read.Problems, p => p.Contains("Index beschädigt"));
        });
    }

    private static byte[] Head() => Chunk("HEAD", new MsgPackWriter().Map(1).Key("runId").Str("run").ToArray());
    private static byte[] Chunk(string type, byte[] body, byte codec = 0, int? rawSize = null)
    {
        var data = new byte[ResultFile.ChunkHeaderSize + body.Length]; Encoding.ASCII.GetBytes(type).CopyTo(data, 0); data[4] = codec;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(5), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(9), rawSize ?? body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(13), Crc32.Compute(body)); body.CopyTo(data, ResultFile.ChunkHeaderSize); return data;
    }
    private static byte[] FileBytes(byte[][] chunks, long? pointer = null)
    {
        using var stream = new MemoryStream(); stream.Write("TRES\x01\0\0\0"u8);
        foreach (var chunk in chunks) stream.Write(chunk);
        if (pointer is { } p) { var bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, p); stream.Write(bytes); stream.Write("TEND"u8); }
        return stream.ToArray();
    }
    private static void WithFile(byte[] data, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "tu-stream-" + Guid.NewGuid() + ".tres");
        try { File.WriteAllBytes(path, data); action(path); } finally { File.Delete(path); }
    }
}
