using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Abschnitt 5 – Auswahl- und Auftragsablauf: Kennungsregeln (Priorität, Mehrdeutigkeit), manuelle Auswahl, eigener
/// Auswahlablauf mit „Test auswählen“, Vorbelegung und genau ein Prüfling je Zuordnung, Werkerabbruch beendet die Auswahl.
/// </summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class SelectionTests : IDisposable
{
    private const string P1 = "Prüfplatz 01";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-sel-" + Guid.NewGuid().ToString("N")[..8]);

    public SelectionTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Abbruch_beendet_auch_laufenden_eigenen_Auswahlablauf_und_wartet_darauf()
    {
        var project = SampleProjects.AcPruefung();
        var customPath = Path.Combine(_dir, "custom.tproj.json");
        var custom = new TestProject(Test(Group("loop").Add(S(BlockCatalog.Wait, "wait", "Warten", ("Dauer", "60000")))));
        new ProjectSerializer().Save(custom, customPath);
        var setup = StationSetup.Default(project, 1);
        setup.Station(P1)!.Selection = new SelectionConfig { OrderSession = false, UseCustom = true, CustomProject = customPath };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance,
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        await station.StartAsync();
        station.Selection.Kennung = "SN-1";
        var assignment = station.Selection.AssignAsync();
        var completion = station.WaitForCompletionAsync();
        Assert.False(completion.IsCompleted);
        station.RequestAbort();
        await Task.WhenAll(assignment, completion).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(station.Selection.IsActive);
        Assert.False(station.Selection.CanStart);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static readonly AvailableTest Ac15 = new("AC-Prüfung", "1.5", true, "freigegeben");
    private static readonly AvailableTest Ac14 = new("AC-Prüfung", "1.4", false, "früher freigegeben");
    private static readonly AvailableTest Dc20 = new("DC-Prüfung", "2.0", true, "freigegeben");
    private static readonly IReadOnlyList<AvailableTest> Tests = [Ac15, Ac14, Dc20];

    private static StepDefinition S(string type, string id, string name, params (string Key, string Value)[] p)
        => BlockCatalog.Default.Create(type, id, name, p.ToDictionary(x => x.Key, x => x.Value));

    [Theory]
    [InlineData("SN-10482", "SN-*", true)]
    [InlineData("sn-10482", "SN-*", true)]
    [InlineData("SN-1048A", "SN-####", false)]
    [InlineData("SN-1048", "SN-####", true)]
    [InlineData("AB12", "A?1#", true)]
    [InlineData("DC-77", "re:^DC-\\d+$", true)]
    [InlineData("DC-7x", "re:^DC-\\d+$", false)]
    [InlineData("X", "re:[", false)]
    public void Kennungsregel_Muster(string id, string pattern, bool expected)
        => Assert.Equal(expected, new SelectionRule { Pattern = pattern, Test = "x" }.Matches(id));

    [Fact]
    public void Regeln_Prioritaet_Version_Parameter_Mehrdeutigkeit_und_manuelle_Auswahl()
    {
        var config = new SelectionConfig
        {
            Rules =
            [
                new SelectionRule { Priority = 2, Pattern = "SN-*", Test = "AC-Prüfung" },
                new SelectionRule { Priority = 1, Pattern = "SN-9*", Test = "AC-Prüfung", Version = "1.4", Parameters = "GrenzwertMin=200; GrenzwertMax=250" },
                new SelectionRule { Priority = 1, Pattern = "DC-*", Test = "DC-Prüfung" },
                new SelectionRule { Priority = 1, Pattern = "DC-X*", Test = "AC-Prüfung" },
                new SelectionRule { Priority = 1, Pattern = "OLD-*", Test = "AC-Prüfung", Version = "0.9" },
            ],
        };

        var a = TestSelector.Select(config, "SN-10482", "FA-1", Tests);
        Assert.True(a.CanStart);
        Assert.Same(Ac15, a.Test);
        Assert.Equal("FA-1", a.Order);
        Assert.StartsWith("Kennungsregel SN-*", a.Source);

        var b = TestSelector.Select(config, "SN-900", null, Tests);
        Assert.Same(Ac14, b.Test);
        Assert.Equal("200", b.Parameters["GrenzwertMin"]);
        Assert.Equal("250", b.Parameters["GrenzwertMax"]);

        var c = TestSelector.Select(config, "DC-X1", null, Tests);
        Assert.Equal(SelectionState.Ambiguous, c.State);
        Assert.False(c.CanStart);
        Assert.StartsWith("Keine eindeutige Testzuordnung", c.Message);
        Assert.Equal([Dc20, Ac15], c.Candidates);
        var manual = TestSelector.Manual(c, Dc20, "DC-X1");
        Assert.True(manual.CanStart);
        Assert.Equal("Manuell ausgewählt", manual.Source);

        var d = TestSelector.Select(config, "OLD-1", null, Tests);
        Assert.Equal(SelectionState.None, d.State);
        Assert.Contains("nicht freigegeben", d.Message);

        // Keine Regel: mit manueller Auswahl → Kandidaten = aktuelle Freigaben; ohne → kein Start
        var e = TestSelector.Select(config, "ZZ-1", null, Tests);
        Assert.Equal(SelectionState.Ambiguous, e.State);
        Assert.Equal([Ac15, Dc20], e.Candidates);
        config.UseManual = false;
        var f = TestSelector.Select(config, "ZZ-1", null, Tests);
        Assert.Equal(SelectionState.None, f.State);
        Assert.Empty(f.Candidates);
        Assert.Throws<InvalidOperationException>(() => TestSelector.Manual(f, Ac15, "ZZ-1"));

        Assert.Equal(SelectionState.None, TestSelector.Select(config, "  ", null, Tests).State);
    }

    [Fact]
    public void Eigener_Auswahlablauf_hat_Vorrang_und_Fehler_verhindern_den_Start()
    {
        var config = new SelectionConfig { UseCustom = true, Rules = [new SelectionRule { Pattern = "*", Test = "AC-Prüfung" }] };
        var ok = TestSelector.Select(config, "SN-1", "FA-1", Tests, new TestSelectionValue("DC-Prüfung", null, "FA-9", "Strom=2"));
        Assert.Same(Dc20, ok.Test);
        Assert.Equal("FA-9", ok.Order);
        Assert.Equal("Eigener Auswahlablauf", ok.Source);
        Assert.Equal("2", ok.Parameters["Strom"]);

        var unknown = TestSelector.Select(config, "SN-1", null, Tests, new TestSelectionValue("XY", "1.0", null, null));
        Assert.False(unknown.CanStart);
        Assert.Contains("nicht freigegeben", unknown.Message);

        var error = TestSelector.Select(config, "SN-1", null, Tests, null, "Datenbank nicht erreichbar");
        Assert.False(error.CanStart);
        Assert.Contains("Datenbank nicht erreichbar", error.Message);
    }

    [Fact]
    public async Task Eigener_Auswahlablauf_liefert_Test_ueber_Baustein()
    {
        var dut = new GroupDefinition("auswahl", "Auswahl") { IsDutRun = true }
            .Var("Kennung", VarType.String)
            .Var("Auftrag", VarType.String)
            .Add(S(BlockCatalog.SelectTest, "wahl", "Test auswählen",
                ("Testkennung", "\"DC-Prüfung\""), ("Auftrag", "Auftrag + \"/\" + Kennung"), ("Pruefparameter", "\"Strom=2\"")));
        var loop = Group("loop", "Test Loop / Testing");
        loop.Repetition = Repetition.Endless;
        loop.Add(dut);
        var project = new TestProject(Test(loop));
        var (value, error) = await TestSelector.RunCustomAsync(project, "SN-5", "FA-7", new TestRunOptions { Clock = new VirtualClock() }).Within();
        Assert.Null(error);
        Assert.Equal(new TestSelectionValue("DC-Prüfung", null, "FA-7/SN-5", "Strom=2"), value);

        // Ohne Baustein → Fehlertext
        var empty = new GroupDefinition("leer", "Auswahl") { IsDutRun = true }.Var("Kennung", VarType.String);
        var (none, why) = await TestSelector.RunCustomAsync(new TestProject(Test(Group("loop").Add(empty))), "SN-5", null,
            new TestRunOptions { Clock = new VirtualClock() }).Within();
        Assert.Null(none);
        Assert.Contains("Test auswählen", why);
    }

    [Fact]
    public async Task Eigener_Auswahlablauf_nur_mit_einem_CodeBlock()
    {
        const string csharp = """
            if (Kennung.StartsWith("AB", StringComparison.OrdinalIgnoreCase))
                SelectTest("Testprogramm X", "1.0", Auftrag + " / Art 12345", "Artikel=12345");
            else
                SelectTest("Standardprüfung", null, Auftrag, null);
            return NResult();
            """;
        var baseOptions = new TestRunOptions { Clock = new VirtualClock(), CodeRuntime = CodeBlockTests.SharedRuntime.Value };

        async Task<(TestSelectionValue? Value, string? Error)> RunAsync(string language, string source, string id)
        {
            var dut = new GroupDefinition("auswahl", "Auswahl") { IsDutRun = true }
                .Var("Kennung", VarType.String).Var("Auftrag", VarType.String)
                .Add(S(BlockCatalog.Code, "code", "Auswahl per Code", ("Sprache", language), ("Quelltext", source), ("Timeout", "30")));
            var loop = Group("loop", "Test Loop / Testing");
            loop.Repetition = Repetition.Endless;
            loop.Add(dut);
            return await TestSelector.RunCustomAsync(new TestProject(Test(loop)), id, "FA-1", baseOptions).WaitAsync(TimeSpan.FromMinutes(2));
        }

        var (ab, abErr) = await RunAsync("C#", csharp, "AB-9");
        Assert.Null(abErr);
        Assert.Equal(new TestSelectionValue("Testprogramm X", "1.0", "FA-1 / Art 12345", "Artikel=12345"), ab);

        var (other, otherErr) = await RunAsync("C#", csharp, "XY-1");
        Assert.Null(otherErr);
        Assert.Equal(new TestSelectionValue("Standardprüfung", null, "FA-1", null), other);

        // Gleiche Entscheidung in Python – select_test(test, version, order, parameters)
        const string python = """
            if Kennung.startswith("AB"):
                select_test("Testprogramm X", "1.0", Auftrag + " / Art 12345", "Artikel=12345")
            else:
                select_test("Standardprüfung", None, Auftrag, None)
            return NResult()
            """;
        var (py, pyErr) = await RunAsync("Python", python, "AB-2");
        Assert.Null(pyErr);
        Assert.Equal(new TestSelectionValue("Testprogramm X", "1.0", "FA-1 / Art 12345", "Artikel=12345"), py);
    }

    [Fact]
    public async Task Auswahl_entscheidet_anhand_des_einmal_gesetzten_Auftrags()
    {
        // Szenario des Beispiels docs/beispiele/auswahl-auftrag.tproj.json: Der Auftrag wird EINMAL am Anfang gesetzt
        // (als Vorbelegung, hier über RunCustomAsync) und vom CodeBlock nur GELESEN – kein Ask pro Prüfling.
        const string csharp = """
            string auftrag = (Auftrag ?? "").Trim();
            if (auftrag.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                SelectTest("Programm A", null, auftrag, "Artikel=11111");
            else if (auftrag.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                SelectTest("Programm B", null, auftrag, "Artikel=22222");
            else
                return Error("Kein Testprogramm für Auftrag '" + auftrag + "'.");
            return Pass();
            """;
        var baseOptions = new TestRunOptions { Clock = new VirtualClock(), CodeRuntime = CodeBlockTests.SharedRuntime.Value };

        async Task<(TestSelectionValue? Value, string? Error)> RunAsync(string auftrag, string id)
        {
            var dut = new GroupDefinition("auswahl", "Auswahl") { IsDutRun = true }
                .Var("Kennung", VarType.String).Var("Auftrag", VarType.String)
                .Add(S(BlockCatalog.Code, "code", "Auswahl per Code", ("Sprache", "C#"), ("Quelltext", csharp), ("Timeout", "30")));
            var loop = Group("loop", "Test Loop / Testing");
            loop.Repetition = Repetition.Endless;
            loop.Add(dut);
            return await TestSelector.RunCustomAsync(new TestProject(Test(loop)), id, auftrag, baseOptions).WaitAsync(TimeSpan.FromMinutes(2));
        }

        // Verschiedene Kennungen (Scans), gleicher Auftrag -> dasselbe Programm. Der Auftrag entscheidet, nicht die Kennung.
        var (a1, e1) = await RunAsync("A-100", "SN-1");
        Assert.Null(e1);
        Assert.Equal(new TestSelectionValue("Programm A", null, "A-100", "Artikel=11111"), a1);

        var (a2, e2) = await RunAsync("A-100", "SN-2");
        Assert.Null(e2);
        Assert.Equal(new TestSelectionValue("Programm A", null, "A-100", "Artikel=11111"), a2);

        var (b1, e3) = await RunAsync("B-200", "SN-3");
        Assert.Null(e3);
        Assert.Equal(new TestSelectionValue("Programm B", null, "B-200", "Artikel=22222"), b1);

        // Leerer Auftrag -> klarer Fehler, kein Start (statt stiller Falschauswahl).
        var (none, e4) = await RunAsync("", "SN-4");
        Assert.Null(none);
        Assert.NotNull(e4);
    }

    [Fact]
    public async Task Vorbelegung_und_Prueflingsanzahl_in_der_Engine()
    {
        var seen = new List<string>();
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }
            .Var("Kennung", VarType.String)
            .Var("Grenze", VarType.Double, 1.0)
            .Add(Steps.Code("lesen", "lesen", ctx =>
            {
                lock (seen) seen.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ctx.Get("Kennung")}|{ctx.Get("Grenze")}"));
                return StepOutcome.Unrated(null);
            }));
        var loop = Group("loop");
        loop.Repetition = Repetition.Endless;
        loop.Add(dut);
        var run = new TestRun(Test(loop), new TestRunOptions
        {
            Clock = new VirtualClock(),
            DutLimit = 1,
            Presets = new Dictionary<string, object?> { ["Kennung"] = "SN-P", ["Grenze"] = "2.5" },
        });
        await run.RunAsync().Within();
        Assert.Equal(1, run.DutsCompleted);
        Assert.Equal(["SN-P|2.5"], seen);
        Assert.Single(run.Events.OfType<DutRunCompleted>());
        Assert.Contains(run.Events.OfType<Diagnostic>(), d => d.Message.Contains("Aus der Auswahl übernommen"));
    }

    [Fact]
    public async Task Werkerpanel_Kennung_zuordnen_ein_Pruefling_mit_Vorbelegung_und_Werkerabbruch()
    {
        var ui = new QueueDispatcher();
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project, 1);
        setup.Station(P1)!.Selection = new SelectionConfig
        {
            OrderSession = false,
            Rules = [new SelectionRule { Pattern = "SN-*", Test = "AC-Prüfung", Parameters = "GrenzwertMin=200" }],
        };
        var coordinator = new StationCoordinator(setup, new ResourceManager());
        var store = new StationSetupStore(Path.Combine(_dir, "pruefplaetze.json"));
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance, stations: coordinator, stationStore: store);
        var s = shell.SelectedStation;
        var sel = s.Selection;
        Assert.True(s.UsesSelection);

        // Start öffnet die Erfassung; ohne Zuordnung kein Start
        await s.StartAsync();
        ui.Pump();
        Assert.True(sel.IsActive);
        Assert.True(sel.ShowPanel);
        Assert.False(s.IsRunning);
        Assert.False(sel.CanStart);

        // Unbekannte Kennung → manuelle Auswahl
        sel.Kennung = "XY-1";
        await sel.AssignAsync();
        Assert.True(sel.HasProblem);
        Assert.True(sel.ShowManual);
        sel.ManualChoice = sel.Candidates.Single();
        ui.Pump();
        Assert.True(sel.IsUnique);
        Assert.Equal("Manuell ausgewählt", sel.SourceText);

        // Regel → eindeutig; Start prüft genau einen Prüfling, „Kennung erfassen“ wird übersprungen
        sel.Kennung = "SN-77";
        sel.Auftrag = "FA-5";
        await sel.AssignAsync();
        Assert.True(sel.IsUnique);
        Assert.Equal("GrenzwertMin = 200", sel.ParametersText);
        var start = s.StartAsync();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true }, "erste Abfrage");
        Assert.Equal("Sichtprüfung", s.ActiveContent!.Title);
        s.ActiveContent.ChooseCommand.Execute("i. O.");
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Gehäusetemperatur erfassen" }, "Temp");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Prüfling anschließen" }, "Anschluss");
        s.ActiveContent!.Confirm();
        await start.Within();
        await ui.Until(() => !s.IsRunning && sel.CompletedDuts == 1, "ein Prüfling");
        Assert.True(sel.IsActive);
        Assert.Equal("", sel.Kennung);
        Assert.Equal("FA-5", sel.Auftrag);
        Assert.StartsWith("SN-77: AC-Prüfung v1.5", sel.LastResult);
        Assert.Contains(s.Protocol, p => p.Text == "Auswahl · Kennung zugeordnet");

        // Werkerabbruch beendet die Auswahl
        Assert.True(s.CanAbort);
        s.RequestAbort();
        ui.Pump();
        Assert.False(sel.IsActive);
        Assert.Contains(s.Protocol, p => p.Text == "Auswahl-/Auftragsablauf beendet");
    }

    [Fact]
    public async Task Werkerpanel_zeigt_Erfassung_pro_Pruefling_erneut_fuer_den_Scanner()
    {
        // Sichert das Signal ab, auf das der Auto-Fokus des Kennungsfeldes reagiert: ShowPanel ist während der Prüfung
        // aus (Feld unsichtbar), danach wieder an, und ShowPanel wird pro Prüfling erneut gemeldet – so bekommt das
        // Kennungsfeld bei jedem Prüfling wieder den Fokus (Scannerbetrieb).
        var ui = new QueueDispatcher();
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project, 1);
        setup.Station(P1)!.Selection = new SelectionConfig
        {
            OrderSession = false,
            Rules = [new SelectionRule { Pattern = "SN-*", Test = "AC-Prüfung" }],
        };
        var coordinator = new StationCoordinator(setup, new ResourceManager());
        var store = new StationSetupStore(Path.Combine(_dir, "pruefplaetze.json"));
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance, stations: coordinator, stationStore: store);
        var s = shell.SelectedStation;
        var sel = s.Selection;

        var showPanelRaised = 0;
        sel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(sel.ShowPanel)) showPanelRaised++; };

        await s.StartAsync();
        ui.Pump();
        Assert.True(sel.ShowPanel);                 // Erfassung sichtbar → Kennungsfeld wird fokussiert

        sel.Kennung = "SN-1";
        await sel.AssignAsync();
        Assert.True(sel.IsUnique);

        var before = showPanelRaised;
        var start = s.StartAsync();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true }, "erste Abfrage");
        Assert.False(sel.ShowPanel);                // während der Prüfung ausgeblendet (kein Fokusdiebstahl)
        s.ActiveContent!.ChooseCommand.Execute("i. O.");
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Gehäusetemperatur erfassen" }, "Temp");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Prüfling anschließen" }, "Anschluss");
        s.ActiveContent!.Confirm();
        await start.Within();
        await ui.Until(() => !s.IsRunning && sel.CompletedDuts == 1, "ein Prüfling");

        Assert.True(sel.ShowPanel);                 // danach wieder sichtbar → nächster Scan
        Assert.Equal("", sel.Kennung);              // Feld geleert, bereit für den nächsten Scan
        Assert.True(showPanelRaised > before);      // ShowPanel erneut gemeldet → Auto-Fokus greift erneut
    }

    [Fact]
    public async Task Konfigurationsseite_Regeln_speichern_und_simulieren()
    {
        var ui = new QueueDispatcher();
        var project = SampleProjects.AcPruefung();
        var coordinator = new StationCoordinator(StationSetup.Default(project), new ResourceManager()) { IsSuggestion = true };
        var store = new StationSetupStore(Path.Combine(_dir, "pruefplaetze.json"));
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance, stations: coordinator, stationStore: store);
        var page = shell.SelectionPage;
        Assert.Equal(P1, page.SelectedStation);
        Assert.False(page.UseSelection);

        page.UseSelection = true;
        page.AddRuleCommand.Execute(null);
        var rule = page.Rules.Single();
        rule.Pattern = "SN-*";
        rule.Test = "AC-Prüfung";
        Assert.True(rule.IsReleased);
        Assert.True(page.IsDirty);
        page.SampleId = "SN-1";
        await page.SimulateAsync().WaitAsync(Kit.Timeout);
        Assert.True(page.SimulationOk);
        Assert.Equal("AC-Prüfung v1.5", page.SimulationTest);

        page.Save();
        Assert.Null(page.Error);
        ui.Pump();
        Assert.True(store.Exists);
        Assert.Equal("SN-*", store.Load()!.Station(P1)!.Selection!.Rules.Single().Pattern);
        Assert.True(shell.Stations[0].UsesSelection);
        Assert.False(shell.Stations[1].UsesSelection);
    }
}

