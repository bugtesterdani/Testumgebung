using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class WearCounterTests
{
    [Fact]
    public void Counter_reservations_and_reset_audit_survive_reopen()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-wear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "wear.json");
            var store = new WearCounterStore(path);
            var needles = new WearCounterConfig { Name = "Nadelbett", WarningAt = 1, StopAt = 2 };
            var relay = new WearCounterConfig { Name = "Relais" };
            var first = store.BeginDut([needles, relay]);
            Assert.True(first.Started);
            Assert.True(first.Readings[0].Warning);
            Assert.False(first.Readings[0].Blocked);
            var second = new WearCounterStore(path);
            Assert.True(second.BeginDut([needles, relay]).Started);
            var denied = store.BeginDut([needles, relay]);
            Assert.False(denied.Started);
            Assert.All(denied.Readings, reading => Assert.Equal(2, reading.Count));

            var now = DateTimeOffset.UnixEpoch;
            second.Reset("Platz 1", needles, "m.weber", now);
            var reopened = new WearCounterStore(path);
            Assert.Equal(new long[] { 0, 2 }, reopened.Read([needles, relay]).Select(reading => reading.Count));
            var audit = Assert.Single(reopened.Resets("Platz 1"));
            Assert.Equal(new WearReset("Platz 1", needles.Id, "Nadelbett", 2, "m.weber", now), audit);
            Assert.Empty(reopened.Resets("Platz 2"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Continuous_station_run_stops_before_next_DUT_and_restarts_after_audited_reset()
    {
        var probe = new Probe();
        var dut = Group("dut").Var("Kennung", VarType.String, "product");
        dut.IsDutRun = true;
        dut.Add(probe.Mark("measure"));
        var skipped = Group("skipped").Var("Kennung", VarType.String, "skipped");
        skipped.IsDutRun = true;
        skipped.EntryCondition = "false";
        skipped.Add(probe.Mark("never"));
        var loop = Group("loop").Add(skipped, dut);
        loop.Repetition = Repetition.Endless;
        var definition = Test(loop, finalization: Group("fin").Add(probe.Mark("end")));
        var project = new TestProject(definition);
        var setup = StationSetup.Default(project);
        var counter = new WearCounterConfig { Name = "Adapter", WarningAt = 1, StopAt = 2 };
        var configured = setup.Stations[0];
        configured.WearCounters.Add(counter);
        var resources = new ResourceManager();
        var coordinator = new StationCoordinator(setup, resources);
        var ui = new QueueDispatcher();
        var station = new StationViewModel(configured.Name, () => project, resources, ui: ui, coordinator: coordinator);
        var runProject = typeof(StationViewModel).GetMethod("RunProjectAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await ((Task)runProject.Invoke(station, [project, null, null, null, null, false, false, null])!).Within();
        ui.Pump();
        Assert.Equal(new[] { "measure", "measure", "end" }, probe.Marks);
        Assert.Contains(station.Protocol, entry => entry.Status == "Warngrenze erreicht");
        Assert.Contains("Verschleißgrenze", station.Error);
        Assert.Equal(2, Assert.Single(coordinator.Wear.Read([counter])).Count);

        await station.StartAsync().Within();
        Assert.Contains("Verschleißgrenze", station.Error);

        var settings = new StationsViewModel(coordinator, new EditorViewModel(project), null, ui, () => station.IsRunning, () => "wartung");
        settings.SelectedWearCounter = Assert.Single(settings.WearCounters);
        settings.ResetWearCounterCommand.Execute(null);
        Assert.Null(settings.Error);
        Assert.Equal("wartung", Assert.Single(settings.WearResets).User);
        Assert.Equal(0, Assert.Single(settings.WearCounters).Count);
        await ((Task)runProject.Invoke(station, [project, null, null, 1, null, false, false, null])!).Within();
        Assert.Equal(1, Assert.Single(coordinator.Wear.Read([counter])).Count);
    }

    [Fact]
    public async Task Simulation_does_not_consume_wear_and_corrupt_storage_blocks_physical_runs()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-wear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var dut = Group("dut").Var("Kennung", VarType.String, "product");
            dut.IsDutRun = true;
            var project = new TestProject(Test(Group("loop").Add(dut)));
            var setup = StationSetup.Default(project);
            var stationConfig = setup.Stations[0];
            var counter = new WearCounterConfig { Name = "Adapter", StopAt = 1 };
            stationConfig.WearCounters.Add(counter);
            var path = Path.Combine(folder, "wear.json");
            File.WriteAllText(path, "{}");
            var resources = new ResourceManager();
            var coordinator = new StationCoordinator(setup, resources, wear: new(path));
            var station = new StationViewModel(stationConfig.Name, () => project, resources, coordinator: coordinator);
            await station.StartSimulationAsync().Within();
            Assert.Equal(1, station.Run!.DutsCompleted);
            Assert.Equal("{}", File.ReadAllText(path));
            await station.StartAsync().Within();
            Assert.Contains("nicht gelesen", station.Error);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
