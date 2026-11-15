using System.IO.Compression;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Hardware;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class LimitSetTests
{
    private static TestProject Project()
    {
        var project = SampleProjects.AcPruefung();
        project.Definition.Root.Variables.Add(new("Grenze", VarType.Double));
        project.Definition.Root.Variables.Add(new("Aktiv", VarType.Bool));
        return project;
    }

    [Fact]
    public void Csv_import_round_trips_in_release_and_participates_in_comparison()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        try
        {
            File.WriteAllText(path, "Variante;Grenze\nStandard;2,5\nXL;4\n");
            var project = Project();
            var before = new ProjectSerializer().Clone(project);
            foreach (var (name, values) in LimitSetTable.Import(project, path))
                project.LimitSets.Add(name, values);
            var (bytes, manifest) = ReleasePackage.Build(project, "test", DateTimeOffset.UnixEpoch);
            var opened = ReleasePackage.Open(bytes);
            Assert.True(opened.IsIntact);
            Assert.Contains(manifest.Entries, entry => entry.Name == "Grenzwertsatz: Standard");
            Assert.Equal(2.5, Assert.IsType<double>(LimitSetTable.Resolve(opened.Project, "Standard")["Grenze"]));
            Assert.Equal(4.0, Assert.IsType<double>(LimitSetTable.Resolve(opened.Project, "XL")["Grenze"]));
            Assert.Contains(ProjectDiff.Compare(before, opened.Project), entry => entry.Area == "Grenzwertsätze" && entry.Kind == DiffKind.Added);
            opened.Project.LimitSets["Standard"]["Grenze"] = "3";
            Assert.Contains(ProjectDiff.Compare(project, opened.Project), entry => entry.Item == "Standard" && entry.Kind == DiffKind.Changed);
            Assert.False(DefinitionCompatibility.CanSwapAtDut(project, opened.Project, out var reason));
            Assert.Contains("Grenzwertsätze", reason);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Excel_import_reads_text_variant_names_and_more_than_ten_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xlsx");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write("xl/workbook.xml", """
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Grenzen" r:id="r1"/></sheets></workbook>
                    """);
                Write("xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Target="worksheets/sheet1.xml"/></Relationships>
                    """);
                var rows = string.Concat(Enumerable.Range(1, 12).Select(index =>
                    $"<row><c t='inlineStr'><is><t>V{index}</t></is></c><c><v>{index}</v></c><c t='b'><v>{index % 2}</v></c></row>"));
                Write("xl/worksheets/sheet1.xml", $"""
                    <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                      <row><c t="inlineStr"><is><t>Variante</t></is></c><c t="inlineStr"><is><t>Grenze</t></is></c><c t="inlineStr"><is><t>Aktiv</t></is></c></row>
                      {rows}
                    </sheetData></worksheet>
                    """);

                void Write(string entry, string content)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                    writer.Write(content);
                }
            }
            var project = Project();
            var sets = LimitSetTable.Import(project, path);
            Assert.Equal(12, sets.Count);
            Assert.Equal("12", sets["V12"]["Grenze"]);
            foreach (var (name, values) in sets) project.LimitSets.Add(name, values);
            Assert.True(Assert.IsType<bool>(LimitSetTable.Resolve(project, "V1")["Aktiv"]));
            Assert.False(Assert.IsType<bool>(LimitSetTable.Resolve(project, "V12")["Aktiv"]));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("Standard;1\nStandard;2", "mehrfach")]
    [InlineData("Standard;NaN", "Endliche Zahl")]
    [InlineData("Standard;", "Wert fehlt")]
    public void Invalid_table_does_not_replace_existing_values(string rows, string message)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var project = Project();
        project.LimitSets.Add("Installiert", new() { ["Grenze"] = "8" });
        try
        {
            File.WriteAllText(path, "Variante;Grenze\n" + rows);
            Assert.Contains(message, Assert.Throws<ProjectFormatException>(() => LimitSetTable.Import(project, path)).Message);
            Assert.Single(project.LimitSets);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Editor_simulation_uses_an_explicit_variant_and_clears_a_removed_choice()
    {
        var definition = new TestDefinition("Simulation", "1", new("init", "Init"), new("loop", "Loop"), new("exit", "Ende"));
        definition.Root.Variables.Add(new("Grenze", VarType.Double));
        var project = new TestProject(definition);
        var ui = new QueueDispatcher();
        var session = new SessionService(new SimulatedIdentityProvider(() => "test"), null);
        var shell = new ShellViewModel(ui, project: project, clock: new VirtualClock(),
            identity: new IdentityServices(session, null, _ => new SimulatedIdentityProvider(() => "test")));
        await shell.LoginDevelopmentAsync();
        ui.Pump();
        Assert.True(shell.StartSimulationCommand.CanExecute(null));

        project.LimitSets.Add("Standard", new() { ["Grenze"] = "2.5" });
        shell.Editor.MarkChanged();
        Assert.Equal("Standard", Assert.Single(shell.SimulationVariants));
        Assert.False(shell.StartSimulationCommand.CanExecute(null));
        shell.SimulationVariant = "Standard";
        Assert.True(shell.StartSimulationCommand.CanExecute(null));
        shell.StartSimulationCommand.Execute(null);
        await shell.SelectedStation.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        ui.Pump();
        Assert.Null(shell.SelectedStation.Error);
        Assert.Contains(shell.SelectedStation.Protocol, row => row.Text.Contains("Grenze = 2.5"));

        project.LimitSets.Clear();
        shell.Editor.MarkChanged();
        Assert.Null(shell.SimulationVariant);
        Assert.Empty(shell.SimulationVariants);
        Assert.True(shell.StartSimulationCommand.CanExecute(null));
    }

    [Fact]
    public async Task Selected_variant_sets_root_and_dut_values_before_execution()
    {
        var definition = new TestDefinition("Variantenprüfung", "1", new("init", "Init"), new("loop", "Loop"), new("exit", "Ende"));
        var project = new TestProject(definition);
        definition.Root.Variables.Add(new("Grenze", VarType.Double));
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true };
        dut.Variables.Add(new("Kennung", VarType.String));
        dut.Variables.Add(new("DutGrenze", VarType.Double));
        definition.Loop.Children.Add(dut);
        project.LimitSets.Add("Standard", new() { ["Grenze"] = "2.5", ["DutGrenze"] = "4" });
        var setup = StationSetup.Default(project);
        setup.Stations[0].Selection = new SelectionConfig
        {
            OrderSession = false,
            Rules = [new SelectionRule { Pattern = "A*", Test = definition.Name, Parameters = "Variante=Standard;Grenze=10;DutGrenze=20" }],
        };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: new VirtualClock(),
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        await station.StartAsync();
        ui.Pump();
        station.Selection.Kennung = "A123";
        await station.Selection.AssignAsync();
        ui.Pump();
        await station.Selection.StartSelectedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        ui.Pump();
        Assert.NotNull(station.Run);
        Assert.Contains(station.Protocol, row => row.Text.Contains("Grenze = 2.5"));
        Assert.Contains(station.Protocol, row => row.Text.Contains("DutGrenze = 4"));
    }

    [Theory]
    [InlineData("", "Kein Grenzwertsatz gewählt")]
    [InlineData("Variante=Falsch", "Grenzwertsatz „Falsch“ fehlt")]
    public async Task Selected_test_is_blocked_and_reports_error_if_variant_is_missing(string parameters, string message)
    {
        var project = Project();
        project.LimitSets.Add("Standard", new() { ["Grenze"] = "2.5" });
        var setup = StationSetup.Default(project);
        setup.Stations[0].Selection = new SelectionConfig
        {
            OrderSession = false,
            Rules = [new SelectionRule { Pattern = "A*", Test = project.Definition.Name, Parameters = parameters }],
        };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: new VirtualClock(),
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        await station.StartAsync();
        ui.Pump();
        station.Selection.Kennung = "A123";
        await station.Selection.AssignAsync();
        ui.Pump();
        Assert.True(station.Selection.CanStart);
        await station.Selection.StartSelectedAsync();
        ui.Pump();
        Assert.False(station.IsRunning);
        Assert.Null(station.Run);
        Assert.Contains(message, station.Error);
        Assert.Contains(station.Protocol, row => row.Rating == ResultRating.Error && row.Value.Contains(message));
    }
}