/// <summary>Abschnitt 4 – konfigurierbare Werkeransicht: Fortschritt oben/links/unten, Übersicht/Protokoll ausblendbar, Messwerte.</summary>
public sealed class OperatorLayoutTests
{
    [Fact]
    public async Task Layout_wird_gespeichert_und_steuert_die_Werkeransicht()
    {
        var project = SampleProjects.AcPruefung();
        var curves = project.Definition.AllNodes().Single(n => n.Id == "netzkurven");
        curves.Storage = curves.Storage.With(Testumgebung.Domain.Storage.StorageTarget.Display, false);
        project.Definition.Layout = new OperatorLayout
        {
            ProgressPosition = ProgressPosition.Bottom, ShowOverview = false, ShowProtocol = false, ShowAllMeasurements = false,
        };

        // Projektformat: Layout bleibt erhalten, Standardlayout wird nicht geschrieben
        var serializer = new ProjectSerializer();
        var json = serializer.Serialize(project);
        Assert.Contains("\"operatorLayout\"", json);
        var back = serializer.Deserialize(json).Definition.Layout;
        Assert.Equal((ProgressPosition.Bottom, false, false, false), (back.ProgressPosition, back.ShowOverview, back.ShowProtocol, back.ShowAllMeasurements));
        Assert.DoesNotContain("operatorLayout", serializer.Serialize(SampleProjects.AcPruefung()));

        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance);
        var s = shell.SelectedStation;
        Assert.True(s.ProgressBottom);
        Assert.False(s.ProgressLeft || s.ProgressTop);
        Assert.Equal((0, 3, 3), (s.ContentColumn, s.ContentSpan, s.MainRowSpan));
        Assert.True(s.CanToggleMeasurements);

