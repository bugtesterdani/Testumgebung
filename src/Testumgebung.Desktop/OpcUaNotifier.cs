using System.IO;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Desktop;

/// <summary>
/// Meldeweg OPC UA über den OPC-Foundation-Stack: je Meldung werden die Felder RunId, DutId, Event, Time, Attempt, Error und
/// Station als Zeichenkette in die Knoten <c>{Präfix}{Feld}</c> des Servers geschrieben (eine Write-Anfrage). Die Sitzung bleibt
/// offen und wird nach einem Fehler neu aufgebaut. Ohne Sicherheit (Standard) oder Sign &amp; Encrypt mit selbst erzeugtem
/// Anwendungszertifikat unter %LOCALAPPDATA%\Testumgebung\pki; unbekannte Serverzertifikate werden angenommen und abgelegt.
/// Best effort wie MQTT: ein Versuch je Meldung, keine Wiederholung.
/// </summary>
public sealed class OpcUaNotifier(OpcUaNotifyOptions options) : QueuedNotifier(options.Timeout)
{
    private readonly OpcUaSessionFactory _sessionFactory = new();
    private readonly string _pkiRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "pki");
    internal OpcUaNotifier(OpcUaNotifyOptions options, OpcUaSessionFactory sessionFactory, string pkiRoot) : this(options)
    { _sessionFactory = sessionFactory; _pkiRoot = pkiRoot; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ISession? _session;

    public override string Channel => "OPC UA";

    public override string TargetFor(RunNotification n) => $"{options.EndpointUrl} · {options.NodePrefix}…";

    private string Pki(string part) => Path.Combine(_pkiRoot, part);

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
            TransportQuotas = new TransportQuotas { OperationTimeout = (int)options.Timeout.TotalMilliseconds },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        await config.Validate(ApplicationType.Client).ConfigureAwait(false);
        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
        };
        if (options.UseSecurity)
        {
            await _sessionFactory.CheckCertificateAsync(config).ConfigureAwait(false);
        }

        var description = _sessionFactory.SelectEndpoint(config, options.EndpointUrl, options.UseSecurity, (int)options.Timeout.TotalMilliseconds);
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
        IUserIdentity identity = options.Username is null
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(options.Username, options.Password ?? "");
        _session = await _sessionFactory.CreateAsync(config, endpoint, "Testumgebung " + options.Station, identity).ConfigureAwait(false);
        return _session;
    }

    protected override async Task SendAsync(RunNotification n, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync().ConfigureAwait(false);
            var fields = NotificationPayload.Fields(n, options.Station);
            var writes = new WriteValueCollection();
            foreach (var name in OpcUaNotifyOptions.FieldNames)
                writes.Add(new WriteValue
                {
                    NodeId = NodeId.Parse(options.NodeFor(name)),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(fields[name])),
                });
            var response = await session.WriteAsync(null, writes, cancellationToken).ConfigureAwait(false);
            var bad = response.Results.Select((r, i) => (Code: r, Field: OpcUaNotifyOptions.FieldNames[i])).Where(x => StatusCode.IsBad(x.Code)).ToList();
            if (bad.Count > 0)
                throw new InvalidOperationException("Schreiben abgelehnt: " + string.Join(", ", bad.Select(b => $"{options.NodeFor(b.Field)} {StatusCode.LookupSymbolicId(b.Code.Code)}")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Verbindung neu aufbauen beim nächsten Mal
            _session?.Dispose();
            _session = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        try
        {
            _session?.Close();
        }
        catch (Exception)
        {
            // beim Beenden ohne Bedeutung
        }
        _session?.Dispose();
        _gate.Dispose();
    }
}
