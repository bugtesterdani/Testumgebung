using System.Reflection;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StationStartGuardTests
{
    [Theory]
    [InlineData("missing-station")]
    [InlineData("missing-device")]
    [InlineData("invalid-definition")]
    public async Task Invalid_project_or_station_configuration_refuses_start_and_releases_run_registration(string damage)
    {
        var project = SampleProjects.AcPruefung(); var setup = StationSetup.Default(project);
        var name = setup.Stations[0].Name;
        if (damage == "missing-station") name = "missing station";
        if (damage == "missing-device") setup.Stations[0].Resources[0].Device = "missing device";
        if (damage == "invalid-definition") project.Definition.Loop.Children.Add(project.Definition.Loop.Children[0]);
        var resources = new ResourceManager(); var coordinator = new StationCoordinator(setup, resources);
        var station = new StationViewModel(name, () => project, resources, coordinator: coordinator);
        var start = (Task)typeof(StationViewModel).GetMethod("RunProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(station, [project, null, null, null, null, false, false, null])!;
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(station.IsRunning); Assert.Null(station.Run); Assert.NotNull(station.Error); Assert.Empty(coordinator.RunningStations);
        if (damage != "invalid-definition") Assert.Contains(damage == "missing-station" ? "nicht vorhanden" : "nicht konfiguriert", station.Error);
    }
}
