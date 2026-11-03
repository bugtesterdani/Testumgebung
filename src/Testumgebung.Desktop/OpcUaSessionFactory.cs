/**
 * @file
 * @brief Bündelt Stack-Aufrufe zum Aufbau einer OPC-UA-Sitzung an einer testbaren Verbindungsgrenze.
 */

using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Testumgebung.Desktop;

/// <summary>OPC-Stack-Aufrufe an der Verbindungsgrenze; Tests verwenden einen kontrollierten Server-Gegenpart.</summary>
internal class OpcUaSessionFactory
{
    // Ein gemeinsamer Diagnosekontext ohne Ausgabeanbieter. Damit bleibt das
    // bisherige stille Verhalten erhalten; es werden keine Daten exportiert.
    internal static ITelemetryContext Telemetry { get; } = DefaultTelemetry.Create(_ => { });

    internal virtual Task CheckCertificateAsync(ApplicationConfiguration configuration)
        => new ApplicationInstance(Telemetry) { ApplicationName = "Testumgebung", ApplicationType = ApplicationType.Client, ApplicationConfiguration = configuration }
            .CheckApplicationInstanceCertificatesAsync(true, 2048).AsTask();

    internal virtual async Task<EndpointDescription> SelectEndpointAsync(ApplicationConfiguration configuration, string url, bool security, int timeout)
        => await CoreClientUtils.SelectEndpointAsync(configuration, url, security, timeout, Telemetry).ConfigureAwait(false)
            ?? throw new ServiceResultException(StatusCodes.BadConfigurationError, "Kein passender OPC-UA-Endpunkt verfügbar.");

    internal virtual async Task<ISession> CreateAsync(ApplicationConfiguration configuration, ConfiguredEndpoint endpoint, string name, IUserIdentity identity)
        => await new DefaultSessionFactory(Telemetry).CreateAsync(configuration, endpoint, false, name, 60_000, identity, []).ConfigureAwait(false);
}
