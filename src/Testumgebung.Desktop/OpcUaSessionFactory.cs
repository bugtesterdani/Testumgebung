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
    internal virtual Task CheckCertificateAsync(ApplicationConfiguration configuration)
        => new ApplicationInstance { ApplicationName = "Testumgebung", ApplicationType = ApplicationType.Client, ApplicationConfiguration = configuration }
            .CheckApplicationInstanceCertificatesAsync(true, 2048).AsTask();

    internal virtual EndpointDescription SelectEndpoint(ApplicationConfiguration configuration, string url, bool security, int timeout)
        => CoreClientUtils.SelectEndpoint(configuration, url, security, timeout);

    internal virtual async Task<ISession> CreateAsync(ApplicationConfiguration configuration, ConfiguredEndpoint endpoint, string name, IUserIdentity identity)
        => await Session.Create(configuration, endpoint, false, name, 60_000, identity, null).ConfigureAwait(false);
}
