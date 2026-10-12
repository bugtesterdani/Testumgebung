using System.Data.Common;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Presentation.Settings;

/// <summary>Liefert die ADO.NET-Treiber (in der Anwendung über NuGet eingebunden, in Tests ersetzbar).</summary>
public interface IDatabaseProviderRegistry
{
    /// <summary>null = Treiber nicht verfügbar.</summary>
    DbProviderFactory? Factory(DatabaseProvider provider);
}

/// <summary>
/// Speicherdienste der Anwendung: Ergebnisdateien, Outbox, Datenbank und Meldeweg. Einstellungen werden ohne
/// Neustart übernommen; offene Outbox-Einträge bleiben erhalten und gehen an die neu eingestellte Datenbank.
/// </summary>
public sealed class ResultServices : IAsyncDisposable
{
    private readonly IDatabaseProviderRegistry _registry;
    private MqttNotifier? _mqtt;
    private HttpNotifier? _http;
    private QueuedNotifier? _opcUa;

    /// <summary>
    /// Erzeugt den OPC-UA-Meldeweg (in der Windows-Anwendung mit dem OPC-Foundation-Stack gesetzt). Null = in dieser Ausgabe
    /// nicht verfügbar.
    /// </summary>
    public static Func<OpcUaNotifyOptions, QueuedNotifier>? OpcUaFactory { get; set; }

    public ResultServices(AppSettings settings, IDatabaseProviderRegistry registry, string? outboxFolder = null,
        string? simulatedDatabaseFolder = null, IClock? clock = null, string? recoveryFolder = null, string? releaseFolder = null,
        string? journalFolder = null)
    {
        _registry = registry;
        ReleaseFolder = releaseFolder ?? FolderReleaseStore.DefaultFolder;
        ReleaseStore = new SwitchableReleaseStore(new FolderReleaseStore(ReleaseFolder));
        SimulatedDatabaseFolder = simulatedDatabaseFolder ?? FolderRunDatabase.DefaultFolder;
        RecoveryFolder = recoveryFolder ?? ResultFileStore.DefaultRecoveryFolder;
        Outbox = new OutboxService(outboxFolder ?? OutboxService.DefaultFolder, Database, clock, notifier: Notifier);
        Pipeline = new ResultPipeline(null, Outbox)
        {
            // Zwischenaufzeichnung: in Tests neben dem angegebenen Wiederherstellungsordner, sonst im Benutzerprofil.
            Journal = new RunJournal(journalFolder ?? (recoveryFolder is not null ? Path.Combine(recoveryFolder, "Zwischenaufzeichnung") : RunJournal.DefaultFolder)),
        };
        Apply(settings);
    }

    public SwitchableRunDatabase Database { get; } = new();
    public SwitchableNotifier Notifier { get; } = new();
    public OutboxService Outbox { get; }
    public ResultPipeline Pipeline { get; }
    public string SimulatedDatabaseFolder { get; }
    public string RecoveryFolder { get; }

    /// <summary>Release-Pakete und Audit-Log: in der Prüfdatenbank, ohne SQL-Datenbank im Ordner <see cref="ReleaseFolder"/>.</summary>
    public SwitchableReleaseStore ReleaseStore { get; }

    public string ReleaseFolder { get; }
    public AppSettings Current { get; private set; } = new();

    /// <summary>Hinweis zur letzten Übernahme (z. B. fehlender Treiber).</summary>
    public string? ApplyWarning { get; private set; }

    public MqttNotifier? Mqtt => _mqtt;
    public HttpNotifier? Http => _http;
    public QueuedNotifier? OpcUa => _opcUa;

    /// <summary>Sendeergebnisse von HTTP und OPC UA (für die Anzeige).</summary>
    public event Action<NotifyDelivery>? NotifyDelivered;

    /// <summary>MQTT-Sendeergebnisse (für die Anzeige).</summary>
    public event Action<MqttDelivery>? MqttDelivered;

    public event Action? Applied;

