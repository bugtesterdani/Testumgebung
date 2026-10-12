using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StationEditorWorkflowTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("other")]
    [InlineData("none")]
    public void New_resource_names_are_unique_and_device_fallback_is_explicit(string devices)
    {
        var project = SampleProjects.AcPruefung(); var setup = StationSetup.Default(project, 1);
        setup.Stations[0].Resources.Clear();
        if (devices != "local") setup.Devices.Clear(); if (devices == "other") setup.Devices.Add(new DeviceConfig { Id = "other", Name = "Other" });
        var coordinator = new StationCoordinator(setup, new ResourceManager());
        var page = new StationsViewModel(coordinator, new ShellViewModel(project: project).Editor, null, new QueueDispatcher(), () => false);
        page.AddResourceCommand.Execute(null); page.AddResourceCommand.Execute(null);
        Assert.Equal(new[] { "Neue_Ressource_1", "Neue_Ressource_2" }, page.Rows.Select(r => r.Name));
        Assert.All(page.Rows, r => Assert.Equal(devices == "local" ? StationSetup.LocalSimulation : devices == "other" ? "other" : "", r.Assignment.Device));
        Assert.True(page.IsDirty);
        page.RemoveResourceCommand.Execute(null); Assert.Single(page.Rows); page.RemoveResourceCommand.Execute(null); Assert.Empty(page.Rows); page.RemoveResourceCommand.Execute(null);
        page.SelectedStation = "missing"; page.AddResourceCommand.Execute(null); page.AddMissingCommand.Execute(null); Assert.Empty(page.Rows);
        page.DiscardCommand.Execute(null); Assert.False(page.IsDirty); Assert.Empty(page.Rows);
    }
    [Fact]
    public void Missing_resources_are_added_once_and_station_device_editing_preserves_assignments()
    {
        var project = SampleProjects.AcPruefung(); var setup = StationSetup.Default(project, 1); setup.Stations[0].Resources.Clear();
        var coordinator = new StationCoordinator(setup, new ResourceManager());
        var page = new StationsViewModel(coordinator, new ShellViewModel(project: project).Editor, null, new QueueDispatcher(), () => false);
        page.AddMissingCommand.Execute(null); Assert.NotEmpty(page.Rows); Assert.Contains("ergänzt", page.Status);
        var names = page.Rows.Select(r => r.Name).ToArray(); page.AddMissingCommand.Execute(null); Assert.Equal(names, page.Rows.Select(r => r.Name)); Assert.Contains("Alle", page.Status);
        page.AddDeviceCommand.Execute(null); page.AddDeviceCommand.Execute(null); Assert.Contains(page.Devices, d => d.Id == "geraet-2");
        var device = page.Devices.First(d => d.Id == "geraet-1"); device.Shared = true;
        var row = page.Rows[0]; row.Device = device; Assert.True(row.RequiresLock); row.Device = device; row.Device = null;
        row.Channel = " new-channel "; Assert.Equal("new-channel", row.Channel); row.Channel = null!; row.Channel = "new-channel";
        row.Name = " New_Name "; Assert.Equal("New_Name", row.Name); row.RequiresLock = false; Assert.False(row.RequiresLock);
        page.SelectedDevice = device; page.RemoveDeviceCommand.Execute(null); Assert.True(page.HasError); Assert.Contains("wird noch verwendet", page.Error);
        row.Device = page.Devices.First(d => d.Id == StationSetup.LocalSimulation); page.RemoveDeviceCommand.Execute(null); Assert.DoesNotContain(page.Devices, d => d.Id == "geraet-1");
        page.SelectedDevice = null; page.RemoveDeviceCommand.Execute(null);
        page.AddStationCommand.Execute(null); Assert.Equal(2, page.StationNames.Count); Assert.Equal(page.Draft.Stations[0].Resources.Count, page.Rows.Count);
        page.RemoveStationCommand.Execute(null); Assert.Single(page.StationNames); page.RemoveStationCommand.Execute(null); Assert.Single(page.StationNames);
        page.RestoreSuggestionCommand.Execute(null); Assert.Contains("wiederhergestellt", page.Status); Assert.DoesNotContain(page.Rows, r => r.Name == "New_Name");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_applies_station_changes_only_when_no_test_is_running(bool running)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-station-editor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var project = SampleProjects.AcPruefung(); var setup = StationSetup.Default(project, 1); var coordinator = new StationCoordinator(setup, new ResourceManager());
            var store = new StationSetupStore(Path.Combine(directory, "stations.json"));
            var page = new StationsViewModel(coordinator, new ShellViewModel(project: project).Editor, store, new QueueDispatcher(), () => running);
            page.AddStationCommand.Execute(null); page.SaveCommand.Execute(null);
            if (running) { Assert.True(page.HasError); Assert.Same(setup, coordinator.Setup); Assert.False(File.Exists(store.Path)); }
            else { Assert.False(page.HasError); Assert.Equal(2, coordinator.Setup.Stations.Count); Assert.NotNull(store.Load()); Assert.False(page.IsDirty); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
