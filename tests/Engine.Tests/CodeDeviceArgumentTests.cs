using Testumgebung.Contracts;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;

namespace Testumgebung.Engine.Tests;

public sealed class CodeDeviceArgumentTests
{
    private sealed class CanPeer : IHardwareAdapter, ICanAdapter
    {
        public string Name => "CAN fixture";
        public CanFrame? Sent; public CanFilter? Filter; public TimeSpan Timeout; public bool Discarded;
        public ChannelCapabilities GetCapabilities(string channel) => throw new NotSupportedException();
        public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SendAsync(string channel, CanFrame frame, CancellationToken cancellationToken) { Assert.Equal("bus", channel); Sent = frame; return Task.CompletedTask; }
        public Task<CanFrame> ReceiveAsync(string channel, CanFilter filter, TimeSpan timeout, CancellationToken cancellationToken) { Filter = filter; Timeout = timeout; return Task.FromResult(new CanFrame(0x123, [1, 2])); }
        public void DiscardInput(string channel) => Discarded = true;
    }
    public static IEnumerable<object?[]> Flags()
    {
        foreach (var value in new object?[] { null, true, false, 0L, 1L, -1L, "1", "true", "True", "ja", "0", "false", "nein", 1.0, 1 })
            yield return [value, value is true or 1L or -1L or "1" or "true" or "True" or "ja"];
    }
    [Theory]
    [MemberData(nameof(Flags))]
    public async Task Code_flags_accept_documented_scalar_forms(object? flag, bool expected)
    {
        var peer = new CanPeer(); var arguments = new Dictionary<string, object?> { ["id"] = 0x123L, ["extended"] = flag, ["fd"] = flag, ["discard"] = flag };
        Assert.Null(await CodeDevices.ExecuteAsync(peer, "bus", "can.send", arguments, default));
        Assert.Equal(expected, peer.Sent!.Extended); Assert.Equal(expected, peer.Sent.Fd); Assert.Empty(peer.Sent.Data);
        var received = Assert.IsType<Dictionary<string, object?>>(await CodeDevices.ExecuteAsync(peer, "bus", "can.receive", arguments, default));
        Assert.Equal("0102", received["data"]); Assert.Equal(expected, peer.Discarded); Assert.Equal(TimeSpan.FromSeconds(1), peer.Timeout);
        Assert.Equal(flag is null ? null : (bool?)expected, peer.Filter!.Extended);
    }
    [Theory]
    [InlineData(123L, 123L)]
    [InlineData(123.9, 123L)]
    [InlineData(true, 1L)]
    [InlineData(false, 0L)]
    [InlineData("0x7B", 123L)]
    [InlineData("123", 123L)]
    [InlineData(123, 123L)]
    public async Task Code_integer_arguments_accept_worker_value_types(object value, long expected)
    {
        var peer = new CanPeer(); await CodeDevices.ExecuteAsync(peer, "bus", "can.send", new Dictionary<string, object?> { ["id"] = value, ["data"] = "AA" }, default);
        Assert.Equal((uint)expected, peer.Sent!.Id); Assert.Equal(new byte[] { 0xAA }, peer.Sent.Data);
        await CodeDevices.ExecuteAsync(peer, "bus", "can.receive", new Dictionary<string, object?> { ["id"] = value, ["mask"] = "0x7FF", ["timeout"] = 123L }, default);
        Assert.Equal(TimeSpan.FromMilliseconds(123), peer.Timeout); Assert.Equal(0x7FFu, peer.Filter!.Mask);
    }
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task Stream_simulator_preserves_request_line_ending(string end)
    {
        var peer = new SimulatedStreamDut("query => answer");
        await peer.SendAsync("stream", System.Text.Encoding.ASCII.GetBytes("query" + end), default);
        var bytes = await peer.ReceiveAsync("stream", new ReceiveRule(null, 6 + end.Length, TimeSpan.FromMilliseconds(1)), default);
        Assert.Equal("answer" + end, System.Text.Encoding.ASCII.GetString(bytes));
    }
    [Fact]
    public async Task Empty_stream_request_is_matched_without_line_end()
    {
        var peer = new SimulatedStreamDut(".* => ok"); await peer.SendAsync("stream", ReadOnlyMemory<byte>.Empty, default);
        Assert.Equal("ok", System.Text.Encoding.ASCII.GetString(await peer.ReceiveAsync("stream", new ReceiveRule(null, 2, TimeSpan.FromMilliseconds(1)), default)));
    }
}
