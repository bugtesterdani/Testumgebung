using System.Net;
using System.Net.Sockets;
using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class UdpTransportBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Udp_transport_opens_once_preserves_partial_datagrams_and_handles_timeout_and_cancellation(bool ipv6)
    {
        var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var server = new UdpClient(new IPEndPoint(address, 0));
        using var transport = new UdpTransport(address.ToString(), ((IPEndPoint)server.Client.LocalEndPoint!).Port, 0);
        Assert.False(transport.IsOpen); Assert.DoesNotContain("lokal", transport.Description);
        await Assert.ThrowsAsync<CommunicationException>(() => transport.WriteAsync(new byte[] { 1 }, CancellationToken.None));
        await transport.OpenAsync(CancellationToken.None); await transport.OpenAsync(CancellationToken.None); Assert.True(transport.IsOpen);
        await transport.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
        var received = await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(new byte[] { 1, 2 }, received.Buffer);
        await server.SendAsync(new byte[] { 3, 4, 5, 6 }, received.RemoteEndPoint);
        var first = new byte[2]; Assert.Equal(2, await transport.ReadAsync(first, TimeSpan.FromSeconds(1), CancellationToken.None)); Assert.Equal(new byte[] { 3, 4 }, first);
        var last = new byte[8]; Assert.Equal(2, await transport.ReadAsync(last, TimeSpan.FromSeconds(1), CancellationToken.None)); Assert.Equal(new byte[] { 5, 6 }, last[..2]);
        Assert.Equal(0, await transport.ReadAsync(last, TimeSpan.FromMilliseconds(10), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ReadAsync(last, TimeSpan.FromSeconds(1), cancelled.Token));
        await server.SendAsync(new byte[] { 7, 8 }, received.RemoteEndPoint); await Task.Delay(20); transport.DiscardInput();
        Assert.Equal(0, await transport.ReadAsync(last, TimeSpan.FromMilliseconds(10), CancellationToken.None));
        transport.Close(); transport.Close(); Assert.False(transport.IsOpen); transport.DiscardInput();
        using var explicitPort = new UdpTransport(address.ToString(), 1, 32123); Assert.Contains("lokal 32123", explicitPort.Description);
    }
}
