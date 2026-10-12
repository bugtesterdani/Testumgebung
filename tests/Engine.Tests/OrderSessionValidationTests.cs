using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class OrderSessionValidationTests
{
    [Theory]
    [InlineData("no-dut", "benötigt einen Prüflingstest")]
    [InlineData("no-serial", "eigenen String-Variable")]
    [InlineData("wrong-serial-type", "eigenen String-Variable")]
    [InlineData("no-scan", "verpflichtenden Werkereingabe")]
    [InlineData("group-first", "verpflichtenden Werkereingabe")]
    [InlineData("wrong-block", "verpflichtenden Werkereingabe")]
    [InlineData("wrong-target", "verpflichtenden Werkereingabe")]
    [InlineData("wrong-kind", "verpflichtenden Werkereingabe")]
    [InlineData("invalid-required", "verpflichtenden Werkereingabe")]
    [InlineData("not-required", "verpflichtenden Werkereingabe")]
    [InlineData("no-order", "String-Variable Auftrag")]
    [InlineData("wrong-order-type", "String-Variable Auftrag")]
    public async Task Order_start_requires_a_valid_serial_scan_and_root_order_variable(string damage, string expected)
    {
        var project = SampleProjects.AcPruefung();
        var dut = project.Definition.AllNodes().OfType<GroupDefinition>().Single(g => g.IsDutRun);
        var scan = (StepDefinition)dut.Children[0];
        switch (damage)
        {
            case "no-dut": dut.IsDutRun = false; break;
            case "no-serial": dut.Variables.RemoveAll(v => v.Name == dut.DutIdVariable); break;
            case "wrong-serial-type": dut.Variables.RemoveAll(v => v.Name == dut.DutIdVariable); dut.Variables.Add(new VariableDefinition(dut.DutIdVariable, VarType.Int)); break;
            case "no-scan": dut.Children.Clear(); break;
            case "group-first": dut.Children.Insert(0, new GroupDefinition("first", "first")); break;
            case "wrong-block": dut.Children[0] = BlockCatalog.Default.Create(BlockCatalog.SetRating, "first", "First", new Dictionary<string, string> { ["Bewertung"] = "PASS" }); break;
            case "wrong-target": scan.Parameters["Ziel"] = "other"; break;
            case "wrong-kind": scan.Parameters["Art"] = "Bestätigung"; break;
            case "missing-required": scan.Parameters.Remove("Pflicht"); break;
            case "invalid-required": scan.Parameters["Pflicht"] = "bad"; break;
            case "not-required": scan.Parameters["Pflicht"] = "false"; break;
            case "no-order": project.Definition.Root.Variables.RemoveAll(v => v.Name == "Auftrag"); break;
            case "wrong-order-type": project.Definition.Root.Variables.RemoveAll(v => v.Name == "Auftrag"); project.Definition.Root.Variables.Add(new VariableDefinition("Auftrag", VarType.Int)); break;
        }
        var setup = StationSetup.Default(project);
        setup.Stations[0].Selection = new SelectionConfig { Rules = [new SelectionRule { Pattern = "A*", Test = project.Definition.Name }] };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: new VirtualClock(), stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        await station.StartAsync(); ui.Pump();
        station.Selection.Auftrag = "A123";
        await station.Selection.AssignAsync(); ui.Pump(); Assert.True(station.Selection.CanStart);
        await station.Selection.StartSelectedAsync(); ui.Pump();
        Assert.False(station.IsRunning); Assert.Contains(expected, station.Error); Assert.Null(station.Run);
        station.RequestAbort(); await station.WaitForCompletionAsync(); ui.Pump();
    }
}
