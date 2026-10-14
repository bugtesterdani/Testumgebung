/**
 * @file
 * @brief Vereinheitlicht Meldungsdaten und best-effort-Meldewege, getrennt von dauerhafter Ergebnisspeicherung.
 */

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Notifications;

/// <summary>Ergebnis eines Sendeversuchs eines weiteren Meldewegs (HTTP, OPC UA) für die Anzeige.</summary>
public sealed record NotifyDelivery(string Channel, RunNotification Notification, string Target, bool Sent, string? Error, DateTimeOffset Time);

/// <summary>Gemeinsamer Inhalt aller Meldewege (gleich wie MQTT).</summary>
public static class NotificationPayload
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IReadOnlyDictionary<string, string> Fields(RunNotification n, string station) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RunId"] = n.RunId,
        ["DutId"] = n.DutId,
        ["Event"] = MqttNotifier.EventName(n.Kind),
        ["Time"] = n.Time.ToString("O", CultureInfo.InvariantCulture),
        ["Attempt"] = n.Attempt.ToString(CultureInfo.InvariantCulture),
        ["Error"] = n.Error ?? "",
        ["Station"] = station,
    };

    public static byte[] JsonBytes(RunNotification n, string station) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        runId = n.RunId,
        dutId = n.DutId,
        @event = MqttNotifier.EventName(n.Kind),
        time = n.Time.ToString("O", CultureInfo.InvariantCulture),
        attempt = n.Attempt,
        error = n.Error,
        station,
        application = "Testumgebung",
    }, Json);

    /// <summary>Platzhalter {station}, {event}, {runId}, {dutId} (URL-kodiert bzw. bereinigt).</summary>
    public static string Expand(string template, RunNotification n, string station, Func<string, string> encode)
        => template.Replace("{station}", encode(station), StringComparison.Ordinal)
            .Replace("{event}", encode(MqttNotifier.EventName(n.Kind)), StringComparison.Ordinal)
            .Replace("{runId}", encode(n.RunId), StringComparison.Ordinal)
            .Replace("{dutId}", encode(n.DutId), StringComparison.Ordinal);

    public static RunNotification TestNotification()
        => new(RunNotificationKind.Stored, "TEST-" + Guid.NewGuid().ToString("N")[..8], "Testmeldung", DateTimeOffset.Now, 0, null);
}

/// <summary>
/// Basis für best-effort-Meldewege: <see cref="Notify"/> blockiert nie; ein Sende-Thread arbeitet einen kleinen Puffer ab,
/// genau ein begrenzter Versuch je Meldung, keine Wiederholung und keine persistente Warteschlange (wie MQTT, P06).
/// </summary>
public abstract class QueuedNotifier : IRunNotifier, IAsyncDisposable
{
    private readonly Channel<RunNotification> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sender;
    private int _dropped;

    protected QueuedNotifier(TimeSpan timeout)
    {
        Timeout = timeout;
        _queue = System.Threading.Channels.Channel.CreateBounded<RunNotification>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }, _ => Interlocked.Increment(ref _dropped));
        _sender = Task.Run(SendLoopAsync);
    }

    public abstract string Channel { get; }
    protected TimeSpan Timeout { get; }
    public int Dropped => Volatile.Read(ref _dropped);
    public event Action<NotifyDelivery>? Delivered;

    public void Notify(RunNotification notification) => _queue.Writer.TryWrite(notification);

    /// <summary>Anzeigeziel, z. B. URL oder Knoten.</summary>
    public abstract string TargetFor(RunNotification n);

    protected abstract Task SendAsync(RunNotification n, CancellationToken cancellationToken);

    /// <summary>Testmeldung direkt (Knopf „testen“); Ausnahme bei Fehler.</summary>
    public async Task<string> SendTestAsync(CancellationToken cancellationToken)
    {
        var n = NotificationPayload.TestNotification();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        await SendAsync(n, cts.Token).ConfigureAwait(false);
        return $"Testmeldung gesendet ({Channel} → {TargetFor(n)}).";
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var n in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                string? error = null;
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    attempt.CancelAfter(Timeout);
                    await SendAsync(n, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    error = $"Zeitüberschreitung nach {Timeout.TotalSeconds:0.#} s";
                }
                catch (Exception ex)
                {
                    error = ex.Message; // verworfen – keine Wiederholung
                }
                try { Delivered?.Invoke(new NotifyDelivery(Channel, n, TargetFor(n), error is null, error, DateTimeOffset.Now)); }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    public virtual async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_sender, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}

