using System.Data.Common;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Engine.Tests;

public sealed class ResultServiceConfigurationTests
{
    private sealed class Registry : IDatabaseProviderRegistry { public DbProviderFactory? Factory(DatabaseProvider provider) => provider == DatabaseProvider.Sqlite ? NativeSqliteFactory.Instance : null; }
    private sealed class Peer : QueuedNotifier
    {
        public Peer() : base(TimeSpan.FromSeconds(1)) { }
        public bool Disposed;
        public override string Channel => "fixture";
        public override string TargetFor(RunNotification notification) => "fixture";
        protected override Task SendAsync(RunNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async ValueTask DisposeAsync() { Disposed = true; await base.DisposeAsync(); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("mqtt")]
    [InlineData("http")]
    [InlineData("opc")]
    [InlineData("all")]
    [InlineData("invalid")]
    [InlineData("blank")]
    [InlineData("no-factory")]
    public async Task Applying_settings_replaces_senders_and_reports_configuration_errors(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "service-options-" + Guid.NewGuid());
        var factory = ResultServices.OpcUaFactory; Peer? peer = null;
        try
        {
            ResultServices.OpcUaFactory = mode == "no-factory" ? null : _ => peer = new Peer();
            var settings = new AppSettings
            {
                ResultFolder = Path.Combine(directory, "results"),
                Database = new DatabaseSettings { Provider = mode == "invalid" ? DatabaseChoice.SqlServer : DatabaseChoice.None },
                Mqtt = new MqttSettings { Enabled = mode is "mqtt" or "all" or "blank", Host = mode == "blank" ? " " : "fixture.invalid", ClientId = " ", Topic = " ", Username = " ", CaCertificatePath = " " },
                Http = new HttpNotifySettings { Enabled = mode is "http" or "all" or "invalid" or "blank", Url = mode == "blank" ? " " : mode == "invalid" ? "bad" : "http://fixture.invalid/run" },
                OpcUa = new OpcUaSettings { Enabled = mode is "opc" or "all" or "invalid" or "blank" or "no-factory", EndpointUrl = mode == "blank" ? " " : mode == "invalid" ? "bad" : "opc.tcp://fixture.invalid", NodePrefix = "ns=2;s=Fixture." },
                Outbox = new OutboxSettings { RetryIntervalSeconds = -1, AttemptTimeoutSeconds = -1, BackoffFactor = -1, MaxRetryMinutes = -1 },
            };
            await using var services = new ResultServices(settings, new Registry(), Path.Combine(directory, "outbox"), Path.Combine(directory, "db"), recoveryFolder: Path.Combine(directory, "recovery"), releaseFolder: Path.Combine(directory, "releases"));
            Assert.Same(settings, services.Current); Assert.IsType<FolderReleaseStore>(services.ReleaseStore.Inner);
            Assert.Equal(mode is "mqtt" or "all", services.Mqtt is not null); Assert.Equal(mode is "http" or "all", services.Http is not null); Assert.Equal(mode is "opc" or "all", services.OpcUa is not null);
            Assert.Equal(mode is "invalid" or "no-factory", services.ApplyWarning is not null);
            if (mode == "invalid") { Assert.Contains("Treiber", services.ApplyWarning); Assert.Contains("HTTP:", services.ApplyWarning); Assert.Contains("OPC UA:", services.ApplyWarning); }
            if (peer is not null)
            {
                var delivered = new TaskCompletionSource<NotifyDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
                services.NotifyDelivered += d => delivered.TrySetResult(d);
                peer.Notify(NotificationPayload.TestNotification()); Assert.True((await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5))).Sent);
            }
            var applied = 0; services.Applied += () => applied++;
            services.Apply(new AppSettings { ResultFolder = Path.Combine(directory, "other"), Database = new DatabaseSettings { Provider = DatabaseChoice.Sqlite, ConnectionString = "Data Source=" + Path.Combine(directory, "unused.db") } });
            Assert.Equal(1, applied); Assert.IsType<SqlReleaseStore>(services.ReleaseStore.Inner); Assert.Null(services.ApplyWarning);
            Assert.Null(services.Mqtt); Assert.Null(services.Http); Assert.Null(services.OpcUa); Assert.Null(services.Notifier.Inner);
            if (peer is not null) Assert.True(peer.Disposed);
            Assert.Null(services.CreateDatabase(new DatabaseSettings { Provider = DatabaseChoice.Sqlite, ConnectionString = " " }, out var warning)); Assert.Contains("Verbindungsstring", warning);
        }
        finally { ResultServices.OpcUaFactory = factory; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
