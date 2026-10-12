using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Testumgebung.Contracts;
using Testumgebung.Instruments;

namespace Testumgebung.Desktop;

/// <summary>
/// Realer OPC-UA-Client (OPC-Foundation-Stack) für Geräte vom Typ „OPC UA“. Wird in <see cref="App"/> in
/// <c>OpcUaDevice.ClientFactory</c> eingehängt und steht damit nur in der Windows-Anwendung zur Verfügung.
/// Die Ressource ist der Endpunkt (<c>opc.tcp://…</c>). Optionen: „Sicherheit“ (Sign &amp; Encrypt), „Benutzer“/„Passwort“
/// (sonst anonym), „Zeitlimit“ (ms). Werte werden auf die vom Contract vorgesehenen Typen null/bool/long/double/string
/// abgebildet. Anwendungszertifikat und Vertrauensspeicher unter %LOCALAPPDATA%\Testumgebung\pki (unbekannte
/// Serverzertifikate werden angenommen und abgelegt) – dieselbe Ablage wie der OPC-UA-Meldeweg.
/// </summary>
public sealed class OpcUaClient(string endpointUrl, InstrumentOptions options) : IOpcUaClient
{
    private readonly OpcUaSessionFactory _sessionFactory = new();
    private readonly string _pkiRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "pki");
    internal OpcUaClient(string endpointUrl, InstrumentOptions options, OpcUaSessionFactory sessionFactory, string pkiRoot) : this(endpointUrl, options)
    { _sessionFactory = sessionFactory; _pkiRoot = pkiRoot; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _useSecurity = options.Flag("Sicherheit");
    private readonly string? _user = options.Text("Benutzer");
    private readonly string? _password = options.Text("Passwort");
    private readonly int _timeoutMs = (int)(options.Number("Zeitlimit") ?? 10_000);
    private readonly List<SubscriptionHandle> _subscriptions = [];
    private ISession? _session;

    private string Pki(string part) => Path.Combine(_pkiRoot, part);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnsureSessionAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ISession> EnsureSessionAsync()
    {
        if (_session is { Connected: true } existing) return existing;
        _session?.Dispose();
        _session = null;

        var config = new ApplicationConfiguration
        {
            ApplicationName = "Testumgebung",
            ApplicationUri = Utils.Format("urn:{0}:Testumgebung", System.Net.Dns.GetHostName()),
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Pki("own"),
                    SubjectName = "CN=Testumgebung, O=Testumgebung",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Pki("issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Pki("trusted") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Pki("rejected") },
                AutoAcceptUntrustedCertificates = true,
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = _timeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        await config.Validate(ApplicationType.Client).ConfigureAwait(false);
        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
        };
        if (_useSecurity)
        {
            await _sessionFactory.CheckCertificateAsync(config).ConfigureAwait(false);
        }

        try
        {
            var description = _sessionFactory.SelectEndpoint(config, endpointUrl, _useSecurity, _timeoutMs);
            var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
            IUserIdentity identity = _user is null
                ? new UserIdentity(new AnonymousIdentityToken())
                : new UserIdentity(_user, _password ?? "");
            _session = await _sessionFactory.CreateAsync(config, endpoint, "Testumgebung OPC UA", identity).ConfigureAwait(false);
            return _session;
        }
        catch (Exception ex)
        {
            throw new OpcUaException($"OPC UA „{endpointUrl}“: Verbindung fehlgeschlagen – {ex.Message}", ex);
        }
    }

    public async Task<object?> ReadAsync(string nodeId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync().ConfigureAwait(false);
            var read = new ReadValueIdCollection { new() { NodeId = NodeId.Parse(nodeId), AttributeId = Attributes.Value } };
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, read, cancellationToken).ConfigureAwait(false);
            var value = response.Results[0];
            if (StatusCode.IsBad(value.StatusCode))
                throw new OpcUaException($"OPC UA: Lesen von „{nodeId}“ abgelehnt ({StatusCode.LookupSymbolicId(value.StatusCode.Code)}).");
            return FromVariant(value.Value);
        }
        catch (Exception ex) when (ex is not OpcUaException and not OperationCanceledException)
        {
            Reset();
            throw new OpcUaException($"OPC UA: Lesen von „{nodeId}“ fehlgeschlagen – {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string nodeId, object? value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync().ConfigureAwait(false);
            var writes = new WriteValueCollection
            {
                new() { NodeId = NodeId.Parse(nodeId), AttributeId = Attributes.Value, Value = new DataValue(ToVariant(value)) },
            };
            var response = await session.WriteAsync(null, writes, cancellationToken).ConfigureAwait(false);
            if (StatusCode.IsBad(response.Results[0]))
                throw new OpcUaException($"OPC UA: Schreiben auf „{nodeId}“ abgelehnt ({StatusCode.LookupSymbolicId(response.Results[0].Code)}).");
        }
        catch (Exception ex) when (ex is not OpcUaException and not OperationCanceledException)
        {
            Reset();
            throw new OpcUaException($"OPC UA: Schreiben auf „{nodeId}“ fehlgeschlagen – {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<object?>> CallAsync(string objectNodeId, string methodNodeId, IReadOnlyList<object?> arguments, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync().ConfigureAwait(false);
            var request = new CallMethodRequestCollection
            {
                new()
                {
                    ObjectId = NodeId.Parse(objectNodeId),
                    MethodId = NodeId.Parse(methodNodeId),
                    InputArguments = [.. arguments.Select(a => ToVariant(a))],
                },
            };
            var response = await session.CallAsync(null, request, cancellationToken).ConfigureAwait(false);
            var result = response.Results[0];
            if (StatusCode.IsBad(result.StatusCode))
                throw new OpcUaException($"OPC UA: Methode „{methodNodeId}“ abgelehnt ({StatusCode.LookupSymbolicId(result.StatusCode.Code)}).");
            return [.. result.OutputArguments.Select(v => FromVariant(v.Value))];
        }
        catch (Exception ex) when (ex is not OpcUaException and not OperationCanceledException)
        {
            Reset();
            throw new OpcUaException($"OPC UA: Methode „{methodNodeId}“ fehlgeschlagen – {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<IOpcUaSubscription> SubscribeAsync(string nodeId, int samplingIntervalMs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync().ConfigureAwait(false);
            var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = Math.Max(samplingIntervalMs, 1) };
            session.AddSubscription(subscription);
            await subscription.CreateAsync(cancellationToken).ConfigureAwait(false);
            var handle = new SubscriptionHandle(nodeId, subscription, this);
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                StartNodeId = NodeId.Parse(nodeId),
                AttributeId = Attributes.Value,
                SamplingInterval = Math.Max(samplingIntervalMs, 1),
            };
            item.Notification += handle.OnNotification;
            subscription.AddItem(item);
            await subscription.ApplyChangesAsync(cancellationToken).ConfigureAwait(false);
            lock (_subscriptions) _subscriptions.Add(handle);
            return handle;
        }
        catch (Exception ex) when (ex is not OpcUaException and not OperationCanceledException)
        {
            Reset();
            throw new OpcUaException($"OPC UA: Abonnement „{nodeId}“ fehlgeschlagen – {ex.Message}", ex);
        }
        finally { _gate.Release(); }
    }

    private void Reset()
    {
        _session?.Dispose();
        _session = null;
    }

    private void Remove(SubscriptionHandle handle)
    {
        lock (_subscriptions) _subscriptions.Remove(handle);
    }

    public async ValueTask DisposeAsync()
    {
        List<SubscriptionHandle> handles;
        lock (_subscriptions) { handles = [.. _subscriptions]; _subscriptions.Clear(); }
        foreach (var handle in handles) await handle.DisposeAsync().ConfigureAwait(false);
        try { _session?.Close(); } catch (Exception) { /* beim Beenden ohne Bedeutung */ }
        _session?.Dispose();
        _gate.Dispose();
    }

    /// <summary>OPC-UA-Variant → null/bool/long/double/string (andere Typen als Text).</summary>
    private static object? FromVariant(object? value) => value switch
    {
        null => null,
        bool b => b,
        sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        ulong u => (long)u,
        float or double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        string s => s,
        _ => value.ToString(),
    };

    private static Variant ToVariant(object? value) => value switch
    {
        null => Variant.Null,
        bool b => new Variant(b),
        long l => new Variant(l),
        int i => new Variant(i),
        double d => new Variant(d),
        float f => new Variant(f),
        string s => new Variant(s),
        _ => new Variant(value),
    };

    private sealed class SubscriptionHandle : IOpcUaSubscription
    {
        private readonly Subscription? _subscription;
        private readonly OpcUaClient? _owner;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly object _lock = new();

        public SubscriptionHandle(string nodeId, Subscription subscription, OpcUaClient owner)
        {
            NodeId = nodeId;
            _subscription = subscription;
            _owner = owner;
        }

        public string NodeId { get; } = "";
        public object? Current { get; private set; }
        public int Changes { get; private set; }

        public void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            foreach (var value in item.DequeueValues())
            {
                lock (_lock) { Current = FromVariant(value.Value); Changes++; }
                _signal.Release();
            }
        }

        public async Task<object?> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!await _signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
                throw new CommunicationTimeoutException($"OPC-UA-Abo „{NodeId}“: keine Änderung innerhalb von {timeout.TotalMilliseconds:0} ms.", []);
            lock (_lock) return Current;
        }

        public async ValueTask DisposeAsync()
        {
            _owner?.Remove(this);
            if (_subscription is not null)
            {
                try { await _subscription.DeleteAsync(false).ConfigureAwait(false); } catch (Exception) { /* beim Beenden ohne Bedeutung */ }
            }
            _signal.Dispose();
        }
    }
}
