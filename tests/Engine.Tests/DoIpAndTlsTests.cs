using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Offene Punkte ADR 0014: TLS für den TCP-Client, DoIP-Fahrzeugsuche (UDP), UDS über DoIP mit TLS.</summary>
public sealed class DoIpAndTlsTests
{
    private sealed class FakeDatagrams : IDatagramChannel
    {
        private readonly Queue<byte[]> _responses;
        public List<byte[]> Sent { get; } = [];
        public FakeDatagrams(IEnumerable<byte[]> responses) => _responses = new Queue<byte[]>(responses);

        public Task SendAsync(byte[] data, CancellationToken cancellationToken) { Sent.Add(data); return Task.CompletedTask; }

        public Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => _responses.Count > 0 ? Task.FromResult(_responses.Dequeue())
                : throw new CommunicationTimeoutException("keine weitere Antwort", []);
    }

    private static byte[] Announcement(string vin, ushort address)
    {
        var payload = new byte[33];
        Encoding.ASCII.GetBytes(vin.PadRight(17)).AsSpan(0, 17).CopyTo(payload);
        payload[17] = (byte)(address >> 8);
        payload[18] = (byte)address;
        return DoIpTransport.Frame(0x02, DoIpDiscovery.VehicleAnnouncement, payload);
    }

    [Fact]
    public async Task DoIP_Fahrzeugsuche_sammelt_Antworten_je_Adresse_einmal()
    {
        var channel = new FakeDatagrams([Announcement("WVWZZZ1JZXW000001", 0x1000), Announcement("WVWZZZ1JZXW000002", 0x1001), Announcement("WVWZZZ1JZXW000001", 0x1000)]);
        var vehicles = await DoIpDiscovery.DiscoverAsync(channel, TimeSpan.FromMilliseconds(200), 0x02, default).Within();
        Assert.Equal(2, vehicles.Count);
        Assert.Equal((0x1000, "WVWZZZ1JZXW000001"), (vehicles[0].LogicalAddress, vehicles[0].Vin));
        Assert.Single(channel.Sent);                                   // genau eine Anfrage
        Assert.Equal(DoIpDiscovery.VehicleIdentificationRequest, (ushort)(channel.Sent[0][2] << 8 | channel.Sent[0][3]));
    }

    [Fact]
    public async Task DoIP_Fahrzeugsuche_ueber_echtes_UDP_Loopback()
    {
        using var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;
        var serve = Task.Run(async () =>
        {
            var req = await responder.ReceiveAsync();
            await responder.SendAsync(Announcement("VIN0000000000000X", 0x1000), Announcement("VIN0000000000000X", 0x1000).Length, req.RemoteEndPoint);
        });
        using var channel = new UdpDatagramChannel("127.0.0.1", port);
        var vehicles = await DoIpDiscovery.DiscoverAsync(channel, TimeSpan.FromSeconds(2), 0x02, default).Within();
        await serve;
        Assert.Single(vehicles);
        Assert.Equal(0x1000, vehicles[0].LogicalAddress);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    [Fact]
    public async Task TCP_Client_mit_TLS_gegen_Selbstzertifikat()
    {
        using var cert = SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(cert);
            var buffer = new byte[64];
            var n = await ssl.ReadAsync(buffer);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes("OK\r\n"));
            _ = n;
        });

        // TLS-Prüfung aus (Selbstzertifikat) → Handschlag und Austausch funktionieren
        using var device = (StreamDevice)InstrumentCatalog.Create(StreamDevice.TcpAdapter, "", $"127.0.0.1:{port}",
            new Dictionary<string, string> { ["TLS"] = "ja", ["TLS-Prüfung"] = "aus" });
        await device.SendAsync("", "PING\r\n"u8.ToArray(), default);
        var response = await device.ReceiveAsync("", new ReceiveRule([13, 10], null, TimeSpan.FromSeconds(2)), default);
        Assert.Equal("OK", CommText.Raw(response));
        Assert.Contains("TLS", device.Name);
        await serve;
        listener.Stop();
    }

    [Fact]
    public async Task TLS_Pruefung_lehnt_unbekanntes_Zertifikat_ab()
    {
        using var cert = SelfSigned();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(cert);
            }
            catch (Exception) { /* Handschlag scheitert clientseitig */ }
        });
        using var device = (StreamDevice)InstrumentCatalog.Create(StreamDevice.TcpAdapter, "", $"127.0.0.1:{port}",
            new Dictionary<string, string> { ["TLS"] = "ja" });   // Prüfung an, keine passende CA
        await Assert.ThrowsAsync<CommunicationException>(() => device.SendAsync("", "x"u8.ToArray(), default));
        await serve;
        listener.Stop();
    }
}
