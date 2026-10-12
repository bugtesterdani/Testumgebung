using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StationAssignmentUnitBoundaryTests
{
    [Theory]
    [InlineData("real", "V")]
    [InlineData("scalar", "A")]
    [InlineData("output", "Hz")]
    [InlineData("unknown", "—")]
    public void Resource_unit_comes_from_driver_then_project_simulation(string source, string expected)
    {
        var project = new TestProject(Kit.Test(Kit.Group("loop"))); project.Simulation.Add(new SimulatedResource("Scalar", "ch", "A", 1)); project.SimulationOutputs.Add(new SimulatedOutput("Output", "arb", "Hz", -1, 1, "1000", "L1"));
        var setup = StationSetup.Default(project, 1); setup.Devices.Add(new DeviceConfig { Id = "real", Name = "DMM", Adapter = Keysight3446xDmm.AdapterName, Model = "34461A", Resource = "fixture" });
        var page = new StationsViewModel(new StationCoordinator(setup, new ResourceManager()), new EditorViewModel(project), null, new QueueDispatcher(), () => false);
        var assignment = new ResourceAssignment { Name = source switch { "scalar" => "Scalar", "output" => "Output", _ => "Missing" }, Device = source == "real" ? "real" : "missing", Channel = source == "real" ? "VDC" : "unknown" };
        var row = new AssignmentRow(page, assignment); Assert.Equal(expected, row.Unit); Assert.Equal(expected, row.Unit);
        if (source == "real") { assignment.Channel = "bad"; Assert.Equal("—", row.Unit); }
    }
}
