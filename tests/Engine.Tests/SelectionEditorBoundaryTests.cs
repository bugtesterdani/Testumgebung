using Testumgebung.Contracts;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class SelectionEditorBoundaryTests
{
    [Theory]
    [InlineData("running")]
    [InlineData("disabled")]
    [InlineData("custom-missing")]
    [InlineData("rule-pattern")]
    [InlineData("rule-test")]
    [InlineData("missing-station")]
    [InlineData("write-error")]
    [InlineData("success")]
    [InlineData("off")]
    public void Saving_selection_validates_configuration_before_replacing_station(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-selection-save-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var setup = StationSetup.Default(SampleProjects.AcPruefung(), 1); var coordinator = new StationCoordinator(setup, new ResourceManager());
            var path = Path.Combine(directory, "stations.json");
            if (mode == "write-error") { File.WriteAllText(path, "blocked"); path = Path.Combine(path, "stations.json"); }
            var page = new SelectionPageViewModel(coordinator, new StationSetupStore(path), null, () => [new AvailableTest("AC-Prüfung", "1", true, "released")], () => mode == "running", new QueueDispatcher());
            page.UseSelection = mode != "off";
            if (mode == "disabled") { page.UseManual = false; page.UseRules = false; page.UseCustom = false; }
            if (mode == "custom-missing") page.UseCustom = true;
            if (mode.StartsWith("rule-")) { page.AddRuleCommand.Execute(null); if (mode == "rule-pattern") page.Rules[0].Pattern = " "; else page.Rules[0].Test = " "; }
            if (mode == "missing-station") page.SelectedStation = "missing";
            page.Save();
            if (mode is "success" or "off")
            {
                Assert.False(page.HasError); Assert.False(page.IsDirty); Assert.Contains("gespeichert", page.Status);
                Assert.Equal(mode == "success", coordinator.Setup.Stations[0].Selection is not null);
                Assert.NotNull(new StationSetupStore(path).Load());
            }
            else { Assert.True(page.HasError); Assert.Same(setup, coordinator.Setup); }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rule_simulation_propagates_order_and_parameters_and_editor_reverts(bool orderMode, bool hasOrder)
    {
        var coordinator = new StationCoordinator(StationSetup.Default(SampleProjects.AcPruefung(), 1), new ResourceManager());
        var page = new SelectionPageViewModel(coordinator, null, null, () => [new AvailableTest("AC-Prüfung", "1", true, "released")], () => false, new QueueDispatcher());
        Assert.False(page.HasSimulation); Assert.Equal("—", page.SimulationTest); Assert.Equal("—", page.SimulationParameters);
        page.UseSelection = true; page.OrderSession = orderMode; page.UseCustom = false; page.UseRules = true; page.UseManual = false;
        page.SampleId = " A123 "; page.SampleOrder = hasOrder ? " FA1 " : "";
        page.AddRuleCommand.Execute(null); var row = page.Rules[0]; row.Priority = 2; row.Priority = 2; row.Pattern = " A* "; row.Test = " AC-Prüfung ";
        row.Version = "1"; row.Parameters = " Port=COM7 "; Assert.True(row.IsReleased); Assert.Equal("Ja", row.ReleasedText);
        await page.SimulateAsync(); Assert.True(page.SimulationOk); Assert.Contains("AC-Prüfung", page.SimulationTest); Assert.Contains("COM7", page.SimulationParameters);
        Assert.Equal(orderMode ? "A123" : hasOrder ? "FA1" : "—", page.SimulationOrder);
        row.Version = "missing"; Assert.False(row.IsReleased); Assert.Equal("Nein", row.ReleasedText); row.Version = "aktuelle freigegebene"; Assert.Null(row.Rule.Version);
        row.Version = null!; row.Parameters = null!; row.Parameters = ""; Assert.Null(row.Rule.Parameters); row.Pattern = null!; row.Test = null!;
        page.AddRuleCommand.Execute(null); Assert.Equal(3, page.Rules[1].Priority);
        page.RemoveRuleCommand.Execute(null); page.RemoveRuleCommand.Execute(null); page.RemoveRuleCommand.Execute(null); Assert.Empty(page.Rules);
        page.DiscardCommand.Execute(null); Assert.False(page.IsDirty); Assert.False(page.HasSimulation);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    public async Task Custom_selection_reports_missing_or_invalid_project(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-selection-custom-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "custom.tproj.json"); if (mode == "invalid") File.WriteAllText(path, "{invalid}");
            var page = new SelectionPageViewModel(new StationCoordinator(StationSetup.Default(SampleProjects.AcPruefung(), 1), new ResourceManager()), null, null, () => [], () => false, new QueueDispatcher());
            page.UseSelection = true; page.UseCustom = true; page.UseManual = false; page.UseRules = false; page.CustomProject = path;
            await page.SimulateAsync(); Assert.False(page.SimulationOk); Assert.True(page.HasSimulation); Assert.NotEmpty(page.SimulationText);
        }
        finally { Directory.Delete(directory, true); }
    }
}
