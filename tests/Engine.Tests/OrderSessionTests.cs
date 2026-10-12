using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class OrderSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-order-" + Guid.NewGuid().ToString("N"));
    public OrderSessionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
    private static StepDefinition S(string block, string id, params (string Key, string Value)[] parameters)
        => BlockCatalog.Default.Create(block, id, id, parameters.ToDictionary(p => p.Key, p => p.Value));

    private static TestProject Program()
    {
        var dut = Group("dut").Var("Kennung", VarType.String);
        dut.IsDutRun = true;
        dut.Add(S(BlockCatalog.Operator, "scan", ("Art", "Eingabe"), ("Text", "Seriennummer scannen"),
            ("Datentyp", "Text"), ("Pflicht", "true"), ("Ziel", "Kennung")));
        var test = Test(Group("loop").Add(dut),
            Group("init").Add(S(BlockCatalog.SetValue, "start", ("Ziel", "StartAuftrag"), ("Ausdruck", "Auftrag"))),
            Group("fin").Add(S(BlockCatalog.SetValue, "end", ("Ziel", "EndAuftrag"), ("Ausdruck", "Auftrag"))));
        test.Root.Var("Auftrag", VarType.String).Var("StartAuftrag", VarType.String).Var("EndAuftrag", VarType.String);
        return new TestProject(test);
    }

    [Fact]
    public async Task Auftrag_vor_Start_Seriennummer_je_Loop_End_einmal_und_Ergebnisse_getrennt()
    {
        var serial = 0;
        var op = new ScriptedOperator().Answer("scan", _ => "SN-" + ++serial);
        var run = new TestRun(Program().Definition, new TestRunOptions
        {
            Operator = op, ContinuousLoop = true,
            SessionPresets = new Dictionary<string, object?> { ["Auftrag"] = "FA-42" },
        });
        var results = new List<ResultRun>();
        var recorder = new ResultRecorder(run);
        recorder.RunRecorded += results.Add;
        run.EventRaised += e => { if (e is DutRunCompleted && run.DutsCompleted == 2) run.RequestAbort(); };
        await run.RunAsync().Within();
        Assert.Equal(2, op.Asked.Count);
        Assert.Equal(new[] { "SN-1", "SN-2" }, results.Select(r => r.DutId));
        Assert.All(results, r => Assert.Equal("FA-42", r.Order));
        Assert.Equal(2, results.Select(r => r.RunId).Distinct().Count());
        Assert.Equal("FA-42", Assert.Single(run.Steps("start")).Value);
        Assert.Equal("FA-42", Assert.Single(run.Steps("end")).Value);
        Assert.Equal(RepeatMode.Once, run.Definition.Loop.Repetition.Mode);
        foreach (var result in results)
        {
            var path = Path.Combine(_dir, result.DutId + ".tres");
            using (var stream = File.Create(path)) ResultFile.Write(stream, result);
            var read = ResultFile.Read(path);
            Assert.True(read.IsIntact);
            Assert.Equal(result.Order, read.Run.Order);
            Assert.Equal(result.DutId, read.Run.DutId);
            Assert.Equal(result.Order, result.CopyWith("Test", [], [], []).Order);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CSharp_Auswahl_liest_Auftrag_bereits_im_Start_und_Fehler_blockiert_Auswahl(bool fail)
    {
        var source = "SelectTest(\"Beispiel\", null, Auftrag, null); return " + (fail ? "Error(\"Datenquelle fehlerhaft\");" : "Pass();");
        var selection = Test(Group("loop"), Group("init").Add(S(BlockCatalog.Code, "choose",
            ("Sprache", "C#"), ("Quelltext", source), ("Timeout", "30"))));
        selection.Root.Var("Auftrag", VarType.String).Var("Kennung", VarType.String);
        var (value, error) = await TestSelector.RunCustomAsync(new TestProject(selection), "FA-42", "FA-42",
            new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value }).WaitAsync(TimeSpan.FromMinutes(1));
        if (fail) { Assert.Null(value); Assert.NotNull(error); }
        else { Assert.Null(error); Assert.Equal("FA-42", value!.Order); }
    }

    [Fact]
    public async Task Auftragsscan_startet_Sitzung_Abbruch_erfordert_neuen_Auftrag_und_Script_laeuft_erneut()
    {
        var project = Program();
        var selection = Test(Group("loop"), Group("init").Add(S(BlockCatalog.Code, "choose",
            ("Sprache", "C#"), ("Quelltext", "if (!Auftrag.StartsWith(\"FA-\")) return Error(\"Unbekannter Auftrag\"); SelectTest(\"Beispiel\", null, Auftrag, \"Kennung=FALSCH\"); return Pass();"), ("Timeout", "30"))));
        selection.Root.Var("Auftrag", VarType.String).Var("Kennung", VarType.String);
        var path = Path.Combine(_dir, "selection.tproj.json");
        new ProjectSerializer().Save(new TestProject(selection), path);
        var setup = StationSetup.Default(project, 1);
        setup.Stations[0].Selection = new SelectionConfig { UseCustom = true, UseRules = false, UseManual = true, CustomProject = path };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance,
            stations: new StationCoordinator(setup, new ResourceManager()), codeRuntime: CodeBlockTests.SharedRuntime.Value);
        var station = shell.SelectedStation;
        var panel = station.Selection;
        Assert.True(panel.ShowPanel);
        panel.ScanText = "UNKNOWN";
        await panel.ProcessScanAsync().WaitAsync(TimeSpan.FromMinutes(1));
        ui.Pump();
        Assert.True(panel.HasProblem);
        Assert.False(panel.ShowManual); // Fehler im Script darf nicht umgangen werden.
        Assert.False(station.IsRunning);
        for (var order = 1; order <= 2; order++)
        {
            panel.ScanText = "FA-" + order;
            var session = panel.ProcessScanAsync();
            await ui.Until(() => station.ActiveContent is { IsAwaiting: true, Title: "scan" }, "Seriennummer");
            Assert.Equal("", station.ActiveContent!.Input);
            Assert.Equal("FA-" + order, station.CurrentOrder);
            station.ActiveContent.Input = "SN-" + order;
            station.ActiveContent.Confirm();
            await ui.Until(() => panel.CompletedDuts == order && station.ActiveContent is { IsAwaiting: true, Title: "scan" }, "next serial number");
            Assert.True(station.IsRunning);
            station.RequestAbort();
            await session.Within();
            await station.WaitForCompletionAsync().Within();
            ui.Pump();
            Assert.True(panel.ShowPanel);
            Assert.Equal("", panel.ScanText);
            Assert.Null(panel.Outcome);
            Assert.False(panel.IsActive);
            Assert.False(station.IsRunning);
        }
    }

    [SqliteFact]
    public async Task Datenbank_speichert_Auftrag_und_Seriennummer_auch_nach_Schemamigration()
    {
        var connectionString = "Data Source=" + Path.Combine(_dir, "orders.db");
        await using (var connection = NativeSqliteFactory.Instance.CreateConnection()!)
        {
            connection.ConnectionString = connectionString;
            await connection.OpenAsync();
            var dialect = new SqliteDialect();
            await using (var createVersion = connection.CreateCommand())
            {
                createVersion.CommandText = dialect.CreateVersionTable;
                await createVersion.ExecuteNonQueryAsync();
            }
            foreach (var (version, statements) in dialect.Migrations.Where(m => m.Version < 3))
            {
                foreach (var sql in statements)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync();
                }
                await using var mark = connection.CreateCommand();
                mark.CommandText = $"INSERT INTO tu_schema_version (version, applied_utc) VALUES ({version}, 0)";
                await mark.ExecuteNonQueryAsync();
            }
        }
        var db = new SqlRunDatabase(NativeSqliteFactory.Instance, connectionString, new SqliteDialect());
        var result = new ResultRun { RunId = "R1", DutId = "SN-42", Order = "FA-42", TestName = "Beispiel", TestVersion = "1.0", Started = DateTimeOffset.Now, Ended = DateTimeOffset.Now };
        await db.StoreAsync(result, default);
        var stored = await db.LoadRunAsync(result.RunId, default);
        Assert.Equal("FA-42", stored!.Order);
        Assert.Equal("SN-42", stored.DutId);
    }

    [Fact]
    public async Task Dateibeispiel_liest_Daten_waehlt_A_oder_B_und_blockiert_unbekannte_Auftraege()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var examples = Path.Combine(root.FullName, "docs", "beispiele");
        var serializer = new ProjectSerializer();
        var project = serializer.Load(Path.Combine(examples, "auswahl-auftragsdatei.tproj.json"));
        var source = project.Definition.Init.Children.OfType<StepDefinition>().Single().Parameters["Quelltext"];
        var path = Path.Combine(_dir, "orders.json");
        File.Copy(Path.Combine(examples, "fertigungsauftraege.beispiel.json"), path);
        var expression = "System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), \"Testumgebung\", \"fertigungsauftraege.json\")";
        source = source.Replace(expression, System.Text.Json.JsonSerializer.Serialize(path));
        project.Definition.Init.Children.Clear();
        project.Definition.Init.Add(S(BlockCatalog.Code, "file-choice", ("Sprache", "C#"), ("Quelltext", source), ("Timeout", "30")));
        foreach (var (order, expected) in new[] { ("FA-100", "Programm A"), ("FA-200", "Programm B"), ("FA-UNKNOWN", (string?)null) })
        {
            var (value, error) = await TestSelector.RunCustomAsync(project, order, order,
                new TestRunOptions { CodeRuntime = CodeBlockTests.SharedRuntime.Value }).WaitAsync(TimeSpan.FromMinutes(1));
            if (expected is null) { Assert.Null(value); Assert.NotNull(error); }
            else { Assert.Null(error); Assert.Equal(expected, value!.Test); Assert.Equal(order, value.Order); }
        }
        foreach (var name in new[] { "programm-a.tproj.json", "programm-b.tproj.json" })
        {
            var program = serializer.Load(Path.Combine(examples, name)).Definition;
            Assert.Empty(program.Validate());
            Assert.Equal(RepeatMode.Endless, program.Loop.Repetition.Mode);
            Assert.Contains(program.Loop.Descendants().OfType<GroupDefinition>(), d => d.IsDutRun && d.Variables.Any(v => v.Name == d.DutIdVariable));
        }
    }

    [Fact]
    public async Task Abbruch_waehrend_Auftragsauswahl_startet_keinen_Test_und_leert_den_Auftrag()
    {
        var project = Program();
        var path = Path.Combine(_dir, "waiting-selection.tproj.json");
        new ProjectSerializer().Save(new TestProject(Test(Group("loop").Add(S(BlockCatalog.Wait, "wait", ("Dauer", "60000"))))), path);
        var setup = StationSetup.Default(project, 1);
        setup.Stations[0].Selection = new SelectionConfig { UseCustom = true, CustomProject = path };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance,
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        station.Selection.ScanText = "FA-42";
        var assignment = station.Selection.ProcessScanAsync();
        Assert.False(assignment.IsCompleted);
        station.RequestAbort();
        await Task.WhenAll(assignment, station.WaitForCompletionAsync()).Within();
        ui.Pump();
        Assert.False(station.IsRunning);
        Assert.False(station.Selection.IsActive);
        Assert.True(station.Selection.ShowPanel);
        Assert.Equal("", station.Selection.ScanText);
        Assert.Null(station.Selection.Outcome);
    }

    [Theory]
    [InlineData(true, "A")]
    [InlineData(false, "A")]
    [InlineData(true, "B")]
    [InlineData(true, "C")]
    [InlineData(true, "D")]
    public async Task Auftrag_startet_Programm_per_Start_oder_Enter_und_prueft_mehrere_Seriennummern(bool startButton, string program)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var example = OrderExample.Load(Path.Combine(root.FullName, "docs", "beispiele"));
        var ui = new QueueDispatcher();
        var resultFolder = Path.Combine(_dir, "A123-results");
        var shell = new ShellViewModel(ui, project: example.Project, clock: SystemClock.Instance,
            stations: new StationCoordinator(example.Setup, new ResourceManager()), codeRuntime: CodeBlockTests.SharedRuntime.Value,
            results: new ResultPipeline(new ResultFileStore(resultFolder), null));
        var station = shell.SelectedStation;
        var order = program + "123";
        station.Selection.ScanText = order;
        var session = startButton ? station.StartAsync() : station.Selection.ProcessScanAsync();
        await ui.Until(() => station.ActiveContent is { IsAwaiting: true, Title: "Seriennummer erfassen" }, "A123 Programm A");
        Assert.Equal("Programm " + program, station.Run!.Definition.Name);
        Assert.Equal(order, station.CurrentOrder);
        for (var i = 1; i <= 2; i++)
        {
            station.ActiveContent!.Input = order + "-SN-" + i;
            station.ActiveContent.Confirm();
            await ui.Until(() => station.Selection.CompletedDuts == i && station.ActiveContent is { IsAwaiting: true }, "next serial");
        }
        station.RequestAbort();
        await session.Within();
        ui.Pump();
        var recorded = Directory.GetFiles(resultFolder, "*.tres", SearchOption.AllDirectories).Select(p => ResultFile.Read(p).Run).ToList();
        Assert.Contains(recorded, r => r.DutId == order + "-SN-1" && r.Order == order);
        Assert.Contains(recorded, r => r.DutId == order + "-SN-2" && r.Order == order);
        Assert.Single(station.Run.Events.OfType<StepCompleted>(), e => e.NodeId == program.ToLowerInvariant() + "-info");
        Assert.Single(station.Run.Events.OfType<StepCompleted>(), e => e.NodeId == program.ToLowerInvariant() + "-end");
        Assert.True(station.Selection.ShowPanel);
        Assert.Equal("", station.Selection.ScanText);
        var nextProgram = program == "D" ? "A" : ((char)(program[0] + 1)).ToString();
        station.Selection.ScanText = nextProgram + "123";
        var nextSession = station.StartAsync();
        await ui.Until(() => station.ActiveContent is { IsAwaiting: true, Title: "Seriennummer erfassen" }, "program switch");
        Assert.Equal("Programm " + nextProgram, station.Run!.Definition.Name);
        Assert.Equal(nextProgram + "123", station.CurrentOrder);
        station.RequestAbort();
        await nextSession.Within();
        ui.Pump();
        Assert.True(station.Selection.ShowPanel);

    }
}

