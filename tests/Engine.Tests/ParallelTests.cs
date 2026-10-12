using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.ViewModels;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Etappe 9 – Parallelbetrieb: mehrere Prüfplätze, gemeinsame Ressource, Timeout. Abnahme: unabhängige Läufe ohne
/// widersprüchlichen Gerätezugriff (Zugriffsprotokoll, Gerätesicht), Sperre je Gerät statt je Name, Zyklusrisiko statisch
/// verweigert und zur Laufzeit erkannt.
/// </summary>
public sealed class ParallelTests : IDisposable
{
    private const string P1 = "Prüfplatz 01";
    private const string P2 = "Prüfplatz 02";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-par-" + Guid.NewGuid().ToString("N")[..8]);

    public ParallelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Gruppe, die <paramref name="outer"/> reserviert und darin eine Gruppe mit <paramref name="inner"/> (Erweiterung).</summary>
    private static TestDefinition Nested(string name, string outer, string inner, Probe? probe = null, string prefix = "x")
    {
        var innerGroup = Group($"{prefix}.inner", "Innen");
        innerGroup.Resources.Add(inner);
        if (probe is not null) innerGroup.Add(probe.Mark($"{prefix}.inner.mark"));
        var outerGroup = Group($"{prefix}.outer", "Außen");
        outerGroup.Resources.Add(outer);
        if (probe is not null) outerGroup.Add(probe.Gate($"{prefix}.gate"));
        outerGroup.Add(innerGroup);
        return new TestDefinition(name, "1.0", Group($"{prefix}.init"), Group($"{prefix}.loop", "Loop").Add(outerGroup), Group($"{prefix}.fin"));
    }

    private static StationSetup Setup(params (string Station, string Name, string Device, string Channel, bool Lock)[] map)
    {
        var setup = new StationSetup
        {
            Devices =
            [
                new DeviceConfig { Id = StationSetup.LocalSimulation, Name = "Simulation (je Prüfplatz)" },
                new DeviceConfig { Id = StationSetup.SharedSimulation, Name = "Gemeinsame Simulation", Shared = true },
            ],
        };
        foreach (var g in map.GroupBy(m => m.Station))
            setup.Stations.Add(new StationConfig
            {
                Name = g.Key,
                Resources = g.Select(m => new ResourceAssignment { Name = m.Name, Device = m.Device, Channel = m.Channel, RequiresLock = m.Lock }).ToList(),
            });
        return setup;
    }

    // ------------------------------------------------------------------ Zyklusrisiko

    [Fact]
    public void Sperrreihenfolge_Zyklus_ueber_zwei_Ablaeufe_auch_bei_verschiedenen_Namen_fuer_dasselbe_Geraet()
    {
        var a = Nested("A", "AC_Quelle", "DC_Quelle");
        var b = Nested("B", "DC_Quelle", "AC_Quelle");
        var same = Nested("C", "AC_Quelle", "DC_Quelle");
        Assert.Equal("AC_Quelle → DC_Quelle", string.Join("|", LockOrderAnalysis.Edges(a).Select(e => $"{e.HeldName} → {e.AcquiredName}")));
        Assert.Null(LockOrderAnalysis.FindCycle(LockOrderAnalysis.Edges(a).Concat(LockOrderAnalysis.Edges(same))));    // gleiche Reihenfolge
        var cycle = LockOrderAnalysis.FindCycle(LockOrderAnalysis.Edges(a, station: P1).Concat(LockOrderAnalysis.Edges(b, station: P2)));
        Assert.NotNull(cycle);
        Assert.Contains("Zyklusrisiko", cycle!.Text);
        Assert.Contains($"{P1}: AC_Quelle → DC_Quelle", cycle.Text);
        Assert.Contains($"{P2}: DC_Quelle → AC_Quelle", cycle.Text);

        // Atomar in einer Gruppe: keine Erweiterung, kein Risiko
        var atomic = Group("g", "Beide");
        atomic.Resources.AddRange(["DC_Quelle", "AC_Quelle"]);
        var atomicDef = new TestDefinition("D", "1", Group("i"), Group("l").Add(atomic), Group("f"));
        Assert.Empty(LockOrderAnalysis.Edges(atomicDef));

        // Prüfplatz 02 nennt dieselben Geräte anders → der Zyklus entsteht über die Sperrschlüssel
        var b2 = Nested("B2", "Netzteil", "Netz_AC");
        var setup = Setup(
            (P1, "AC_Quelle", StationSetup.SharedSimulation, "ac", true), (P1, "DC_Quelle", StationSetup.SharedSimulation, "dc", true),
            (P2, "Netz_AC", StationSetup.SharedSimulation, "ac", true), (P2, "Netzteil", StationSetup.SharedSimulation, "dc", true));
        Assert.Null(LockOrderAnalysis.FindCycle(LockOrderAnalysis.Edges(a).Concat(LockOrderAnalysis.Edges(b2))));     // nach Namen unsichtbar
        Assert.NotNull(StationHardware.CycleRisk(setup, [(P1, a), (P2, b2)]));

        // Start wird verweigert, solange der andere Prüfplatz läuft
        var coordinator = new StationCoordinator(setup, new ResourceManager());
        Assert.Null(coordinator.TryBeginRun(P1, a));
        var refusal = coordinator.TryBeginRun(P2, b2);
        Assert.Contains("Zyklusrisiko", refusal);
        Assert.Contains("Start verweigert", refusal);
        Assert.Equal([P1], coordinator.RunningStations);
        coordinator.EndRun(P1);
        Assert.Null(coordinator.TryBeginRun(P2, b2));
    }

