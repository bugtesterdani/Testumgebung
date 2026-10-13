using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class DoIpReceiveQualityTests
{
    private sealed class Peer : IByteStreamAdapter
    {
        private readonly Queue<byte> _input = new();
        public List<byte[]> Sent { get; } = [];
        public void Add(ushort type, params byte[] payload)
        {
            foreach (var b in DoIpTransport.Frame(2, type, payload)) _input.Enqueue(b);
        }
        public Task SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken token)
        { Sent.Add(data.ToArray()); return Task.CompletedTask; }
        public Task<byte[]> ReceiveAsync(string channel, ReceiveRule rule, CancellationToken token)
        {
            if (_input.Count < rule.Length)
                return Task.FromException<byte[]>(new CommunicationTimeoutException("fixture timeout", [0xAB]));
            return Task.FromResult(Enumerable.Range(0, rule.Length!.Value).Select(_ => _input.Dequeue()).ToArray());
        }
        public void DiscardInput(string channel) => _input.Clear();
    }

    [Fact]
    public async Task ReceiveAnswersAliveChecksAndSkipsAcksUnknownMessagesAndOtherEcus()
    {
        var peer = new Peer();
        peer.Add(DoIpTransport.AliveCheckRequest);
        peer.Add(DoIpTransport.DiagnosticAck);
        peer.Add(0x9999);
        peer.Add(DoIpTransport.DiagnosticMessage);
        peer.Add(DoIpTransport.DiagnosticMessage, 0x20, 0, 0x0E, 0, 0x62);
        peer.Add(DoIpTransport.DiagnosticMessage, 0x10, 0, 0x0E, 0, 0x62, 0xF1, 0x90);
        var transport = new DoIpTransport(peer, "bus", new DoIpSettings());
        Assert.Equal(new byte[] { 0x62, 0xF1, 0x90 }, await transport.ReceiveAsync(TimeSpan.FromSeconds(1), default));
        var aliveReply = Assert.Single(peer.Sent);
        Assert.Equal(DoIpTransport.Frame(2, DoIpTransport.AliveCheckResponse, [0x0E, 0]), aliveReply);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ReceiveReportsNegativeAcknowledgementsWithOrWithoutAnErrorCode(bool diagnostic, bool code)
    {
        var peer = new Peer();
        byte[] payload = diagnostic ? code ? [0x10, 0, 0x0E, 0, 2] : [] : code ? [2] : [];
        peer.Add(diagnostic ? DoIpTransport.DiagnosticNack : DoIpTransport.GenericNack, payload);
        var error = await Assert.ThrowsAsync<CommunicationException>(() =>
            new DoIpTransport(peer, "bus", new DoIpSettings()).ReceiveAsync(TimeSpan.FromSeconds(1), default));
        Assert.Contains(code ? "0x02" : "0x00", error.Message);
    }

    [Fact]
    public async Task ReceiveReportsTimeoutsAndKeepsThePartialResponse()
    {
        var transport = new DoIpTransport(new Peer(), "bus", new DoIpSettings());
        var expired = await Assert.ThrowsAsync<CommunicationTimeoutException>(() => transport.ReceiveAsync(TimeSpan.Zero, default));
        Assert.Empty(expired.Partial);
        var partial = await Assert.ThrowsAsync<CommunicationTimeoutException>(() => transport.ReceiveAsync(TimeSpan.FromSeconds(1), default));
        Assert.Equal(new byte[] { 0xAB }, partial.Partial);
        Assert.Contains("keine Antwort", partial.Message);
    }
}
