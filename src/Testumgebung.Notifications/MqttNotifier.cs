using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Notifications;

/// <summary>Ergebnis eines Sendeversuchs (Anzeige in der Entwickleransicht).</summary>
public sealed record MqttDelivery(RunNotification Notification, string Topic, bool Sent, string? Error, DateTimeOffset Time);

/// <summary>
/// Meldeweg über MQTT (Abschnitt 9.3): Run-ID, Prüflingskennung, Zeitpunkt und Ereignis.
/// Strikt best effort: genau ein begrenzter Sendeversuch je Meldung, QoS 0, nicht retained, keine persistente
/// Warteschlange und keine spätere Wiederholung. Fehler werden gemeldet, beeinflussen den Test aber nie.
/// <see cref="Notify"/> blockiert nicht – Meldungen gehen über einen kleinen Speicherpuffer an einen Sende-Thread.
/// </summary>
public sealed class MqttNotifier : IRunNotifier, IAsyncDisposable
{
    private readonly Channel<RunNotification> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sender;
    private int _dropped;

    public MqttNotifier(MqttOptions options, Func<MqttOptions, string, byte[], CancellationToken, Task>? publish = null)
    {
        Options = options;
        _publish = publish ?? MqttClientLite.PublishAsync;
        _queue = Channel.CreateBounded<RunNotification>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }, _ => Interlocked.Increment(ref _dropped));
        _sender = Task.Run(SendLoopAsync);
    }

    private readonly Func<MqttOptions, string, byte[], CancellationToken, Task> _publish;

    public MqttOptions Options { get; }

    /// <summary>Verworfene Meldungen (Puffer voll) – kein Nachsenden.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    public event Action<MqttDelivery>? Delivered;

    public void Notify(RunNotification notification) => _queue.Writer.TryWrite(notification);

    public static string EventName(RunNotificationKind kind) => kind switch
    {
        RunNotificationKind.Stored => "stored",
        RunNotificationKind.StoreFailed => "store_failed",
        RunNotificationKind.RetryFailed => "retry_failed",
        RunNotificationKind.StoredLater => "stored_later",
        RunNotificationKind.OutboxDamaged => "outbox_damaged",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public string TopicFor(RunNotification n) => Options.Topic
        .Replace("{station}", Sanitize(Options.Station), StringComparison.Ordinal)
        .Replace("{event}", EventName(n.Kind), StringComparison.Ordinal)
        .Replace("{runId}", Sanitize(n.RunId), StringComparison.Ordinal)
        .Replace("{dutId}", Sanitize(n.DutId), StringComparison.Ordinal);

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public byte[] PayloadFor(RunNotification n) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        runId = n.RunId,
        dutId = n.DutId,
        @event = EventName(n.Kind),
        time = n.Time.ToString("O", CultureInfo.InvariantCulture),
        attempt = n.Attempt,
        error = n.Error,
        station = Options.Station,
        application = "Testumgebung",
    }, Json);

    /// <summary>Testnachricht (Bedienknopf „MQTT testen“) – direkt, ohne Puffer.</summary>
    public async Task<string> SendTestAsync(CancellationToken cancellationToken)
    {
        var n = new RunNotification(RunNotificationKind.Stored, "TEST-" + Guid.NewGuid().ToString("N")[..8], "Testmeldung", DateTimeOffset.Now, 0, null);
        var topic = TopicFor(n);
        await _publish(Options, topic, PayloadFor(n), cancellationToken).ConfigureAwait(false);
        return $"Testmeldung an {Options.Host}:{Options.Port} gesendet (Topic {topic}).";
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var n in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var topic = TopicFor(n);
                string? error = null;
                try
                {
                    await _publish(Options, topic, PayloadFor(n), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    error = ex.Message; // verworfen – keine Wiederholung (P06)
                }
                try { Delivered?.Invoke(new MqttDelivery(n, topic, error is null, error, DateTimeOffset.Now)); }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_sender, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _cts.Dispose();
    }

    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? "_" : new string(s.Select(c => c is '+' or '#' or '/' || char.IsControl(c) ? '_' : c).ToArray());
}