    [Fact]
    public async Task Laufzeit_Verklemmung_wird_erkannt_ERROR_statt_ewig_warten()
    {
        var rm = new ResourceManager();
        var a1 = await rm.AcquireAsync(new ResourceRequest("Run-1", "s", ["A"], P1), null, default);
        var b2 = await rm.AcquireAsync(new ResourceRequest("Run-2", "s", ["B"], P2), null, default);
        var waiting = rm.AcquireAsync(new ResourceRequest("Run-1", "s2", ["B"], P1), null, default);
        Assert.False(waiting.IsCompleted);
        var ex = await Assert.ThrowsAsync<ResourceDeadlockException>(() => rm.AcquireAsync(new ResourceRequest("Run-2", "s2", ["A"], P2), null, default));
        Assert.Contains($"{P2} → {P1} → {P2}", ex.Message);
        Assert.Equal([P1], rm.Snapshot().Single(o => o.Resource == "B").WaitingNames);        // abgelehnte Anforderung nicht eingereiht
        b2.Dispose();
        (await waiting.Within()).Dispose();
        a1.Dispose();

        // In der Engine: beide Läufe enden (ERROR für den, der die Verklemmung auslösen würde) – kein Hängen
        var probe = new Probe();
        var resources = new ResourceManager();
        var sim = new SimulationAdapter();
        var hw = new HardwareRegistry().Bind("AC", sim, "ac", true).Bind("DC", sim, "dc", true);
        var r1 = new TestRun(Nested("A", "AC", "DC", probe, "a"), new TestRunOptions { Resources = resources, Hardware = hw, StationName = P1 }, "Run-1");
        var r2 = new TestRun(Nested("B", "DC", "AC", probe, "b"), new TestRunOptions { Resources = resources, Hardware = hw, StationName = P2 }, "Run-2");
        var t1 = r1.RunAsync();
        var t2 = r2.RunAsync();
        await probe.WhenReached("a.gate");
        await probe.WhenReached("b.gate");
        probe.Release("a.gate");                                                  // Run-1 wartet auf DC
        await r1.WaitFor<ResourceWaitStarted>(e => e.NodeId == "a.inner");
        await Task.Delay(50);
        probe.Release("b.gate");                                                  // Run-2 würde auf AC warten → Zyklus
        var s2 = await t2.Within();
        var s1 = await t1.Within();
        Assert.Equal(ResultRating.Error, s2.Rating);
        Assert.Contains(r2.Events.OfType<ResourceNotAcquired>(), e => e.Reason.Contains("Gegenseitiges Warten"));
        Assert.Contains("a.inner.mark", probe.Marks);                             // Run-1 bekam DC nach Freigabe durch Run-2
        Assert.DoesNotContain("b.inner.mark", probe.Marks);
        Assert.NotEqual(ResultRating.Error, s1.Rating);
    }

    // ------------------------------------------------------------------ Konfiguration