        _ = s.StartAsync();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Kennung erfassen" }, "Kennung");
        s.ActiveContent!.Input = "SN-L1";
        s.ActiveContent.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Sichtprüfung" }, "Sicht");
        s.ActiveContent!.ChooseCommand.Execute("i. O.");
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Gehäusetemperatur erfassen" }, "Temp");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Prüfling anschließen" }, "Anschluss");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.Protocol.Any(p => p.Text == "Messung unter Last"), "Messungen");

        // Nur auffällige Messwerte; der Werker blendet alle ein. Ausgeblendeter Schritt: weder Übersicht noch Protokoll.
        Assert.DoesNotContain(s.Measurements, m => m.Rating == ResultRating.Pass);
        Assert.Contains("ausgeblendet", s.HiddenMeasurementsText);
        s.ShowAllMeasurements = true;
        Assert.Contains(s.Measurements, m => m.Name == "Messung Spannung");
        Assert.Equal("", s.HiddenMeasurementsText);
        Assert.DoesNotContain(s.Overview, o => o.Name == "Netzkurven aufnehmen");
        Assert.DoesNotContain(s.Protocol, p => p.Text == "Netzkurven aufnehmen");
        Assert.Contains(s.Overview, o => o.Name == "Messung Spannung");

        s.RequestAbort();
        await ui.Until(() => !s.IsRunning && s.LastStatus is not null, "Ende");

        // Editor: Layout ändern → ruhender Prüfplatz übernimmt es sofort
        var props = new ProjectPropertiesViewModel(shell.Editor) { ShowOverview = true, ProgressPosition = ProgressPosition.Left, ShowProtocol = true };
        Assert.True(props.ShowOverview);
        Assert.True(s.ProgressLeft);
        Assert.Equal((2, 1, 1), (s.ContentColumn, s.ContentSpan, s.MainRowSpan));
        props.ShowOverview = false;
        Assert.True(s.ProgressTop);
    }
}

