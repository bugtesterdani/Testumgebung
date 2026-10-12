using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Hardware;

namespace Testumgebung.Engine.Tests;

public sealed class StreamSimulationBoundaryTests
{
    [Theory]
    [InlineData("hex:01 ?? => hex:AA BB", "0102", "AABB")]
    [InlineData("hex:01 02 => hex:AA", "0102", "AA")]
    [InlineData("hex:01 03 => hex:FF\nhex:01 ?? => hex:AA", "0102", "AA")]
    [InlineData("hex:01 => hex:FF\nhex:01 02 => hex:AA", "0102", "AA")]
    [InlineData("ignored => wrong\n.* => hex:AA", "0102", "AA")]
    public async Task Binary_rules_match_length_exact_bytes_and_wildcards(string rules, string request, string expected)
    {
        var peer = new SimulatedStreamDut(rules);
        await peer.SendAsync("stream", Convert.FromHexString(request), default);
        Assert.Equal(Convert.FromHexString(expected), await peer.ReceiveAsync("stream", new ReceiveRule(null, null, TimeSpan.Zero), default));
        Assert.Single(peer.Requests);
    }

    [Theory]
    [InlineData("q(.*) => $1-$9", "qanswer", "answer-")]
    [InlineData("q => {zufall:1:1}", "q", "1")]
    [InlineData("q => {zufall:1.25:1.25:2}", "q", "1.25")]
    [InlineData("antwortende=\\r\\n\nq => answer", "q", "answer\r\n")]
    public async Task Text_rules_substitute_groups_random_values_and_configured_endings(string rules, string request, string expected)
    {
        var peer = new SimulatedStreamDut(rules);
        await peer.SendAsync("stream", Encoding.ASCII.GetBytes(request), default);
        Assert.Equal(expected, Encoding.ASCII.GetString(await peer.ReceiveAsync("stream", new ReceiveRule(null, null, TimeSpan.Zero), default)));
    }

    [Theory]
    [InlineData("missing arrow")]
    [InlineData("[ => answer")]
    [InlineData("hex:XX => answer")]
    public void Invalid_rules_are_rejected(string rules) => Assert.Throws<FormatException>(() => new SimulatedStreamDut(rules));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Fixed_length_reads_preserve_remaining_bytes(int length)
    {
        var peer = new SimulatedStreamDut("q => abcd");
        await peer.SendAsync("stream", "q"u8.ToArray(), default);
        Assert.Equal("abcd"[..length], Encoding.ASCII.GetString(await peer.ReceiveAsync("stream", new ReceiveRule(null, length, TimeSpan.Zero), default)));
        if (length < 4) Assert.Equal("abcd"[length..], Encoding.ASCII.GetString(await peer.ReceiveAsync("stream", new ReceiveRule(null, null, TimeSpan.Zero), default)));
        peer.DiscardInput("stream");
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() => peer.ReceiveAsync("stream", new ReceiveRule(null, null, TimeSpan.Zero), default));
    }

    [Theory]
    [InlineData("abEND", "END", "ab")]
    [InlineData("ENEND", "END", "EN")]
    [InlineData("END", "END", "")]
    public async Task Terminators_are_removed_only_when_fully_matched(string response, string terminator, string expected)
    {
        var peer = new SimulatedStreamDut("q => " + response);
        await peer.SendAsync("stream", "q"u8.ToArray(), default);
        Assert.Equal(expected, Encoding.ASCII.GetString(await peer.ReceiveAsync("stream", new ReceiveRule(Encoding.ASCII.GetBytes(terminator), null, TimeSpan.Zero), default)));
    }

    [Fact]
    public async Task Partial_response_is_reported_on_timeout_and_discard_removes_buffered_input()
    {
        var peer = new SimulatedStreamDut("q => abc");
        await peer.SendAsync("stream", "q"u8.ToArray(), default);
        var error = await Assert.ThrowsAsync<CommunicationTimeoutException>(() => peer.ReceiveAsync("stream", new ReceiveRule("END"u8.ToArray(), null, TimeSpan.Zero), default));
        Assert.Contains("keine passende Regel", error.Message);
        await peer.SendAsync("stream", "q"u8.ToArray(), default);
        peer.DiscardInput("stream");
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() => peer.ReceiveAsync("stream", new ReceiveRule(null, 1, TimeSpan.Zero), default));
    }
}