    [Fact]
    public void Konfiguration_Vorschlag_Pruefung_Speichern_und_beschaedigte_Datei()
    {
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project);
        Assert.Equal([P1, P2], setup.Stations.Select(s => s.Name));
        var ac = setup.Station(P1)!.Resources.Single(a => a.Name == "AC_Quelle");
        Assert.Equal((StationSetup.SharedSimulation, true), (ac.Device, ac.RequiresLock));
        Assert.Equal(StationSetup.LocalSimulation, setup.Station(P1)!.Resources.Single(a => a.Name == "Spannung_Pruefling").Device);
        Assert.Equal("sim-gemeinsam:L1/L2/L3", setup.KeyOf(P1)("AC_Quelle"));
        Assert.Equal(setup.KeyOf(P1)("AC_Quelle"), setup.KeyOf(P2)("AC_Quelle"));
        Assert.NotEqual(setup.KeyOf(P1)("Spannung_Pruefling"), setup.KeyOf(P2)("Spannung_Pruefling"));

        var issues = StationHardware.Check(setup, project);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.Contains(issues, i => i.Severity == ConfigSeverity.Info && i.Text.Contains($"AC_Quelle ({P1}, {P2})"));

        // Fehler: gemeinsames Gerät ohne Sperre, fehlende Zuordnung, unbekanntes Gerät
        var broken = setup.Clone();
        broken.Station(P2)!.Resources.Single(a => a.Name == "AC_Quelle").RequiresLock = false;
        broken.Station(P2)!.Resources.RemoveAll(a => a.Name == "Strom_Pruefling");
        broken.Station(P1)!.Resources.Single(a => a.Name == "Digital_Start").Device = "fehlt";
        var errors = StationHardware.Check(broken, project).Where(i => i.IsError).Select(i => i.Text).ToList();
        Assert.Contains(errors, e => e.Contains("ohne Sperre – widersprüchlicher Zugriff"));
        Assert.Contains(errors, e => e.Contains("Strom_Pruefling wird im Ablauf verwendet, ist aber nicht zugeordnet"));
        Assert.Contains(errors, e => e.Contains("Gerät 'fehlt' ist nicht konfiguriert"));

