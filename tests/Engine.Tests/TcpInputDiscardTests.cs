using System.Net;
using System.Net.Sockets;
using System.Text;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class TcpInputDiscardTests
{
    [Fact]
    public async Task Delayed_reply_is_drained_without_reconnecting_or_consuming_the_next_measurement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
            foreach (var expected in new[] { "*IDN?", "MEAS?", "MEAS?" })
            {
                var command = await reader.ReadLineAsync(token); Assert.Equal(expected, command); commands.Add(command!);
                if (expected == "*IDN?") await Task.Delay(30, token);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(expected == "*IDN?" ? "Delayed,ID,123,1\n" : "8.5\n"), token);
            }
        }, token);
        var profile = new CustomDeviceProfile
        {
            Id = "tcp-meter", Name = "TCP meter", SafeCommands = "*IDN?",
            InputDiscard = new() { SettleMilliseconds = 100 },
            Channels = [new() { Name = "U", ReadQuery = "MEAS?" }]
        };
        using var driver = new CustomTextInstrument(profile, new ScpiSession($"127.0.0.1:{port}"));
        await driver.SafeStateAsync(token);
        Assert.Equal(8.5, await driver.ReadAsync("U", token));
        Assert.Equal(8.5, await driver.ReadAsync("U", token));
        await server.WaitAsync(token);
        Assert.Equal(3, commands.Count);
        Assert.Contains(driver.Log, line => line.Contains("verworfen") && line.Contains("Delayed,ID"));
    }
}
