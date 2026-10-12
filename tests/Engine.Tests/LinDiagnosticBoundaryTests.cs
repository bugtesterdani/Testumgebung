using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class LinDiagnosticBoundaryTests
{
    private sealed class Lin : ILinAdapter
    {
        public Queue<byte[]> Responses { get; } = new();
        public List<byte[]> Sent { get; } = [];
        public Task SendAsync(string channel, byte frameId, byte[] data, LinChecksum checksum, CancellationToken cancellationToken)
        { Assert.Equal(LinDiagnosticTransport.MasterRequestFrame, frameId); Assert.Equal(LinChecksum.Classic, checksum); Sent.Add(data); return Task.CompletedTask; }
        public Task<byte[]> RequestAsync(string channel, byte frameId, int length, LinChecksum checksum, TimeSpan timeout, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Assert.Equal(LinDiagnosticTransport.SlaveResponseFrame, frameId); return Responses.Count > 0 ? Task.FromResult(Responses.Dequeue()) : throw new CommunicationTimeoutException("empty", []); }
    }

    [Theory]
    [InlineData("wrong-node")]
    [InlineData("short-first-frame")]
    [InlineData("short")]
    [InlineData("zero-single")]
    [InlineData("oversized-single")]
    [InlineData("truncated-single")]
    [InlineData("unexpected-consecutive")]
    [InlineData("zero-first")]
    [InlineData("short-total")]
    public async Task Malformed_or_other_node_frames_are_ignored_before_valid_response(string scenario)
    {
        var lin = new Lin();
        lin.Responses.Enqueue(scenario switch
        { "wrong-node" => [2, 1, 0x99], "short-first-frame" => [1, 0x10], "short" => [1], "zero-single" => [1, 0], "oversized-single" => [1, 7], "truncated-single" => [1, 2, 0x99], "zero-first" => [1, 0x10, 0], "short-total" => [1, 0x10, 6, 1, 2, 3, 4, 5], _ => [1, 0x21, 0x99] });
        lin.Responses.Enqueue([1, 2, 0x62, 0x01]);
        var transport = new LinDiagnosticTransport(lin, "lin", 1, TimeSpan.FromMilliseconds(1));
        Assert.Equal(new byte[] { 0x62, 1 }, await transport.ReceiveAsync(TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task Segmented_response_ignores_wrong_sequence_and_times_out_with_partial_payload()
    {
        var lin = new Lin(); lin.Responses.Enqueue([1, 0x10, 8, 0x62, 1, 2, 3, 4]); lin.Responses.Enqueue([1, 0x22, 99]); lin.Responses.Enqueue([1, 0x21, 5, 6, 7]);
        var transport = new LinDiagnosticTransport(lin, "lin", 1, TimeSpan.FromMilliseconds(1));
        Assert.Equal(new byte[] { 0x62, 1, 2, 3, 4, 5, 6, 7 }, await transport.ReceiveAsync(TimeSpan.FromSeconds(1), default));
        lin.Responses.Enqueue([1, 0x10, 8, 0x62, 1, 2, 3, 4]);
        var error = await Assert.ThrowsAsync<CommunicationTimeoutException>(() => transport.ReceiveAsync(TimeSpan.FromMilliseconds(10), default));
        Assert.Contains("keine vollständige Antwort", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(110)]
    public async Task Outgoing_frames_preserve_payload_across_sequence_wrap(int length)
    {
        var lin = new Lin(); var transport = new LinDiagnosticTransport(lin, "lin", 1);
        var payload = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        await transport.SendAsync(payload, default);
        Assert.All(lin.Sent, frame => { Assert.Equal(8, frame.Length); Assert.Equal(1, frame[0]); });
        var actual = length <= 6 ? lin.Sent[0].Skip(2).Take(length) : lin.Sent[0].Skip(3).Concat(lin.Sent.Skip(1).SelectMany(frame => frame.Skip(2))).Take(length);
        Assert.Equal(payload, actual);
        for (var i = 1; i < lin.Sent.Count; i++) Assert.Equal(0x20 | (i & 15), lin.Sent[i][1]);
    }
}