        // Datei: Rundreise, beschädigt → Ausnahme (kein stiller Rückfall)
        var store = new StationSetupStore(Path.Combine(_dir, "pruefplaetze.json"));
        Assert.Null(store.Load());
        store.Save(setup);
        var loaded = store.Load()!;
        Assert.Equal(setup.Stations.SelectMany(s => s.Resources).Select(a => $"{a.Name}|{a.Device}|{a.Channel}|{a.RequiresLock}"),
            loaded.Stations.SelectMany(s => s.Resources).Select(a => $"{a.Name}|{a.Device}|{a.Channel}|{a.RequiresLock}"));
        File.WriteAllText(store.Path, "{ kaputt");
        Assert.Throws<StationSetupException>(() => store.Load());
        var coordinator = new StationCoordinator(setup, new ResourceManager()) { LoadError = "beschädigt" };
        Assert.Equal("beschädigt", coordinator.TryBeginRun(P1, project.Definition));
    }

    // ------------------------------------------------------------------ Abnahme Etappe 9

    /// <summary>Ablauf: je Prüfling AC-Quelle reservieren, einschalten, lokal messen, Kurve, ausschalten; außerhalb nur lokale Hardware.</summary>
    private static TestProject ParallelProject(string acName, int duts, TimeSpan hold, TimeSpan? timeout = null)
    {
        var mess = new GroupDefinition("mess", "Messgruppe") { ResourceTimeout = timeout };
        mess.Resources.Add(acName);
        mess.Add(
            Steps.Output("mess.ein", "AC ein", acName, 230),
            Steps.Wait("mess.halten", hold),
            Steps.Measure("mess.u", "Spannung", "Spannung_Pruefling", null, 220, 240, "V"),
            Steps.Output("mess.aus", "AC aus", acName, 0));
        mess.ExitGroup = Group("mess.exit", "Exit").Add(Steps.Output("mess.sicher", "AC sicher aus", acName, 0));
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }
            .Var("Kennung", VarType.String, "SN-P")
            .Add(Steps.Measure("vor", "Vorprüfung", "Spannung_Pruefling", null, 220, 240, "V"), mess);
        var loop = new GroupDefinition("loop", "Loop") { Repetition = Repetition.Times(duts) }.Add(dut);
        var def = new TestDefinition("Parallel", "1.0", Group("init"), loop, Group("fin"));
        var project = new TestProject(def);
        project.Simulation.Add(new SimulatedResource("Spannung_Pruefling", "ai0", "V", 230, Noise: 0.5));
        project.Simulation.Add(new SimulatedResource(acName, "L1/L2/L3", "V", 0, RequiresLock: true, Min: 0, Max: 300));
        return project;
    }

    private static StationSetup TwoStations(string ac1, string ac2, bool shared = true, bool locks = true)
        => Setup(
            (P1, "Spannung_Pruefling", StationSetup.LocalSimulation, "ai0", false),
            (P1, ac1, shared ? StationSetup.SharedSimulation : StationSetup.LocalSimulation, "L1/L2/L3", locks),
            (P2, "Spannung_Pruefling", StationSetup.LocalSimulation, "ai0", false),
            (P2, ac2, shared ? StationSetup.SharedSimulation : StationSetup.LocalSimulation, "L1/L2/L3", locks));

    private static string Errors(TestRun run)
        => string.Join(" | ", run.Events.OfType<StepCompleted>().Where(e => e.Rating is ResultRating.Error or ResultRating.Fail).Select(e => $"{e.NodeId}: {e.Message}").Distinct());

    private static TestRun Run(StationCoordinator c, string station, TestProject project, string runId)
    {
        Assert.Null(c.TryBeginRun(station, project.Definition));
        return new TestRun(project.Definition, new TestRunOptions
        {
            Resources = c.Resources,
            Hardware = c.BuildRegistry(station, project, seed: station.GetHashCode()),
            AccessJournal = c.Journal,
            StationName = station,
        }, runId);
    }

    [Fact]
    public async Task Etappe9_Zwei_Pruefplaetze_unabhaengig_ohne_widerspruechlichen_Geraetezugriff()
    {
        // Prüfplatz 02 nennt die gemeinsame AC-Quelle anders – gesperrt wird trotzdem das Gerät.
        var p1 = ParallelProject("AC_Quelle", duts: 6, hold: TimeSpan.FromMilliseconds(15));
        var p2 = ParallelProject("Netz_AC", duts: 6, hold: TimeSpan.FromMilliseconds(15));
        var c = new StationCoordinator(TwoStations("AC_Quelle", "Netz_AC"), new ResourceManager());
        var r1 = Run(c, P1, p1, "Run-01");
        var r2 = Run(c, P2, p2, "Run-02");
        var waits = 0;
        r2.EventRaised += e => { if (e is ResourceWaitStarted) Interlocked.Increment(ref waits); };
        r1.EventRaised += e => { if (e is ResourceWaitStarted) Interlocked.Increment(ref waits); };

        var results = await Task.WhenAll(r1.RunAsync(), r2.RunAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        c.EndRun(P1);
        c.EndRun(P2);

        // Unabhängige Läufe: beide vollständig, jeweils 6 Prüflinge, Ereignisse nur des eigenen Laufs
        Assert.All(results, s => Assert.Equal(ResultRating.Pass, s.Rating));
        Assert.Equal(6, r1.Events.OfType<DutRunStarted>().Count());
        Assert.Equal(6, r2.Events.OfType<DutRunStarted>().Count());
        Assert.All(r1.Events, e => Assert.Equal("Run-01", e.RunId));
        Assert.All(r2.Events, e => Assert.Equal("Run-02", e.RunId));

        // Kein widersprüchlicher Gerätezugriff: Protokoll ohne Konflikt, Zugriffe auf das Gerät zeitlich getrennt
        Assert.Empty(c.Journal.Conflicts);
        var shared = c.Journal.Recent().Where(a => a.Key == "sim-gemeinsam:L1/L2/L3").OrderBy(a => a.Sequence).ToList();
        Assert.Equal(2 * 6 * 3, shared.Count);                                    // je Prüfling: ein, aus, Exit „sicher aus“
        Assert.Contains(shared, a => a.Resource == "Netz_AC");
        for (var i = 1; i < shared.Count; i++)
            if (shared[i].RunId != shared[i - 1].RunId)
                Assert.True(shared[i - 1].End <= shared[i].Start, "Zugriffe zweier Prüfplätze überlappen");

        // Gerätesicht: das gemeinsame Gerät sieht immer ein → aus derselben Station, nie zweimal „ein“
        var device = c.Pool.Instances[StationSetup.SharedSimulation];
        var writes = device.WriteLog.Select(w => w.Value).ToList();
        Assert.Equal(36, writes.Count);
        for (var i = 0; i < writes.Count; i += 3) Assert.Equal((230.0, 0.0, 0.0), (writes[i], writes[i + 1], writes[i + 2]));
        Assert.True(waits > 0, "Die Prüfplätze haben sich die AC-Quelle nie streitig gemacht – Test ohne Aussage");

        // Lokale Geräte sind je Prüfplatz eigene Instanzen
        Assert.Equal(3, c.Pool.Instances.Count);
    }

    [Fact]
    public async Task Ohne_Sperre_erkennt_das_Zugriffsprotokoll_widerspruechliche_Zugriffe()
    {
        // Gegenprobe: gemeinsames Gerät ohne Sperre (Konfigurationsprüfung meldet Fehler) → gleichzeitige Zugriffe werden erkannt
        var p1 = ParallelProject("AC_Quelle", duts: 1, hold: TimeSpan.Zero);
        var p2 = ParallelProject("AC_Quelle", duts: 1, hold: TimeSpan.Zero);
        foreach (var p in new[] { p1, p2 }) p.Definition.Find("mess")!.As<GroupDefinition>().Resources.Clear();
        var setup = TwoStations("AC_Quelle", "AC_Quelle", locks: false);
        Assert.Contains(StationHardware.Check(setup, p1), i => i.IsError && i.Text.Contains("ohne Sperre"));
        var c = new StationCoordinator(setup, new ResourceManager());
        var slow = new SlowAdapter();
        var registry1 = new HardwareRegistry().Bind("AC_Quelle", slow, "L1/L2/L3", false, "sim-gemeinsam:L1/L2/L3").Bind("Spannung_Pruefling", slow, "ai0", false, "P1/ai0");
        var registry2 = new HardwareRegistry().Bind("AC_Quelle", slow, "L1/L2/L3", false, "sim-gemeinsam:L1/L2/L3").Bind("Spannung_Pruefling", slow, "ai0", false, "P2/ai0");
        var r1 = new TestRun(p1.Definition, new TestRunOptions { Resources = c.Resources, Hardware = registry1, AccessJournal = c.Journal, StationName = P1 }, "Run-01");
        var r2 = new TestRun(p2.Definition, new TestRunOptions { Resources = c.Resources, Hardware = registry2, AccessJournal = c.Journal, StationName = P2 }, "Run-02");
        await Task.WhenAll(r1.RunAsync(), r2.RunAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(c.Journal.Conflicts, a => a.Conflict!.StartsWith("gleichzeitig mit"));
    }

    /// <summary>Gerät, dessen Schreibzugriff dauert – damit sich ungesperrte Zugriffe überschneiden.</summary>
    private sealed class SlowAdapter : IHardwareAdapter
    {
        public string Name => "Langsam";
        public ChannelCapabilities GetCapabilities(string channel) => new("V", 0, 300);
        public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => Task.FromResult(230.0);
        public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => Task.Delay(80, cancellationToken);
    }

    [Fact]
    public async Task Timeout_am_einen_Pruefplatz_ERROR_der_andere_laeuft_unbeeinflusst_weiter()
    {
        var p1 = ParallelProject("AC_Quelle", duts: 1, hold: TimeSpan.FromMilliseconds(600));
        var p2 = ParallelProject("AC_Quelle", duts: 1, hold: TimeSpan.Zero, timeout: TimeSpan.FromMilliseconds(100));
        var c = new StationCoordinator(TwoStations("AC_Quelle", "AC_Quelle"), new ResourceManager());
        var r1 = Run(c, P1, p1, "Run-01");
        var r2 = Run(c, P2, p2, "Run-02");
        var t1 = r1.RunAsync();
        await r1.WaitFor<StepStarted>(e => e.NodeId == "mess.halten");
        var s2 = await r2.RunAsync().Within();
        Assert.Equal(ResultRating.Error, s2.Rating);
        var reason = r2.Events.OfType<ResourceNotAcquired>().Single().Reason;
        Assert.Contains("Zeitlimit", reason);
        Assert.Contains($"belegt von {P1}", reason);
        Assert.Empty(r2.Steps("mess.ein"));                                       // reguläre Schritte übersprungen
        Assert.Equal(ResultRating.Error, r2.Steps("mess.sicher").Single().Rating); // Exit ohne Reservierung: kein Zugriff
        Assert.False(t1.IsCompleted);
        Assert.Equal(ResultRating.Pass, (await t1.Within()).Rating);
        Assert.Empty(c.Journal.Conflicts);
        Assert.Equal([230.0, 0.0, 0.0], c.Pool.Instances[StationSetup.SharedSimulation].WriteLog.Select(w => w.Value));
    }

    // ------------------------------------------------------------------ Oberfläche

    [Fact]
    public async Task Konfigurationsseite_Pruefplaetze_anlegen_speichern_Live_Status_und_Werkerabbruch()
    {
        var ui = new QueueDispatcher();
        var store = new StationSetupStore(Path.Combine(_dir, "pruefplaetze.json"));
        var project = SampleProjects.AcPruefung();
        var resources = new ResourceManager();
        var coordinator = new StationCoordinator(StationSetup.Default(project), resources) { IsSuggestion = true };
        var shell = new ShellViewModel(ui, project: project, clock: SystemClock.Instance, stations: coordinator, stationStore: store);
        var page = shell.StationsPage;
        Assert.Equal([P1, P2], shell.Stations.Select(s => s.Name));
        Assert.Contains("Vorschlag", page.SourceText);
        Assert.False(page.HasConfigErrors);
        Assert.Equal("Messgruppe – Belastung", page.SelectedGroup!.Name);
        Assert.Equal("AC_Quelle", page.SelectedRow!.Name);
        Assert.Contains(page.Details, d => d.Label == "Sperrschlüssel" && d.Value == "sim-gemeinsam:L1/L2/L3");
        Assert.Contains(page.Details, d => d.Label == "Gemeinsam mit" && d.Value == $"{P1}, {P2}");

        // Dritten Prüfplatz anlegen und speichern → Shell zeigt ihn
        page.AddStationCommand.Execute(null);
        Assert.Equal("Prüfplatz 03", page.SelectedStation);
        Assert.True(page.IsDirty);
        page.Save();
        Assert.Null(page.Error);
        ui.Pump();
        Assert.True(store.Exists);
        Assert.False(coordinator.IsSuggestion);
        Assert.Equal([P1, P2, "Prüfplatz 03"], shell.Stations.Select(s => s.Name));

        // Fehlerhafte Zuordnung wird nicht gespeichert
        page.SelectedStation = P2;
        page.SelectedRow = page.Rows.Single(r => r.Name == "AC_Quelle");
        page.SelectedRow.RequiresLock = false;
        Assert.True(page.HasConfigErrors);
        page.Save();
        Assert.Contains("ohne Sperre", page.Error);
        page.DiscardCommand.Execute(null);
        Assert.False(page.IsDirty);

        // Live: Prüfplatz 02 hält die AC-Quelle → Status in Zeile, Reservierung und Wartestatus
        var key = coordinator.Setup.KeyOf(P2)("AC_Quelle");
        var held = await resources.AcquireAsync(new ResourceRequest("Run-02", "s", [key], P2, "Messgruppe – Belastung"), null, default);
        var queued = resources.AcquireAsync(new ResourceRequest("Run-01", "s", [key], P1, "Messgruppe – Belastung"), null, default);
        ui.Pump();
        page.SelectedStation = P1;
        page.RefreshLive();
        var row = page.Rows.Single(r => r.Name == "AC_Quelle");
        Assert.StartsWith($"Reserviert – {P2}", row.Status);
        Assert.Contains($"wartend: {P1}", row.Status);
        Assert.Equal(StationStatusKind.Waiting, row.StatusKind);
        Assert.Equal("Nein", page.AcquiredText);
        Assert.Contains(page.WaitStatus, w => w.Station == P1 && w.State == "In Warteschlange");
        Assert.Contains(page.WaitStatus, w => w.Station == P2 && w.State == "Eigentümer");
        Assert.StartsWith("Run-02 / Messgruppe – Belastung", page.ScopeText);
        held.Dispose();
        (await queued.Within()).Dispose();

        // Speichern während einer Prüfung wird abgelehnt; Werkerabbruch eines Prüfplatzes lässt den anderen weiterlaufen
        var (a, b) = (shell.Stations[0], shell.Stations[1]);
        _ = a.StartAsync();
        _ = b.StartAsync();
        await ui.Until(() => a.IsRunning && b.IsRunning, "beide laufen");
        page.AddStationCommand.Execute(null);
        page.Save();
        Assert.Contains("Während einer Prüfung", page.Error);
        page.DiscardCommand.Execute(null);
        a.RequestAbort();
        await ui.Until(() => !a.IsRunning && a.LastStatus is not null, "A beendet");
        Assert.True(b.IsRunning);
        Assert.Equal([P2], coordinator.RunningStations);
        b.RequestAbort();
        await ui.Until(() => !b.IsRunning && b.LastStatus is not null, "B beendet");
        Assert.Empty(coordinator.RunningStations);
        Assert.Empty(coordinator.Journal.Conflicts);
    }
}

internal static class NodeExtensions
{
    public static T As<T>(this NodeDefinition node) where T : NodeDefinition => (T)node;
}
