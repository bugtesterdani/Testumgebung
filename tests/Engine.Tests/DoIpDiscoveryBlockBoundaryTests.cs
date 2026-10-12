using System.Net;
using System.Net.Sockets;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class DoIpDiscoveryBlockBoundaryTests
{
    public static IEnumerable<object[]> Expectations()
    {
        foreach (var vin in new[] { "", " vin0000000000000x ", "OTHER" })
            foreach (var address in new[] { "", "0x1000", "0x2000" })
                foreach (var minimum in new[] { "", "1", "2" }) yield return [vin, address, minimum];
    }

    [Theory, MemberData(nameof(Expectations))]
    public async Task Discovery_block_rates_received_vehicles_against_each_optional_expectation(string vin, string address, string minimum)
    {
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var serve = Task.Run(async () =>
        {
            var request = await responder.ReceiveAsync(deadline.Token);
            var payload = new byte[33]; Encoding.ASCII.GetBytes("VIN0000000000000X").CopyTo(payload, 0);
            payload[17] = 0x10;
            var announcement = DoIpTransport.Frame(2, DoIpDiscovery.VehicleAnnouncement, payload);
            await responder.SendAsync(announcement, request.RemoteEndPoint, deadline.Token);
        });
        var step = BlockCatalog.Default.Create(BusBlocks.DoIpDiscover, "discover", "Discover", new Dictionary<string, string>
        { ["Host"] = "127.0.0.1", ["Port"] = port.ToString(), ["Zeitlimit"] = "100", ["ErwarteteVin"] = vin, ["ErwarteteAdresse"] = address, ["Mindestens"] = minimum });
        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within(); await serve;
        var result = Assert.Single(run.Steps("discover"));
        var failed = vin == "OTHER" || address == "0x2000" || minimum == "2";
        var rated = vin.Length > 0 || address.Length > 0 || minimum.Length > 0;
        Assert.Equal(failed ? ResultRating.Fail : rated ? ResultRating.Pass : ResultRating.NResult, result.Rating);
        Assert.Equal(1, result.Value); Assert.Contains("VIN0000000000000X@0x1000", result.Message);
        if (vin == "OTHER") Assert.Contains("VIN OTHER nicht gefunden", result.Message);
        if (address == "0x2000") Assert.Contains("Adresse 0x2000 nicht gefunden", result.Message);
        if (minimum == "2") Assert.Contains("mindestens 2", result.Message);
    }
}