// ---------------------------------------------------------------------- HTTP

public sealed record HttpNotifyOptions
{
    /// <summary>Ziel (http/https). Platzhalter: {station}, {event}, {runId}, {dutId}.</summary>
    public string Url { get; init; } = "";

    /// <summary>Optionaler Kopf, z. B. „Authorization“ mit „Bearer …“ oder „X-Api-Key“.</summary>
    public string? HeaderName { get; init; }
    public string? HeaderValue { get; init; }
    public string Station { get; init; } = Environment.MachineName;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public string? Validate()
    {
        if (!Uri.TryCreate(Url.Replace("{", "x").Replace("}", "x"), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            return "URL muss mit http:// oder https:// beginnen.";
        if (!string.IsNullOrWhiteSpace(HeaderName) && (HeaderName.Contains(':') || HeaderName.Contains(' ')))
            return "Kopfname ohne Doppelpunkt und Leerzeichen angeben (z. B. Authorization).";
        return null;
    }
}

/// <summary>
/// Meldeweg per HTTP: je Meldung ein POST mit JSON (gleicher Inhalt wie MQTT) an einen Webhook, z. B. MES, Leitstand oder
/// Teams/Power-Automate. Erfolg = Statuscode 2xx.
/// </summary>
public sealed class HttpNotifier : QueuedNotifier
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpNotifier(HttpNotifyOptions options, HttpClient? client = null) : base(options.Timeout)
    {
        Options = options;
        _ownsClient = client is null;
        _client = client ?? new HttpClient();
    }

    public HttpNotifyOptions Options { get; }
    public override string Channel => "HTTP";

    public override string TargetFor(RunNotification n) => NotificationPayload.Expand(Options.Url, n, Options.Station, Uri.EscapeDataString);

    protected override async Task SendAsync(RunNotification n, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TargetFor(n))
        {
            Content = new ByteArrayContent(NotificationPayload.JsonBytes(n, Options.Station)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.UserAgent.ParseAdd("Testumgebung/1.0");
        if (!string.IsNullOrWhiteSpace(Options.HeaderName) && Options.HeaderValue is not null)
            request.Headers.TryAddWithoutValidation(Options.HeaderName.Trim(), Options.HeaderValue);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{(body.Length > 0 ? ": " + body[..Math.Min(body.Length, 200)] : "")}");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        if (_ownsClient) _client.Dispose();
    }
}

// ---------------------------------------------------------------------- OPC UA (Umsetzung in der Windows-Anwendung)

public sealed record OpcUaNotifyOptions
{
    /// <summary>Endpunkt des OPC-UA-Servers, z. B. opc.tcp://mes-server:4840.</summary>
    public string EndpointUrl { get; init; } = "";

    /// <summary>Präfix der Zielknoten, z. B. „ns=2;s=Pruefplatz1.“ → Knoten ns=2;s=Pruefplatz1.RunId, …DutId, …Event, …Time, …Error, …Station.</summary>
    public string NodePrefix { get; init; } = "ns=2;s=Testumgebung.";

    /// <summary>Verschlüsselt (Sign &amp; Encrypt, beste Richtlinie des Servers) statt ohne Sicherheit.</summary>
    public bool UseSecurity { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }
    public string Station { get; init; } = Environment.MachineName;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Feldnamen, die als Zeichenkette in je einen Knoten geschrieben werden.</summary>
    public static readonly string[] FieldNames = ["RunId", "DutId", "Event", "Time", "Attempt", "Error", "Station"];

    public string? Validate()
    {
        if (!EndpointUrl.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)) return "Endpunkt muss mit opc.tcp:// beginnen.";
        if (!NodePrefix.Contains("s=", StringComparison.Ordinal) && !NodePrefix.Contains("i=", StringComparison.Ordinal))
            return "Knotenpräfix im NodeId-Format angeben, z. B. ns=2;s=Pruefplatz1.";
        return null;
    }

    public string NodeFor(string field) => NodePrefix + field;
}

/// <summary>Meldungen an alle konfigurierten Wege verteilen (Fehler eines Weges beeinflussen die anderen nicht).</summary>
public sealed class CompositeNotifier(IReadOnlyList<IRunNotifier> targets) : IRunNotifier
{
    public IReadOnlyList<IRunNotifier> Targets { get; } = targets;

    public void Notify(RunNotification notification)
    {
        foreach (var t in Targets)
        {
            try { t.Notify(notification); }
            catch (Exception) { /* best effort */ }
        }
    }
}
