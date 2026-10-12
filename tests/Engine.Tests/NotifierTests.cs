using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>Weitere Meldewege (offene Punkte ADR 0005): HTTP-Webhook und das Verteilen an mehrere Wege.</summary>
public sealed class NotifierTests
{
    private static RunNotification N(RunNotificationKind kind = RunNotificationKind.Stored, string? error = null)
        => new(kind, "PT-7", "SN-99", new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(2)), 1, error);

    /// <summary>Kleiner HTTP-Server im Prozess, der POST-Nachrichten einsammelt und frei antwortet.</summary>
    private sealed class TestWebhook : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        public ConcurrentQueue<(string Path, string Body, string? Header)> Received { get; } = new();
        public int StatusCode { get; set; } = 200;
        public string Prefix { get; }

        private TestWebhook(int port)
        {
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public static TestWebhook Start()
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return new TestWebhook(FreePort()); }
                catch (HttpListenerException) when (attempt < 5) { }
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                Received.Enqueue((ctx.Request.Url!.AbsolutePath, body, ctx.Request.Headers["X-Api-Key"]));
                ctx.Response.StatusCode = StatusCode;
                ctx.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try { await _loop; } catch (Exception) { }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "Bedingung nicht erfüllt");
    }

    [Fact]
    public async Task HTTP_POST_mit_JSON_Platzhaltern_und_Kopfzeile()
    {
        await using var server = TestWebhook.Start();
        await using var notifier = new HttpNotifier(new HttpNotifyOptions
        {
            Url = server.Prefix + "runs/{station}/{event}",
            HeaderName = "X-Api-Key",
            HeaderValue = "geheim-123",
            Station = "PP01",
        });
        var delivered = new TaskCompletionSource<NotifyDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.Delivered += d => delivered.TrySetResult(d);

        notifier.Notify(N(RunNotificationKind.StoreFailed, "DB weg"));
        var d = await delivered.Task.Within();
        Assert.True(d.Sent, d.Error);
        await WaitUntil(() => server.Received.Count == 1);

        var (path, body, header) = server.Received.Single();
        Assert.Equal("/runs/PP01/store_failed", path);
        Assert.Equal("geheim-123", header);
        using var json = JsonDocument.Parse(body);
        var r = json.RootElement;
        Assert.Equal(("PT-7", "SN-99", "store_failed", "DB weg", "PP01"),
            (r.GetProperty("runId").GetString(), r.GetProperty("dutId").GetString(), r.GetProperty("event").GetString(),
             r.GetProperty("error").GetString(), r.GetProperty("station").GetString()));
    }

    [Fact]
    public async Task HTTP_Fehlerstatus_wird_gemeldet_aber_nicht_wiederholt()
    {
        await using var server = TestWebhook.Start();
        server.StatusCode = 500;
        await using var notifier = new HttpNotifier(new HttpNotifyOptions { Url = server.Prefix + "hook", Station = "PP01" });
        var delivered = new TaskCompletionSource<NotifyDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        notifier.Delivered += d => delivered.TrySetResult(d);

        notifier.Notify(N());
        var d = await delivered.Task.Within();
        Assert.False(d.Sent);
        Assert.Contains("500", d.Error);
        await WaitUntil(() => server.Received.Count == 1);
        await Task.Delay(100);
        Assert.Single(server.Received);   // keine Wiederholung (best effort)
    }

    [Fact]
    public async Task HTTP_Testmeldung_direkt()
    {
        await using var server = TestWebhook.Start();
        await using var notifier = new HttpNotifier(new HttpNotifyOptions { Url = server.Prefix + "test", Station = "PP01" });
        var text = await notifier.SendTestAsync(CancellationToken.None).Within();
        Assert.Contains("HTTP", text);
        await WaitUntil(() => server.Received.Count == 1);
    }

    [Fact]
    public void HTTP_Optionen_werden_geprueft()
    {
        Assert.Null(new HttpNotifyOptions { Url = "https://mes/hook" }.Validate());
        Assert.Contains("http", new HttpNotifyOptions { Url = "mes/hook" }.Validate());
        Assert.Contains("Doppelpunkt", new HttpNotifyOptions { Url = "https://x", HeaderName = "A: B" }.Validate()!);
    }

    [Fact]
    public async Task Mehrere_Meldewege_erhalten_jede_Meldung_unabhaengig()
    {
        await using var a = TestWebhook.Start();
        await using var b = TestWebhook.Start();
        b.StatusCode = 500; // ein Weg scheitert – der andere bekommt die Meldung trotzdem
        await using var na = new HttpNotifier(new HttpNotifyOptions { Url = a.Prefix + "a", Station = "P" });
        await using var nb = new HttpNotifier(new HttpNotifyOptions { Url = b.Prefix + "b", Station = "P" });
        var composite = new CompositeNotifier([na, nb]);

        composite.Notify(N());
        await WaitUntil(() => a.Received.Count == 1 && b.Received.Count == 1);
        Assert.Single(a.Received);
        Assert.Single(b.Received);
    }

    [Fact]
    public void OPC_UA_Optionen_und_Knotennamen()
    {
        var o = new OpcUaNotifyOptions { EndpointUrl = "opc.tcp://server:4840", NodePrefix = "ns=2;s=PP01." };
        Assert.Null(o.Validate());
        Assert.Equal("ns=2;s=PP01.RunId", o.NodeFor("RunId"));
        Assert.Contains("opc.tcp", new OpcUaNotifyOptions { EndpointUrl = "http://x" }.Validate());
        Assert.Contains("NodeId", new OpcUaNotifyOptions { EndpointUrl = "opc.tcp://x", NodePrefix = "PP01." }.Validate());
        Assert.Equal(["RunId", "DutId", "Event", "Time", "Attempt", "Error", "Station"], OpcUaNotifyOptions.FieldNames);
    }
}
