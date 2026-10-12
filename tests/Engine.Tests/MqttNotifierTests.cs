using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Etappe 6: MQTT-Meldeweg (P06) gegen einen kleinen Test-Broker im Prozess – unverschlüsselt und mit TLS.</summary>
public sealed class MqttNotifierTests
{
    private static RunNotification N(RunNotificationKind kind, string run = "PT-1", int attempt = 1, string? error = null)
        => new(kind, run, "SN-42", new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(2)), attempt, error);

    [Fact]
    public async Task Meldung_enthaelt_RunId_Kennung_Zeit_Ereignis_QoS0_nicht_retained()
    {
        await using var broker = await TestBroker.StartAsync(user: "tu", password: "geheim");
        await using var notifier = new MqttNotifier(new MqttOptions
        {
            Host = "127.0.0.1", Port = broker.Port, UseTls = false, Username = "tu", Password = "geheim",
            Station = "PP01", Topic = "werk/{station}/{event}",
        });
        var delivered = new TaskCompletionSource<MqttDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.Delivered += d => delivered.TrySetResult(d);

        notifier.Notify(N(RunNotificationKind.StoreFailed, error: "DB weg"));
        var d = await delivered.Task.Within();
        Assert.True(d.Sent, d.Error);
        await WaitUntil(() => broker.Published.Count == 1);

        var msg = Assert.Single(broker.Published);
        Assert.Equal("werk/PP01/store_failed", msg.Topic);
        Assert.Equal(0x30, msg.Header);                                       // PUBLISH, QoS 0, kein Retain
        Assert.Equal(("tu", "geheim", 4), (broker.LastUser, broker.LastPassword, broker.LastProtocolLevel));
        using var json = JsonDocument.Parse(msg.Payload);
        var r = json.RootElement;
        Assert.Equal(("PT-1", "SN-42", "store_failed", "DB weg", "PP01"),
            (r.GetProperty("runId").GetString(), r.GetProperty("dutId").GetString(), r.GetProperty("event").GetString(),
             r.GetProperty("error").GetString(), r.GetProperty("station").GetString()));
        Assert.Equal("2026-10-02T12:00:00.0000000+02:00", r.GetProperty("time").GetString());
        Assert.Contains("+02:00", Encoding.UTF8.GetString(msg.Payload));        // lesbar, ohne \u002B
    }

    [Fact]
    public async Task P06_Sendefehler_keine_persistente_Meldung_und_kein_spaeterer_Retry()
    {
        var port = TestBroker.FreePort();
        var deliveries = new ConcurrentQueue<MqttDelivery>();
        await using var notifier = new MqttNotifier(new MqttOptions { Host = "127.0.0.1", Port = port, UseTls = false, Timeout = TimeSpan.FromSeconds(2) });
        notifier.Delivered += deliveries.Enqueue;

        notifier.Notify(N(RunNotificationKind.StoreFailed, "PT-verloren"));     // Broker nicht da
        await WaitUntil(() => deliveries.Count == 1);
        Assert.False(deliveries.Single().Sent);
        Assert.NotNull(deliveries.Single().Error);

        await using var broker = await TestBroker.StartAsync(port: port);     // Broker wieder da
        notifier.Notify(N(RunNotificationKind.StoredLater, "PT-danach", attempt: 2));
        await WaitUntil(() => deliveries.Count == 2 && broker.Published.Count == 1);
        await Task.Delay(300);                                                   // kein Nachsenden der ersten Meldung

        Assert.Equal(["PT-danach"], broker.Published.Select(p => JsonDocument.Parse(p.Payload).RootElement.GetProperty("runId").GetString()));
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(1, broker.Connections);                                    // genau ein Verbindungsaufbau beim Broker
    }

    [Fact]
    public async Task Abgelehnte_Anmeldung_wird_als_Fehler_gemeldet_Test_laeuft_weiter()
    {
        await using var broker = await TestBroker.StartAsync(user: "tu", password: "richtig");
        var options = new MqttOptions { Host = "127.0.0.1", Port = broker.Port, UseTls = false, Username = "tu", Password = "falsch" };
        var ex = await Assert.ThrowsAsync<MqttException>(() => MqttClientLite.PublishAsync(options, "a/b", [1], default));
        Assert.Contains("Passwort", ex.Message);
        Assert.Empty(broker.Published);

        // Notify blockiert nie und wirft nie – auch bei vollem Puffer
        await using var notifier = new MqttNotifier(options, (_, _, _, ct) => Task.Delay(Timeout.Infinite, ct));
        for (var i = 0; i < 200; i++) notifier.Notify(N(RunNotificationKind.Stored, "PT-" + i));
        Assert.InRange(notifier.Dropped, 100, 200);
    }

    [Fact]
    public async Task TLS_mit_Firmen_CA_und_Pruefung_des_Hostnamens()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tu-mqtt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var (ca, server) = TestBroker.CreateCertificates("localhost");
            var caFile = Path.Combine(dir, "firmen-ca.crt");
            File.WriteAllText(caFile, ca.ExportCertificatePem());
            await using var broker = await TestBroker.StartAsync(certificate: server);

            var ok = new MqttOptions { Host = "localhost", Port = broker.Port, UseTls = true, CaCertificatePath = caFile, Topic = "t/{runId}" };
            await MqttClientLite.PublishAsync(ok, "t/1", "{}"u8.ToArray(), default);
            await WaitUntil(() => broker.Published.Count == 1);
            Assert.Equal("t/1", Assert.Single(broker.Published).Topic);

            // Ohne Firmen-CA: Zertifikat nicht vertrauenswürdig → abgelehnt
            await Assert.ThrowsAsync<MqttException>(() => MqttClientLite.PublishAsync(ok with { CaCertificatePath = null }, "t/2", [1], default));
            // Mit CA, aber falscher Hostname → abgelehnt
            await Assert.ThrowsAsync<MqttException>(() => MqttClientLite.PublishAsync(ok with { Host = "127.0.0.1" }, "t/3", [1], default));
            Assert.Single(broker.Published);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Topic_Platzhalter_werden_ersetzt_und_Wildcards_entschaerft()
    {
        var n = new MqttNotifier(new MqttOptions { Host = "x", Station = "PP/01", Topic = "a/{station}/{event}/{dutId}" }, (_, _, _, _) => Task.CompletedTask);
        Assert.Equal("a/PP_01/stored_later/SN_1_", n.TopicFor(N(RunNotificationKind.StoredLater) with { DutId = "SN+1#" }));
        Assert.Throws<MqttException>(() => MqttClientLite.Publish("a/#", []));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Kit.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Bedingung nicht erreicht.");
            await Task.Delay(10);
        }
    }
}

