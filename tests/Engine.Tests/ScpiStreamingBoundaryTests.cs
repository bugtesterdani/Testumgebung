using System.Text;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class ScpiStreamingBoundaryTests
{
    [Theory]
    [InlineData("#13abc\n", "abc")]
    [InlineData("#10\n", "")]
    [InlineData("#0abc\n", "abc")]
    public async Task BufferedBinaryQueryReturnsOnlyThePayload(string response, string expected)
    {
        var io = new Io();
        io.Chunks.Enqueue(new IoChunk(Encoding.ASCII.GetBytes(response), true));
        using var session = new ScpiSession(() => io);
        Assert.Equal(Encoding.ASCII.GetBytes(expected), await session.QueryBlockAsync("DATA?"));
        io.Chunks.Enqueue(new IoChunk("ready\n"u8.ToArray(), true));
        Assert.Equal("ready", await session.QueryAsync("STATE?"));
    }

    [Fact]
    public async Task BufferedBinaryQueryRejectsAPlainTextReplyAndReleasesTheGate()
    {
        var io = new Io();
        io.Chunks.Enqueue(new IoChunk("text\n"u8.ToArray(), true));
        using var session = new ScpiSession(() => io);
        Assert.Contains("kein IEEE-Block", (await Assert.ThrowsAsync<InstrumentException>(() => session.QueryBlockAsync("DATA?"))).Message);
        io.Chunks.Enqueue(new IoChunk("ready\n"u8.ToArray(), true));
        Assert.Equal("ready", await session.QueryAsync("STATE?"));
    }

    private sealed class Io : IInstrumentIo
    {
        public Queue<IoChunk> Chunks { get; } = new();
        public int Clears;
        public string Resource => "fixture";
        public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(Chunks.Dequeue());
        public Task ClearAsync(CancellationToken cancellationToken) { Clears++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    public static IEnumerable<object[]> Splits()
    {
        foreach (var length in new[] { 0, 1, 9, 10, 100 })
            foreach (var width in new[] { 1, 2, 3, 7, 1024 })
                foreach (var end in new[] { false, true }) yield return [length, width, end];
    }

    [Theory, MemberData(nameof(Splits))]
    public async Task Payload_is_delivered_once_across_header_and_data_boundaries(int length, int width, bool end)
    {
        var payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var count = length.ToString();
        var bytes = Encoding.ASCII.GetBytes($"#{count.Length}{count}").Concat(payload).Concat(end ? Array.Empty<byte>() : new byte[] { 10 }).ToArray();
        var io = new Io();
        for (var offset = 0; offset < bytes.Length; offset += width)
        {
            var take = Math.Min(width, bytes.Length - offset);
            io.Chunks.Enqueue(new IoChunk(bytes.AsSpan(offset, take).ToArray(), end && offset + take == bytes.Length));
        }
        using var session = new ScpiSession(() => io);
        var starts = new List<int>(); var actual = new List<byte>();
        await session.QueryBlockStreamAsync("DATA?", starts.Add, chunk => actual.AddRange(chunk.ToArray()));
        Assert.Equal(length, Assert.Single(starts)); Assert.Equal(payload, actual.ToArray()); Assert.Empty(io.Chunks); Assert.Equal(0, io.Clears);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("#0abc")]
    [InlineData("#/")]
    [InlineData("#:")]
    [InlineData("#2xx")]
    [InlineData("#2-1")]
    [InlineData("#2+1x")]
    [InlineData("#2 1x")]
    [InlineData("#9100000001")]
    [InlineData("#")]
    [InlineData("#2")]
    [InlineData("#203ab")]
    public async Task Bad_or_truncated_block_resets_transport_before_next_query(string response)
    {
        var io = new Io(); io.Chunks.Enqueue(new IoChunk(Encoding.ASCII.GetBytes(response), true));
        using var session = new ScpiSession(() => io);
        await Assert.ThrowsAsync<InstrumentException>(() => session.QueryBlockStreamAsync("DATA?", _ => { }, _ => { }));
        io.Chunks.Enqueue(new IoChunk("ready\n"u8.ToArray(), true));
        Assert.Equal("ready", await session.QueryAsync("STATE?")); Assert.Equal(1, io.Clears);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Consumer_failure_releases_gate_and_requires_resynchronization(bool failAtBegin)
    {
        var io = new Io(); io.Chunks.Enqueue(new IoChunk("#11x"u8.ToArray(), true));
        using var session = new ScpiSession(() => io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryBlockStreamAsync("DATA?", _ => { if (failAtBegin) throw new InvalidOperationException(); }, _ => { if (!failAtBegin) throw new InvalidOperationException(); }));
        io.Chunks.Enqueue(new IoChunk("ok\n"u8.ToArray(), true));
        Assert.Equal("ok", await session.QueryAsync("STATE?")); Assert.Equal(1, io.Clears);
    }
}
