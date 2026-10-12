using System.Data.Common;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Hardware;
using Testumgebung.Instruments.Communication;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Pilot;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Engine.Tests;

public sealed class SystemCheckBoundaryTests
{
    private sealed class Registry : IDatabaseProviderRegistry { public DbProviderFactory? Factory(DatabaseProvider provider) => provider == DatabaseProvider.Sqlite ? NativeSqliteFactory.Instance : null; }
    [Theory]
    [InlineData("none")]
    [InlineData("simulated")]
    [InlineData("sql")]
    [InlineData("sql-error")]
    [InlineData("missing-provider")]
    public async Task System_check_reports_database_and_device_probe_success_and_errors(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "system-check-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var database = new DatabaseSettings { Provider = mode switch { "none" => DatabaseChoice.None, "simulated" => DatabaseChoice.Simulated, "missing-provider" => DatabaseChoice.SqlServer, _ => DatabaseChoice.Sqlite }, ConnectionString = "Data Source=" + Path.Combine(directory, "db.sqlite") };
            if (mode == "sql-error") File.WriteAllText(Path.Combine(directory, "db.sqlite"), "not a database");
            await using var services = new ResultServices(new AppSettings { ResultFolder = Path.Combine(directory, "results"), Database = database, Mqtt = new MqttSettings { Enabled = true, Host = "" } }, new Registry(), Path.Combine(directory, "outbox"), Path.Combine(directory, "sim"), recoveryFolder: Path.Combine(directory, "recovery"), releaseFolder: Path.Combine(directory, "releases"));
            var setup = StationSetup.Default(SampleProjects.AcPruefung());
            setup.Devices.Add(new DeviceConfig { Id = "good", Name = "Good", Adapter = LinDevice.VirtualAdapter, Resource = "fixture" });
            setup.Devices.Add(new DeviceConfig { Id = "bad", Name = "Bad", Adapter = "unknown adapter", Resource = "fixture" });
            setup.Devices.Add(new DeviceConfig { Id = "blank", Name = "Blank", Adapter = LinDevice.VirtualAdapter, Resource = " " });
            var checks = await SystemCheck.RunAsync(new SystemCheckContext { Services = services, Coordinator = new StationCoordinator(setup, new ResourceManager()), ProbeDevices = true, MinimumFreeBytes = long.MaxValue, RunProcess = (_, _) => null, VisaAvailable = () => false, DaqmxAvailable = () => false });
            Assert.Equal(CheckLevel.Ok, checks.Single(c => c.Area == "Geräte" && c.Name == "Good").Level);
            Assert.Equal(CheckLevel.Error, checks.Single(c => c.Area == "Geräte" && c.Name == "Bad").Level);
            Assert.DoesNotContain(checks, c => c.Area == "Geräte" && c.Name == "Blank");
            Assert.Equal(mode switch { "none" or "missing-provider" => CheckLevel.Info, "simulated" => CheckLevel.Warning, "sql-error" => CheckLevel.Error, _ => CheckLevel.Ok }, checks.Single(c => c.Name == "Prüfdatenbank").Level);
            Assert.Equal(CheckLevel.Warning, checks.Single(c => c.Name == "Freier Speicher").Level);
            Assert.Contains(checks, c => c.Name == "MQTT");
            if (mode == "missing-provider") Assert.Contains(checks, c => c.Name == "Einstellungen" && c.Level == CheckLevel.Warning);
        }
        finally { Directory.Delete(directory, true); }
        var withoutCoordinator = await SystemCheck.RunAsync(new SystemCheckContext { ProbeDevices = true, RunProcess = (_, _) => null });
        Assert.DoesNotContain(withoutCoordinator, c => c.Area == "Geräte");
    }
}