/// <summary>Minimaler MQTT-3.1.1-Broker für Tests: CONNECT/CONNACK, PUBLISH aufzeichnen, DISCONNECT.</summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly X509Certificate2? _certificate;
    private readonly string? _user;
    private readonly string? _password;
    private int _connections;

    public sealed record Message(byte Header, string Topic, byte[] Payload);

    private TestBroker(TcpListener listener, X509Certificate2? certificate, string? user, string? password)
    {
        _listener = listener;
        _certificate = certificate;
        _user = user;
        _password = password;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ConcurrentQueue<Message> Published { get; } = new();
    public int Connections => Volatile.Read(ref _connections);
    public string? LastUser { get; private set; }
    public string? LastPassword { get; private set; }
    public int LastProtocolLevel { get; private set; }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static Task<TestBroker> StartAsync(int port = 0, X509Certificate2? certificate = null, string? user = null, string? password = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var broker = new TestBroker(listener, certificate, user, password);
        _ = broker.AcceptLoop();
        return Task.FromResult(broker);
    }

    /// <summary>Firmen-CA und davon signiertes Serverzertifikat für <paramref name="host"/>.</summary>
    public static (X509Certificate2 Ca, X509Certificate2 Server) CreateCertificates(string host)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test Firmen-CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3, 4]);
        using var withKey = signed.CopyWithPrivateKey(key);
        // Über PFX laden, damit SslStream den Schlüssel auch unter Windows verwenden kann
        var server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        return (X509CertificateLoader.LoadCertificate(ca.RawData), server);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Handle(client);
            }
        }
        catch (Exception) { /* Ende */ }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream s = client.GetStream();
                if (_certificate is not null)
                {
                    var ssl = new SslStream(s);
                    await ssl.AuthenticateAsServerAsync(_certificate, false, false);
                    s = ssl;
                }
                Interlocked.Increment(ref _connections);
                while (true)
                {
                    var (header, body) = await ReadPacket(s);
                    switch (header >> 4)
                    {
                        case 1: // CONNECT
                            var (level, user, password) = ParseConnect(body);
                            (LastProtocolLevel, LastUser, LastPassword) = (level, user, password);
                            var rc = _user is not null && (user != _user || password != _password) ? (byte)4 : (byte)0;
                            await s.WriteAsync(new byte[] { 0x20, 0x02, 0x00, rc });
                            if (rc != 0) return;
                            break;
                        case 3: // PUBLISH
                            var len = BinaryPrimitives.ReadUInt16BigEndian(body);
                            Published.Enqueue(new Message(header, Encoding.UTF8.GetString(body, 2, len), body[(2 + len)..]));
                            break;
                        case 14: // DISCONNECT
                            return;
                    }
                }
            }
            catch (Exception) { /* Verbindung beendet */ }
        }
    }

    private static async Task<(byte Header, byte[] Body)> ReadPacket(Stream s)
    {
        var one = new byte[1];
        await s.ReadExactlyAsync(one);
        var header = one[0];
        int length = 0, multiplier = 1;
        while (true)
        {
            await s.ReadExactlyAsync(one);
            length += (one[0] & 0x7F) * multiplier;
            if ((one[0] & 0x80) == 0) break;
            multiplier *= 128;
        }
        var body = new byte[length];
        await s.ReadExactlyAsync(body);
        return (header, body);
    }

    private static (int Level, string? User, string? Password) ParseConnect(byte[] body)
    {
        var pos = 0;
        string Str()
        {
            var n = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(pos));
            var text = Encoding.UTF8.GetString(body, pos + 2, n);
            pos += 2 + n;
            return text;
        }
        _ = Str();                     // "MQTT"
        var level = body[pos++];
        var flags = body[pos++];
        pos += 2;                      // Keep-Alive
        _ = Str();                     // Client-ID
        var user = (flags & 0x80) != 0 ? Str() : null;
        var password = (flags & 0x40) != 0 ? Str() : null;
        return (level, user, password);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
    }
}
