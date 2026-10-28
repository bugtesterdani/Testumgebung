using System.Net;
using System.Net.Sockets;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class NetworkConnectionTests
{
    private static StepDefinition Step(string id, IStepAction action) => new(id, id, action);
    private static StepDefinition S(string type, string id, params (string Key, string Value)[] p)
        => BlockCatalog.Default.Create(type, id, id, p.ToDictionary(x => x.Key, x => x.Value));

    [Fact]
    public async Task COM_Port_wird_bei_jedem_Schritt_neu_aus_Variable_aufgeloest()
    {
        var device = new RecordingConnection();
        var loop = Group("loop").Var("ComPort", VarType.String, "COM3").Add(
            S(CommunicationBlocks.Send, "first", ("Ressource", "DUT"), ("Adresse", "{ComPort}"), ("Daten", "A")),
            Step("change", new DelegateAction(ctx => { ctx.Set("ComPort", "COM7"); return Task.FromResult(StepOutcome.Unrated()); })),
            S(CommunicationBlocks.Send, "second", ("Ressource", "DUT"), ("Adresse", "{ComPort}"), ("Daten", "B")),
            S(NetworkBlocks.Configure, "configure", ("Ressource", "DUT"), ("Adresse", "COM9"), ("Verbindungsoptionen", "Baud=115200")));
        var run = new TestRun(Test(loop), new TestRunOptions { Hardware = new HardwareRegistry().Bind("DUT", device, "") });
        await run.RunAsync().Within();
        Assert.DoesNotContain(run.Events.OfType<StepCompleted>(), s => s.Rating == ResultRating.Error);
        Assert.Equal(new[] { "COM3", "COM7" }, device.SentTo);
        Assert.Equal("COM9", device.Endpoint);
        Assert.Equal("115200", device.Options["Baud"]);
    }

    [Fact]
    public async Task Echter_Serialadapter_kann_ohne_Oeffnen_umgestellt_werden_und_prueft_Optionen()
    {
        using var device = new StreamDevice(StreamDevice.SerialAdapter, "COM3", new InstrumentOptions());
        await device.ConfigureAsync("COM7", new Dictionary<string, string> { ["Baud"] = "115200" }, default);
        Assert.Equal("COM7", device.Resource);
        Assert.Contains("115200", device.Name);
        await Assert.ThrowsAsync<InstrumentException>(() => device.ConfigureAsync("COM9", new Dictionary<string, string> { ["Datenbits"] = "12" }, default));
        Assert.Equal("COM7", device.Resource); // Fehler erhält bisherige Konfiguration.
    }

    [Fact]
    public async Task TCP_Zielwechsel_verbindet_zur_neuen_Gegenstelle()
    {
        await using var first = new LineServer(_ => "FIRST\r\n");
        await using var second = new LineServer(_ => "SECOND\r\n");
        using var device = new StreamDevice(StreamDevice.TcpAdapter, $"127.0.0.1:{first.Port}", new InstrumentOptions());
        await device.SendAsync("", "test\r\n"u8.ToArray(), default);
        var rule = new ReceiveRule([13, 10], null, TimeSpan.FromSeconds(2));
        Assert.Equal("FIRST", CommText.Raw(await device.ReceiveAsync("", rule, default)));
        await device.ConfigureAsync($"127.0.0.1:{second.Port}", new Dictionary<string, string>(), default);
        await device.SendAsync("", "test\r\n"u8.ToArray(), default);
        Assert.Equal("SECOND", CommText.Raw(await device.ReceiveAsync("", rule, default)));
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    public async Task Netzwerkfunktionen_sind_auch_in_CodeBlocks_nutzbar(string language)
    {
        var device = new RecordingConnection();
        var source = language == "C#" ? """
            ConfigureConnection("DUT", "COM7");
            var http = HttpRequest("DUT", "/status");
            var ssh = SshExecute("DUT", "version");
            SftpUpload("DUT", "local", "/remote");
            SftpDownload("DUT", "/remote", "local");
            return http["code"].GetValue<int>() == 200 && ssh["code"].GetValue<int>() == 0 && ssh["hostKeySHA256"].GetValue<string>() == "SHA256:test" && ssh["hostKeyBase64"].GetValue<string>() == "AQID" ? Pass() : Fail();
            """ : """
            configure_connection("DUT", "COM7")
            http = http_request("DUT", "/status")
            ssh = ssh_execute("DUT", "version")
            sftp_upload("DUT", "local", "/remote")
            sftp_download("DUT", "/remote", "local")
            return Pass() if http["code"] == 200 and ssh["code"] == 0 and ssh["hostKeySHA256"] == "SHA256:test" and ssh["hostKeyBase64"] == "AQID" else Fail()
            """;
        var loop = Group("loop").Add(S(BlockCatalog.Code, "code", ("Sprache", language), ("Quelltext", source)));
        var run = new TestRun(Test(loop), new TestRunOptions { Hardware = new HardwareRegistry().Bind("DUT", device, ""), CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        await run.RunAsync().Within();
        var result = run.Steps("code").Single();
        Assert.True(result.Rating == ResultRating.Pass, result.Message);
        Assert.Equal("COM7", device.Endpoint);
        Assert.Equal(new[] { "http", "ssh", "sftp.upload", "sftp.download" }, device.Operations);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task HTTP_Baustein_sendet_Methode_Header_Body_und_speichert_Antwort_Status(string method)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
                Assert.Equal(method + " /api/123 HTTP/1.1", await reader.ReadLineAsync());
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync() is { Length: > 0 } line)
                { var colon = line.IndexOf(':'); headers[line[..colon]] = line[(colon + 1)..].Trim(); }
                Assert.Equal("abc", headers["X-Test"]);
                var body = new char[int.Parse(headers.GetValueOrDefault("Content-Length", "0"))];
                await reader.ReadBlockAsync(body);
                Assert.Equal("{\"id\":123}", new string(body));
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 201 Created\r\nContent-Length: 2\r\nConnection: close\r\n\r\n" + (method == "HEAD" ? "" : "OK")));
            });
            using var device = new RemoteDevice(RemoteDevice.HttpAdapter, "http://127.0.0.1:1", new InstrumentOptions());
            var loop = Group("loop").Var("Host", VarType.String, "127.0.0.1").Var("Port", VarType.Int, (long)port)
                .Var("Id", VarType.Int, 123L).Var("Antwort", VarType.String).Var("Status", VarType.Int).Add(
                    S(NetworkBlocks.Http, "request", ("Ressource", "HTTP"), ("Adresse", "http://{Host}:{Port}"),
                        ("Methode", method), ("Pfad", "/api/{Id}"), ("Header", "X-Test: abc\nContent-Type: application/json"),
                        ("Body", "{{\"id\":{Id}}}"), ("Ziel", "Antwort"), ("StatusZiel", "Status")),
                    Step("verify", new DelegateAction(ctx =>
                    {
                        Assert.Equal(method == "HEAD" ? "" : "OK", ctx.Get("Antwort"));
                        Assert.Equal(201L, ctx.Get("Status"));
                        return Task.FromResult(StepOutcome.Pass());
                    })));
            var run = new TestRun(Test(loop), new TestRunOptions { Hardware = new HardwareRegistry().Bind("HTTP", device, "") });
            await run.RunAsync().Within();
            await server.Within();
            Assert.Equal(ResultRating.Pass, run.Steps("request").Single().Rating);
            Assert.Equal(ResultRating.Pass, run.Steps("verify").Single().Rating);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task HTTP_Zeitlimit_und_Abbruch_sind_unterscheidbar()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var device = new RemoteDevice(RemoteDevice.HttpAdapter, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}", new InstrumentOptions());
            await Assert.ThrowsAsync<CommunicationTimeoutException>(() => device.ExecuteAsync(new("http", "/", Timeout: TimeSpan.FromMilliseconds(100)), default));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ExecuteAsync(new("http", "/"), cancelled.Token));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SSH_verweigert_Verbindung_ohne_Hostkey()
    {
        using var device = new RemoteDevice(RemoteDevice.SshAdapter, "127.0.0.1:22", new InstrumentOptions(new Dictionary<string, string> { ["Benutzer"] = "test", ["Passwort"] = "secret" }));
        var error = await Assert.ThrowsAsync<CommunicationException>(() => device.ExecuteAsync(new("ssh", "echo test"), default));
        Assert.Contains("HostKeySHA256", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Telnet_Aushandlung_fragmentiert_und_IAC_Escaping()
    {
        var inner = new MemoryTransport(new byte[][] { [255], [251], [1, 255, 253, 3, 255, 250, 24, 1, 255], [240, 79, 75, 13, 0, 10, 255, 255] });
        using var telnet = new TelnetTransport(inner);
        var output = new byte[64];
        var count = await telnet.ReadAsync(output, TimeSpan.FromSeconds(1), default);
        Assert.Equal(new byte[] { 79, 75, 13, 10, 255 }, output[..count]);
        Assert.Equal(new byte[] { 255, 254, 1, 255, 252, 3 }, inner.Sent.ToArray());
        inner.Sent.Clear();
        await telnet.WriteAsync(new byte[] { 65, 255, 66 }, default);
        Assert.Equal(new byte[] { 65, 255, 255, 66 }, inner.Sent.ToArray());
    }

    private sealed class RecordingConnection : IHardwareAdapter, IConfigurableConnection, IByteStreamAdapter, IRemoteConnection
    {
        public string Name => "test";
        public string Endpoint = "";
        public IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>();
        public List<string> SentTo { get; } = [];
        public List<string> Operations { get; } = [];
        public Task<RemoteResponse> ExecuteAsync(RemoteRequest request, CancellationToken cancellationToken)
        { Operations.Add(request.Operation); return Task.FromResult(new RemoteResponse("OK", request.Operation == "http" ? 200 : 0) { HostKeySHA256 = "SHA256:test", HostKeyBase64 = "AQID" }); }
        public ChannelCapabilities GetCapabilities(string channel) => new("", 0, 0);
        public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ConfigureAsync(string endpoint, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
        { Endpoint = endpoint; Options = options; return Task.CompletedTask; }
        public Task SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { SentTo.Add(Endpoint); return Task.CompletedTask; }
        public Task<byte[]> ReceiveAsync(string channel, ReceiveRule rule, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
        public void DiscardInput(string channel) { }
    }
    private sealed class MemoryTransport(IEnumerable<byte[]> data) : IStreamTransport
    {
        private readonly Queue<byte[]> _chunks = new(data);
        public List<byte> Sent { get; } = [];
        public string Description => "test";
        public bool IsOpen => true;
        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) { Sent.AddRange(data.ToArray()); return Task.CompletedTask; }
        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
        { var bytes = _chunks.Count > 0 ? _chunks.Dequeue() : []; bytes.CopyTo(buffer); return Task.FromResult(bytes.Length); }
        public void DiscardInput() { }
        public void Close() { }
        public void Dispose() { }
    }
}

