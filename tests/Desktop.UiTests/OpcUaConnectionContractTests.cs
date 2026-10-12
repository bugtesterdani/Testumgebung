using System.IO;
using System.Reflection;
using Opc.Ua;
using Opc.Ua.Client;
using Testumgebung.Contracts;
using Testumgebung.Instruments;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Xunit;

namespace Testumgebung.Desktop.UiTests;

public sealed class OpcUaConnectionContractTests
{
    private sealed class Factory : OpcUaSessionFactory
    {
        internal int Certificates, Connections;
        internal string? Failure;
        internal ApplicationConfiguration? Config;
        internal IUserIdentity? Identity;
        internal string? SessionName;
        internal readonly List<OpcUaSessionContractTests.SessionPeer> Peers = [];
        internal override Task CheckCertificateAsync(ApplicationConfiguration configuration) { Certificates++; return Task.CompletedTask; }
        internal override EndpointDescription SelectEndpoint(ApplicationConfiguration configuration, string url, bool security, int timeout)
        {
            Config = configuration; Assert.Equal("opc.tcp://fixture:4840", url); Assert.Equal(1234, timeout);
            if (Failure == "endpoint") throw new IOException("endpoint unavailable");
            return new EndpointDescription { EndpointUrl = url, SecurityMode = security ? MessageSecurityMode.SignAndEncrypt : MessageSecurityMode.None, SecurityPolicyUri = security ? SecurityPolicies.Basic256Sha256 : SecurityPolicies.None };
        }
        internal override Task<ISession> CreateAsync(ApplicationConfiguration configuration, ConfiguredEndpoint endpoint, string name, IUserIdentity identity)
        {
            Connections++; Identity = identity; SessionName = name;
            Assert.Equal("opc.tcp://fixture:4840/", endpoint.EndpointUrl.ToString());
            if (Failure == "session") throw new IOException("session unavailable");
            var session = DispatchProxy.Create<ISession, OpcUaSessionContractTests.SessionPeer>();
            Peers.Add((OpcUaSessionContractTests.SessionPeer)(object)session); return Task.FromResult(session);
        }
    }

    [Theory]
    [InlineData(false, null, null)]
    [InlineData(false, "worker", null)]
    [InlineData(true, "worker", "secret")]
    [InlineData(true, null, null)]
    public async Task Client_and_notifier_configure_security_identity_and_reuse_session(bool security, string? user, string? password)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-opc-connect-" + Guid.NewGuid().ToString("N"));
        var factory = new Factory();
        try
        {
            var values = new Dictionary<string, string> { ["Sicherheit"] = security ? "ja" : "nein", ["Zeitlimit"] = "1234" };
            if (user is not null) values["Benutzer"] = user; if (password is not null) values["Passwort"] = password;
            await using (var client = new OpcUaClient("opc.tcp://fixture:4840", new InstrumentOptions(values), factory, directory))
            {
                await client.ConnectAsync(default); await client.ConnectAsync(default); Assert.Equal(1, factory.Connections);
                AssertConfig(factory, directory, security, user);
                Assert.Equal("Testumgebung OPC UA", factory.SessionName);
                factory.Peers[0].Connected = false;
                await client.ConnectAsync(default); Assert.True(factory.Peers[0].Disposed); Assert.Equal(2, factory.Connections);
            }
            Assert.All(factory.Peers, p => Assert.True(p.Disposed));
            factory = new Factory();
            await using (var notifier = new OpcUaNotifier(new OpcUaNotifyOptions { EndpointUrl = "opc.tcp://fixture:4840", UseSecurity = security, Username = user, Password = password, Station = "P1", Timeout = TimeSpan.FromMilliseconds(1234) }, factory, directory))
            {
                await Send(notifier); await Send(notifier); Assert.Equal(1, factory.Connections);
                AssertConfig(factory, directory, security, user); Assert.Equal("Testumgebung P1", factory.SessionName);
                factory.Peers[0].Connected = false;
                await Send(notifier); Assert.Equal(2, factory.Connections); Assert.True(factory.Peers[0].Disposed);
            }
            Assert.All(factory.Peers, p => Assert.True(p.Disposed));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("session")]
    public async Task Failed_connection_is_reported_and_next_attempt_can_recover(string failure)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-opc-fail-" + Guid.NewGuid().ToString("N")); var factory = new Factory { Failure = failure };
        try
        {
            await using var client = new OpcUaClient("opc.tcp://fixture:4840", new InstrumentOptions(new Dictionary<string, string> { ["Zeitlimit"] = "1234" }), factory, directory);
            var error = await Assert.ThrowsAsync<OpcUaException>(() => client.ConnectAsync(default)); Assert.Contains("Verbindung fehlgeschlagen", error.Message);
            Assert.IsType<IOException>(error.InnerException); factory.Failure = null; await client.ConnectAsync(default);
            factory = new Factory { Failure = failure };
            await using var notifier = new OpcUaNotifier(new OpcUaNotifyOptions { EndpointUrl = "opc.tcp://fixture:4840", Timeout = TimeSpan.FromMilliseconds(1234) }, factory, directory);
            await Assert.ThrowsAsync<IOException>(() => Send(notifier)); factory.Failure = null; await Send(notifier);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void AssertConfig(Factory factory, string directory, bool security, string? user)
    {
        var config = factory.Config!; Assert.Equal(ApplicationType.Client, config.ApplicationType); Assert.Equal(1234, config.TransportQuotas.OperationTimeout);
        Assert.Equal(60_000, config.ClientConfiguration.DefaultSessionTimeout); Assert.True(config.SecurityConfiguration.AutoAcceptUntrustedCertificates);
        Assert.Equal(Path.Combine(directory, "own"), config.SecurityConfiguration.ApplicationCertificate.StorePath);
        Assert.Equal(Path.Combine(directory, "trusted"), config.SecurityConfiguration.TrustedPeerCertificates.StorePath);
        Assert.Equal(Path.Combine(directory, "issuer"), config.SecurityConfiguration.TrustedIssuerCertificates.StorePath);
        Assert.Equal(Path.Combine(directory, "rejected"), config.SecurityConfiguration.RejectedCertificateStore.StorePath);
        Assert.Equal(security ? 1 : 0, factory.Certificates);
        Assert.Equal(user is null ? UserTokenType.Anonymous : UserTokenType.UserName, factory.Identity!.TokenType);
    }
    private static Task Send(OpcUaNotifier notifier)
        => (Task)typeof(OpcUaNotifier).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(notifier, [new RunNotification(RunNotificationKind.Stored, "run", "SN", DateTimeOffset.UnixEpoch, 1, null), CancellationToken.None])!;
}
