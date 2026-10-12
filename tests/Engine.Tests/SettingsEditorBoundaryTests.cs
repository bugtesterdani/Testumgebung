using System.Data.Common;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Notifications;
using Testumgebung.Presentation.Settings;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class SettingsEditorBoundaryTests
{
    private sealed class NoProviders : IDatabaseProviderRegistry { public DbProviderFactory? Factory(DatabaseProvider provider) => null; }
    private sealed class Peer(string failure) : QueuedNotifier(TimeSpan.FromSeconds(1))
    {
        public bool Disposed;
        public override string Channel => "fixture";
        public override string TargetFor(Testumgebung.Persistence.Results.RunNotification n) => "fixture";
        protected override Task SendAsync(Testumgebung.Persistence.Results.RunNotification n, CancellationToken cancellationToken)
            => failure switch { "timeout" => Task.FromException(new TimeoutException()), "aggregate" => Task.FromException(new AggregateException(new IOException("nested failure"))), "error" => Task.FromException(new IOException("send failure")), _ => Task.CompletedTask };
        public override async ValueTask DisposeAsync() { Disposed = true; await base.DisposeAsync(); }
    }
    private static async Task WithPage(Func<SettingsViewModel, ResultServices, QueueDispatcher, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-settings-editor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await using var services = new ResultServices(new AppSettings { ResultFolder = Path.Combine(directory, "results"), Database = new DatabaseSettings { Provider = DatabaseChoice.None } }, new NoProviders(), Path.Combine(directory, "outbox"), Path.Combine(directory, "database"), recoveryFolder: Path.Combine(directory, "recovery"), releaseFolder: Path.Combine(directory, "releases"));
            var ui = new QueueDispatcher(); var page = new SettingsViewModel(services, new SettingsStore(Path.Combine(directory, "settings.json"), PlainSecretProtector.Instance), ui);
            await action(page, services, ui, directory);
        }
        finally { Directory.Delete(directory, true); }
    }
    public static IEnumerable<object?[]> TextCases()
    {
        foreach (var property in new[] { "HttpUrl", "HttpHeaderName", "HttpHeaderValue", "OpcUaEndpoint", "OpcUaNodePrefix", "OpcUaUsername", "OpcUaPassword", "ResultFolder", "ConnectionString", "MqttHost", "MqttUsername", "MqttPassword", "MqttClientId", "MqttTopic", "MqttCaCertificatePath" })
            foreach (var value in new string?[] { null, "", "fixture" }) yield return [property, value];
    }
    [Theory]
    [MemberData(nameof(TextCases))]
    public Task Clearing_configuration_fields_removes_stale_values_and_revert_restores_saved_values(string property, string? value)
        => WithPage((page, services, ui, directory) =>
        {
            var target = typeof(SettingsViewModel).GetProperty(property)!;
            var original = target.GetValue(page); target.SetValue(page, "old-value"); target.SetValue(page, value);
            Assert.Equal(value ?? "", target.GetValue(page));
            if (property == "HttpHeaderValue") Assert.Equal(value == "fixture", page.HasHttpHeaderValue);
            if (property == "OpcUaPassword") Assert.Equal(value == "fixture", page.HasOpcUaPassword);
            if (property == "MqttPassword") Assert.Equal(value == "fixture", page.HasMqttPassword);
            page.RevertCommand.Execute(null); Assert.Equal(original, target.GetValue(page)); Assert.Contains("verworfen", page.SaveStatus);
            return Task.CompletedTask;
        });
    [Theory]
    [InlineData(1883, true, 8883)]
    [InlineData(8883, false, 1883)]
    [InlineData(2000, true, 2000)]
    [InlineData(2000, false, 2000)]
    public Task Tls_switch_changes_only_standard_mqtt_ports(int port, bool tls, int expected)
        => WithPage((page, _, _, _) => { page.MqttUseTls = !tls; page.MqttPort = port; page.MqttUseTls = tls; Assert.Equal(expected, page.MqttPort); page.MqttUseTls = tls; Assert.Equal(expected, page.MqttPort); return Task.CompletedTask; });
    [Theory]
    [InlineData("HttpTimeoutSeconds", -1, 1)]
    [InlineData("HttpTimeoutSeconds", 100, 60)]
    [InlineData("OpcUaTimeoutSeconds", -1, 1)]
    [InlineData("OpcUaTimeoutSeconds", 100, 60)]
    [InlineData("MqttTimeoutSeconds", -1, 1)]
    [InlineData("MqttTimeoutSeconds", 100, 60)]
    [InlineData("RetryIntervalSeconds", -1, 1)]
    [InlineData("BackoffFactor", -1, 1)]
    [InlineData("MaxRetryMinutes", -1, .1)]
    [InlineData("AttemptTimeoutSeconds", -1, 1)]
    public Task Timeouts_and_retry_values_are_limited_to_valid_ranges(string property, double value, double expected)
        => WithPage((page, _, _, _) => { var target = typeof(SettingsViewModel).GetProperty(property)!; target.SetValue(page, value); Assert.Equal(expected, target.GetValue(page)); return Task.CompletedTask; });
    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("aggregate")]
    [InlineData("unavailable")]
    [InlineData("invalid")]
    public async Task Opc_test_reports_result_and_disposes_sender(string failure)
    {
        var previous = ResultServices.OpcUaFactory; Peer? peer = null;
        try
        {
            ResultServices.OpcUaFactory = failure == "unavailable" ? null : _ => peer = new Peer(failure);
            await WithPage(async (page, _, ui, _) =>
            {
                page.OpcUaEndpoint = failure == "invalid" ? "bad" : "opc.tcp://fixture";
                await page.TestOpcUaAsync(); ui.Pump(); Assert.False(page.IsBusy);
                if (failure is "invalid" or "unavailable") { Assert.Null(peer); Assert.NotNull(page.OpcUaStatus); }
                else { Assert.True(peer!.Disposed); Assert.Contains(failure == "success" ? "Testmeldung gesendet" : failure == "timeout" ? "Zeitüberschreitung" : failure == "aggregate" ? "nested failure" : "send failure", page.OpcUaStatus); }
            });
        }
        finally { ResultServices.OpcUaFactory = previous; }
    }
    [Fact]
    public Task Unconfigured_database_and_invalid_http_report_configuration_problems()
        => WithPage(async (page, _, ui, _) =>
        {
            Assert.False(page.IsSqlProvider); await page.TestDatabaseAsync(); Assert.Contains("keine Verbindung", page.DatabaseStatus);
            await page.ContractCheckAsync(); Assert.Empty(page.ContractResult);
            foreach (var provider in page.Providers) { page.SelectedProvider = provider; Assert.NotEmpty(page.ConnectionExample); page.SelectedProvider = provider; }
            page.SelectedProvider = null!; page.SelectedProvider = page.Providers.First(p => p.Value == DatabaseChoice.SqlServer);
            await page.TestDatabaseAsync(); Assert.Contains("nicht verfügbar", page.DatabaseStatus);
            page.HttpUrl = "invalid"; await page.TestHttpAsync(); Assert.NotNull(page.HttpStatus); Assert.False(page.IsBusy);
            page.MqttHost = ""; await page.TestMqttAsync(); ui.Pump(); Assert.StartsWith("Fehler:", page.MqttStatus); Assert.False(page.IsBusy);
        });
}