    public void Apply(AppSettings settings)
    {
        Current = settings;
        ApplyWarning = null;
        Pipeline.Files = new ResultFileStore(string.IsNullOrWhiteSpace(settings.ResultFolder) ? ResultFileStore.DefaultFolder : settings.ResultFolder, RecoveryFolder);

        var o = settings.Outbox;
        Outbox.Options = new OutboxOptions
        {
            RetryInterval = TimeSpan.FromSeconds(Math.Max(1, o.RetryIntervalSeconds)),
            BackoffFactor = Math.Max(1, o.BackoffFactor),
            MaxRetryInterval = TimeSpan.FromMinutes(Math.Max(0.1, o.MaxRetryMinutes)),
            AttemptTimeout = TimeSpan.FromSeconds(Math.Max(1, o.AttemptTimeoutSeconds)),
        };

        Database.Inner = CreateDatabase(settings.Database, out var warning);
        ReleaseStore.Inner = Database.Inner is SqlRunDatabase sql ? new SqlReleaseStore(sql) : new FolderReleaseStore(ReleaseFolder);
        ApplyWarning = warning;
        Pipeline.DatabaseEnabled = settings.Database.Provider != DatabaseChoice.None;

        var old = _mqtt;
        _mqtt = null;
        if (settings.Mqtt.Enabled && !string.IsNullOrWhiteSpace(settings.Mqtt.Host))
        {
            _mqtt = new MqttNotifier(MqttOptionsOf(settings.Mqtt));
            _mqtt.Delivered += d => MqttDelivered?.Invoke(d);
        }
        var oldHttp = _http;
        _http = null;
        if (settings.Http.Enabled && !string.IsNullOrWhiteSpace(settings.Http.Url))
        {
            var options = HttpOptionsOf(settings.Http);
            if (options.Validate() is { } error) ApplyWarning = Join(ApplyWarning, "HTTP: " + error);
            else
            {
                _http = new HttpNotifier(options);
                _http.Delivered += d => NotifyDelivered?.Invoke(d);
            }
        }
        var oldOpc = _opcUa;
        _opcUa = null;
        if (settings.OpcUa.Enabled && !string.IsNullOrWhiteSpace(settings.OpcUa.EndpointUrl))
        {
            var options = OpcUaOptionsOf(settings.OpcUa);
            if (options.Validate() is { } error) ApplyWarning = Join(ApplyWarning, "OPC UA: " + error);
            else if (OpcUaFactory is null) ApplyWarning = Join(ApplyWarning, "OPC UA ist in dieser Ausgabe nicht verfügbar.");
            else
            {
                _opcUa = OpcUaFactory(options);
                _opcUa.Delivered += d => NotifyDelivered?.Invoke(d);
            }
        }
        var targets = new List<IRunNotifier>();
        if (_mqtt is not null) targets.Add(_mqtt);
        if (_http is not null) targets.Add(_http);
        if (_opcUa is not null) targets.Add(_opcUa);
        Notifier.Inner = targets.Count switch { 0 => null, 1 => targets[0], _ => new CompositeNotifier(targets) };
        if (old is not null) _ = old.DisposeAsync().AsTask();
        if (oldHttp is not null) _ = oldHttp.DisposeAsync().AsTask();
        if (oldOpc is not null) _ = oldOpc.DisposeAsync().AsTask();
        Applied?.Invoke();
    }

    public IRunDatabase? CreateDatabase(DatabaseSettings db, out string? warning)
    {
        warning = null;
        if (db.Provider == DatabaseChoice.None) return null;
        if (db.Provider == DatabaseChoice.Simulated) return new FolderRunDatabase(SimulatedDatabaseFolder);
        var provider = DatabaseSettings.ProviderOf(db.Provider)!.Value;
        var factory = _registry.Factory(provider);
        if (factory is null)
        {
            warning = $"Treiber für {SqlDialect.For(provider).DisplayName} nicht verfügbar.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(db.ConnectionString))
        {
            warning = "Kein Verbindungsstring angegeben.";
            return null;
        }
        return new SqlRunDatabase(factory, db.ConnectionString, SqlDialect.For(provider));
    }

    private static string Join(string? a, string b) => a is null ? b : a + " " + b;

    public static HttpNotifyOptions HttpOptionsOf(HttpNotifySettings h) => new()
    {
        Url = h.Url.Trim(),
        HeaderName = string.IsNullOrWhiteSpace(h.HeaderName) ? null : h.HeaderName.Trim(),
        HeaderValue = h.HeaderValue,
        Timeout = TimeSpan.FromSeconds(Math.Clamp(h.TimeoutSeconds, 1, 60)),
    };

    public static OpcUaNotifyOptions OpcUaOptionsOf(OpcUaSettings o) => new()
    {
        EndpointUrl = o.EndpointUrl.Trim(),
        NodePrefix = o.NodePrefix.Trim(),
        UseSecurity = o.UseSecurity,
        Username = string.IsNullOrWhiteSpace(o.Username) ? null : o.Username.Trim(),
        Password = o.Password,
        Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 60)),
    };

    public static MqttOptions MqttOptionsOf(MqttSettings m) => new()
    {
        Host = m.Host.Trim(),
        Port = m.Port,
        UseTls = m.UseTls,
        Username = string.IsNullOrWhiteSpace(m.Username) ? null : m.Username,
        Password = m.Password,
        ClientId = string.IsNullOrWhiteSpace(m.ClientId) ? "testumgebung-" + Environment.MachineName.ToLowerInvariant() : m.ClientId,
        Topic = string.IsNullOrWhiteSpace(m.Topic) ? "testumgebung/{station}/runs" : m.Topic,
        CaCertificatePath = string.IsNullOrWhiteSpace(m.CaCertificatePath) ? null : m.CaCertificatePath,
        Timeout = TimeSpan.FromSeconds(Math.Clamp(m.TimeoutSeconds, 1, 60)),
    };

    public async ValueTask DisposeAsync()
    {
        await Outbox.StopAsync().ConfigureAwait(false);
        if (_mqtt is not null) await _mqtt.DisposeAsync().ConfigureAwait(false);
        if (_http is not null) await _http.DisposeAsync().ConfigureAwait(false);
        if (_opcUa is not null) await _opcUa.DisposeAsync().ConfigureAwait(false);
    }
}
