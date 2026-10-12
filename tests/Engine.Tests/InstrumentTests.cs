using System.Diagnostics;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using static Testumgebung.Engine.Tests.Kit;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Etappe 10: Treiber für Keysight/Agilent (SCPI) und NI-DAQmx gegen Gerätesimulatoren – reale Fähigkeiten,
/// Fehlerwarteschlange, Abbruchverhalten, sicherer Zustand, Synchronisation.
/// </summary>
public sealed class InstrumentTests
{
    private static ScpiSession Session(ScpiSimulatorServer server) => new(server.Resource, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Ressourcen_werden_auf_Socket_oder_VISA_abgebildet()
    {
        Assert.IsType<TcpSocketIo>(InstrumentIo.Open("TCPIP::192.168.1.20::5025::SOCKET"));
        Assert.IsType<TcpSocketIo>(InstrumentIo.Open("TCPIP0::k-34465a-01::5025::SOCKET"));
        Assert.IsType<TcpSocketIo>(InstrumentIo.Open("10.0.0.5:5025"));
        Assert.IsType<VisaIo>(InstrumentIo.Open("USB0::0x2A8D::0x1301::MY57200001::INSTR"));
        Assert.IsType<VisaIo>(InstrumentIo.Open("GPIB0::22::INSTR"));
        Assert.IsType<VisaIo>(InstrumentIo.Open("TCPIP0::192.168.1.20::hislip0::INSTR"));
        Assert.True(ScpiInstrument.SameModel("DSOX3034T", "DSO-X 3034T"));
        Assert.True(ScpiInstrument.SameModel("34461A", "34461A"));
        Assert.False(ScpiInstrument.SameModel("34461A", "E36312A"));
        // Ohne VISA-Installation: verständlicher Fehler statt Absturz
        if (!VisaIo.IsAvailable)
        {
            using var io = new VisaIo("GPIB0::22::INSTR");
            var ex = await Assert.ThrowsAsync<InstrumentException>(() => io.WriteAsync("*IDN?\n"u8.ToArray(), TimeSpan.FromSeconds(1), default));
            Assert.Contains("VISA", ex.Message);
        }
    }

    [Fact]
    public async Task Multimeter_Netzgeraet_AC_Quelle_messen_und_stellen_ueber_SCPI()
    {
        await using var lab = new SimulatedLab();
        using var dmm = new Keysight3446xDmm("34465A", Session(lab.Dmm), new InstrumentOptions(new Dictionary<string, string> { ["NPLC"] = "1" }));
        using var supply = new KeysightE36300Supply("E36312A", Session(lab.Supply));
        using var ac = new KeysightAc6800Source("AC6802B", Session(lab.AcSource), new InstrumentOptions(new Dictionary<string, string> { ["Bereich"] = "310" }));

        var probe = await dmm.ProbeAsync(default);
        Assert.True(probe.Ok, string.Join(" ", probe.Details));
        Assert.Contains("34465A", probe.Identity);
        Assert.Contains(dmm.Channels, c => c.Name == "FRES");

        // Netzgerät: 5 V an CH1 einschalten, DMM misst Gleichspannung und Strom (Last 12 Ω)
        await supply.WriteAsync("CH1:I", 1, default);
        await supply.WriteAsync("CH1", 5, default);
        await supply.WriteAsync("CH1:OUT", 1, default);
        Assert.Equal(5, await supply.ReadAsync("CH1", default), 0.01);
        Assert.Equal(5 / 12.0, await supply.ReadAsync("CH1:I", default), 0.01);
        Assert.Equal(5, await dmm.ReadAsync("VDC", default), 0.001);
        Assert.Equal(5 / 12.0, await dmm.ReadAsync("IDC", default), 0.001);
        Assert.Contains("→ CONF:VOLT:DC AUTO", string.Join("|", dmm.Log));
        Assert.Contains("→ VOLT:DC:NPLC 1", string.Join("|", dmm.Log));
        Assert.Contains("VOLT 5,(@1)", lab.Supply.Device.Received);

        // AC-Quelle 230 V / 50 Hz: DMM misst Effektivwert und Frequenz, Quelle misst Strom und Leistung
        await ac.WriteAsync("VOLT", 230, default);
        await ac.WriteAsync("FREQ", 50, default);
        await ac.WriteAsync("OUT", 1, default);
        Assert.Equal(230, await dmm.ReadAsync("VAC", default), 0.01);
        Assert.Equal(50, await dmm.ReadAsync("FREQ", default), 0.01);
        Assert.Equal(230 / SimulatedBench.AcLoadOhm, await ac.ReadAsync("CURR", default), 0.01);
        Assert.Equal(230 * 230 / SimulatedBench.AcLoadOhm, await ac.ReadAsync("POW", default), 1.0);
        Assert.Equal("VOLT:RANG 310", lab.AcSource.Device.Received.First(r => r.StartsWith("VOLT:RANG", StringComparison.Ordinal)));

        // Bereichsprüfung im Treiber (keine automatische Begrenzung) und Fehlerwarteschlange des Geräts
        var range = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => supply.WriteAsync("CH2", 30, default));
        Assert.Contains("keine automatische Begrenzung", range.Message);
        lab.AcSource.Device.Received.Clear();
        var rejected = await Assert.ThrowsAsync<InstrumentException>(() => ac.Session.WriteAsync("VOLT:RANG 155", default));
        Assert.Contains("-221", rejected.Message);                                  // Bereichswechsel bei eingeschaltetem Ausgang
        await Assert.ThrowsAsync<InstrumentException>(() => dmm.WriteAsync("VDC", 1, default));

        // Sicherer Zustand: alle Ausgänge aus
        await supply.SafeStateAsync(default);
        await ac.SafeStateAsync(default);
        Assert.Contains("OUTP OFF,(@1:3)", lab.Supply.Device.Received);
        Assert.Equal(0, await dmm.ReadAsync("VAC", default), 0.01);
        Assert.Equal(0, await supply.ReadAsync("CH1:OUT", default));
    }

    [Fact]
    public async Task Generator_Kurve_laden_uebertragen_starten_Oszilloskop_nimmt_synchron_auf()
    {
        await using var lab = new SimulatedLab();
        using var gen = new Keysight33500Generator("33522B", Session(lab.Generator));
        using var scope = new KeysightInfiniiVisionScope("DSOX3034T", Session(lab.Scope));
        var caps = gen.GetOutputCapabilities("ARB");
        Assert.Equal(["L1", "L2"], caps.Channels);
        Assert.True(caps.SupportsRate(123456.7));                                // freie Abtastrate (1 µSa/s … 250 MSa/s)
        Assert.False(caps.SupportsRate(500e6));

        // Zweiphasige Kurve 1 kHz, ±2 V, 90° versetzt, 100 kSa/s
        const double rate = 100_000;
        var n = 100;
        var l1 = Enumerable.Range(0, n).Select(i => 2 * Math.Sin(2 * Math.PI * i / n)).ToArray();
        var l2 = Enumerable.Range(0, n).Select(i => 2 * Math.Cos(2 * Math.PI * i / n)).ToArray();
        var curve = new OutputCurve("Test@1.0", rate, null, [new OutputChannelData("L1", "V", l1), new OutputChannelData("L2", "V", l2)]);
        await Assert.ThrowsAsync<OutputSequenceException>(() => gen.StartAsync("ARB", default));   // Reihenfolge erzwungen
        await gen.LoadAsync("ARB", curve, default);
        await gen.TransferAsync("ARB", default);
        Assert.Equal(OutputState.Transferred, gen.GetOutputState("ARB"));
        var sent = lab.Generator.Device.Received;
        Assert.Contains("SOUR1:FUNC:ARB:SRAT 100000", sent);
        Assert.Contains("SOUR2:FUNC ARB", sent);
        Assert.Contains("FUNC:ARB:SYNC", sent);
        Assert.Contains(sent, s => s.StartsWith("SOUR1:VOLT 4", StringComparison.Ordinal));        // 4 Vss aus der Kurve, keine Skalierung der Werte
        Assert.Equal(n, lab.Bench.Generator[0].ArbData["TU1"].Length);
        await gen.StartAsync("ARB", default);

        // Zu große Kurve: Geräteeignung verweigert (z. B. ±8 V an 50 Ω)
        var tooLarge = curve with { Channels = [new OutputChannelData("L1", "V", l1.Select(v => v * 4).ToArray())] };
        var ex = await Assert.ThrowsAsync<OutputSequenceException>(() => gen.LoadAsync("ARB", tooLarge, default));
        Assert.Contains("Ausgabe läuft", ex.Message);

        // Oszilloskop: CHAN2/CHAN3 in einer Erfassung → gemeinsame Zeitbasis
        var set = await scope.CaptureAsync(["CHAN2", "CHAN3"], 1_000_000, 2000, default);
        Assert.Equal(2, set.Channels.Count);
        Assert.Equal(set.Channels[0].SyncGroup, set.Channels[1].SyncGroup);
        Assert.Equal(2000, set.Channels[0].Count);
        Assert.Equal(1_000_000, set.Channels[0].SampleRate, 1.0);
        Assert.InRange(set.Channels[0].Samples.Max(), 1.9, 2.05);
        Assert.InRange(set.Channels[1].Samples.Min(), -2.05, -1.9);
        var phase = Correlate(set.Channels[0].Samples, set.Channels[1].Samples);
        Assert.InRange(phase, 0.9, 1.0);                                          // L2 eilt L1 um eine Viertelperiode voraus
        Assert.Equal(2 * Math.Sqrt(0.5), await scope.ReadAsync("CHAN2:VRMS", default), 0.05);

        // Stoppen / sicherer Zustand
        await gen.StopAsync("ARB", default);
        Assert.False(lab.Bench.Generator[0].On);
        await gen.StartAsync("ARB", default);
        await gen.SafeStateAsync(default);
        Assert.False(lab.Bench.Generator[1].On);
        Assert.Equal(OutputState.Stopped, gen.GetOutputState("ARB"));
    }

    /// <summary>Normierte Korrelation von x mit y um eine Viertelperiode (100 Samples bei 1 MSa/s / 1 kHz → 250) verschoben.</summary>
    private static double Correlate(double[] x, double[] y)
    {
        const int shift = 250;
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i + shift < x.Length; i++)
        {
            sxy += x[i + shift] * y[i];
            sxx += x[i + shift] * x[i + shift];
            syy += y[i] * y[i];
        }
        return sxy / Math.Sqrt(sxx * syy);
    }

    [Fact]
    public async Task Abbruch_waehrend_einer_Messung_wirkt_sofort_und_die_Sitzung_bleibt_benutzbar()
    {
        await using var lab = new SimulatedLab();
        using var dmm = new Keysight3446xDmm("34461A", new ScpiSession(lab.Dmm.Resource, TimeSpan.FromSeconds(30)));
        Assert.Equal(0, await dmm.ReadAsync("VDC", default), 0.01);
        lab.Dmm.Device.ResponseDelay = TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dmm.ReadAsync("VDC", cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Abbruch dauerte {watch.Elapsed}");
        lab.Dmm.Device.ResponseDelay = TimeSpan.Zero;
        // Verspätete Antwort darf nicht dem nächsten Befehl zugeordnet werden: neue Verbindung nach Abbruch
        Assert.Equal(0, await dmm.ReadAsync("VDC", default), 0.01);
        Assert.Contains(dmm.Log, l => l.Contains("Device Clear", StringComparison.Ordinal));

        // Zeitlimit: TimeoutException mit Ressource, danach wieder benutzbar
        dmm.Session.Timeout = TimeSpan.FromMilliseconds(300);
        lab.Dmm.Device.ResponseDelay = TimeSpan.FromSeconds(2);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => dmm.Session.QueryAsync("*IDN?"));
        Assert.Contains("Zeitlimit", timeout.Message);
        lab.Dmm.Device.ResponseDelay = TimeSpan.Zero;
        dmm.Session.Timeout = TimeSpan.FromSeconds(2);
        Assert.Contains("34465A", await dmm.Session.QueryAsync("*IDN?"));

        // Nicht erreichbares Gerät: klare Meldung im Test
        using var missing = new Keysight3446xDmm("34461A", new ScpiSession("TCPIP::127.0.0.1::1::SOCKET", TimeSpan.FromSeconds(1)));
        var probe = await missing.ProbeAsync(default);
        Assert.False(probe.Ok);
        // Je nach Betriebssystem „… fehlgeschlagen“ (sofortige Ablehnung) oder „Keine Verbindung … innerhalb“ (Zeitüberschreitung) – beide nennen die Adresse.
        Assert.Contains("127.0.0.1:1", string.Join(" ", probe.Details));
    }

    [Fact]
    public async Task DAQmx_Einzelwerte_Aufnahme_mit_gemeinsamem_Takt_Kurvenausgabe_Abbruch_und_sicherer_Zustand()
    {
        var api = new SimulatedDaqmxApi();
        using var dev = new DaqmxDevice("Dev1,cDAQ1Mod1", api,
            new InstrumentOptions(new Dictionary<string, string> { ["Anschluss"] = "DIFF", ["Sichere Ausgänge"] = "Dev1/port1/line0=0" }));
        var probe = await dev.ProbeAsync(default);
        Assert.True(probe.Ok, string.Join(" ", probe.Details));
        Assert.Contains("USB-6211 (simuliert)", string.Join(" ", probe.Details));
        Assert.Contains("NI 9225", dev.Model);
        Assert.Contains(dev.Channels, c => c.Name == "cDAQ1Mod1/ai2" && c.Max == 425);

        // Einzelwerte: AO setzen, über Verdrahtung auf ai0 messen; DO → DI
        await dev.WriteAsync("Dev1/ao0", 2.5, default);
        Assert.Equal(2.5, await dev.ReadAsync("Dev1/ai0", default), 1e-9);
        await dev.WriteAsync("Dev1/port1/line0", 1, default);
        Assert.Equal(1, await dev.ReadAsync("Dev1/port0/line0", default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dev.WriteAsync("Dev1/ao1", 12, default));
        await Assert.ThrowsAsync<InstrumentException>(() => dev.ReadAsync("Dev1/ao0", default));
        Assert.Equal(0, api.OpenTasks);                                           // jeder Einzelzugriff gibt seinen Task frei

        // Netz 3-phasig über NI 9225: ein Takt, simultan → gemeinsame Zeitbasis, 120° Versatz
        var set = await dev.CaptureAsync(["cDAQ1Mod1/ai0", "cDAQ1Mod1/ai1", "cDAQ1Mod1/ai2"], 10_000, 400, default);
        Assert.All(set.Channels, w => Assert.Equal(set.Channels[0].SyncGroup, w.SyncGroup));
        Assert.All(set.Channels, w => Assert.Equal(230, w.Rms(), 1.0));
        Assert.Contains("simultan", set.Channels[0].Quality);
        for (var i = 0; i < 400; i += 17)
            Assert.Equal(set.Channels[0].Samples[i], set.Channels[1].Samples[(i + 67) % 400], 25.0);   // 1/3 Periode = 66,7 Samples
        Assert.Contains("CfgSampClkTiming 10000 endlich 400", api.Calls);
        Assert.Equal(0, api.OpenTasks);
        // Gemultiplextes Gerät: Gesamtrate wird geprüft
        var tooFast = await Assert.ThrowsAsync<InstrumentException>(() => dev.CaptureAsync(["Dev1/ai2", "Dev1/ai3"], 200_000, 100, default));
        Assert.Contains("übersteigt", tooFast.Message);

        // Kurvenausgabe auf AO (Regeneration), gemessen über ai0 – gleiche Reihenfolge wie beim Generator
        var caps = dev.GetOutputCapabilities("AO");
        Assert.Equal(["L1", "L2"], caps.Channels);
        var ramp = Enumerable.Range(0, 50).Select(i => i / 10.0 - 2.5).ToArray();
        var curve = new OutputCurve("Rampe@1", 5000, null, [new OutputChannelData("L1", "V", ramp)]);
        await dev.LoadAsync("AO", curve, default);
        await dev.TransferAsync("AO", default);
        await dev.StartAsync("AO", default);
        var seen = await dev.CaptureAsync(["Dev1/ai0"], 5000, 100, default);
        Assert.InRange(seen.Channels[0].Samples.Max() - seen.Channels[0].Samples.Min(), 4.8, 5.0);
        Assert.Equal(1, api.OpenTasks);                                           // nur der laufende AO-Task

        // Abbruch einer laufenden Aufnahme mit Starttrigger, der nie kommt: schnell, Task freigegeben
        using var triggered = new DaqmxDevice("Dev1", api, new InstrumentOptions(new Dictionary<string, string> { ["Starttrigger"] = "/Dev1/PFI0" }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => triggered.CaptureAsync(["Dev1/ai3"], 1000, 100, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(1, api.OpenTasks);

        // Getriggert: Start erst mit Trigger (Synchronisation mit anderem Gerät)
        var pending = triggered.CaptureAsync(["Dev1/ai3"], 1000, 50, default);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);
        api.FireTrigger("/Dev1/PFI0");
        Assert.Equal(50, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Channels[0].Count);

        // Sicherer Zustand: Ausgabe beendet, AO 0 V, DO sicher
        await dev.SafeStateAsync(default);
        Assert.Equal(OutputState.Stopped, dev.GetOutputState("AO"));
        Assert.Equal(0, api.AoValue(0));
        Assert.Equal(0, api.DoValue(0));
        Assert.Equal(0, api.OpenTasks);
        Assert.Equal(0, await dev.ReadAsync("Dev1/ai0", default), 1e-9);
    }

    // ------------------------------------------------------------------ Ablauf mit realen Treibern (Prüfplatzkonfiguration)

    private static StationSetup LabSetup(SimulatedLab lab) => new()
    {
        Devices =
        [
            new DeviceConfig { Id = "dmm", Name = "DMM", Adapter = Keysight3446xDmm.AdapterName, Model = "34465A", Resource = lab.Dmm.Resource },
            new DeviceConfig { Id = "ac", Name = "AC-Quelle", Adapter = KeysightAc6800Source.AdapterName, Model = "AC6802B", Resource = lab.AcSource.Resource, Shared = true },
            new DeviceConfig { Id = "netz", Name = "Netzgerät", Adapter = KeysightE36300Supply.AdapterName, Model = "E36312A", Resource = lab.Supply.Resource },
            new DeviceConfig { Id = "oszi", Name = "Oszilloskop", Adapter = KeysightInfiniiVisionScope.AdapterName, Model = "DSOX3034T", Resource = lab.Scope.Resource },
            new DeviceConfig { Id = "daq", Name = "DAQ", Adapter = InstrumentCatalog.DaqmxReplica, Resource = "Dev1",
                Options = new() { ["Sichere Ausgänge"] = "Dev1/port1/line0=0" } },
        ],
        Stations =
        [
            new StationConfig
            {
                Name = "Prüfplatz 01",
                Resources =
                [
                    new ResourceAssignment { Name = "AC_Quelle", Device = "ac", Channel = "VOLT", RequiresLock = true },
                    new ResourceAssignment { Name = "AC_Ein", Device = "ac", Channel = "OUT", RequiresLock = true },
                    new ResourceAssignment { Name = "Spannung_Pruefling", Device = "dmm", Channel = "VAC" },
                    new ResourceAssignment { Name = "Netz_L1", Device = "oszi", Channel = "CHAN1" },
                    new ResourceAssignment { Name = "Versorgung", Device = "netz", Channel = "CH1" },
                    new ResourceAssignment { Name = "Versorgung_Ein", Device = "netz", Channel = "CH1:OUT" },
                    new ResourceAssignment { Name = "Analog_Aus", Device = "daq", Channel = "Dev1/ao0" },
                    new ResourceAssignment { Name = "Relais", Device = "daq", Channel = "Dev1/port1/line0" },
                ],
            },
        ],
    };

    private static TestProject LabProject(Probe probe)
    {
        var mess = new GroupDefinition("mess", "Messgruppe");
        mess.Resources.AddRange(["AC_Quelle", "AC_Ein"]);
        mess.Add(
            Steps.Output("v", "AC 230 V", "AC_Quelle", 230),
            Steps.Output("ein", "AC ein", "AC_Ein", 1),
            Steps.Output("netz", "Versorgung 5 V", "Versorgung", 5),
            Steps.Output("netz.ein", "Versorgung ein", "Versorgung_Ein", 1),
            Steps.Output("ao", "Analog 2 V", "Analog_Aus", 2),
            Steps.Output("relais", "Relais ein", "Relais", 1),
            Steps.Measure("u", "Spannung Prüfling", "Spannung_Pruefling", "U", 220, 240, "V"),
            Steps.Code("kurve", "Netzkurve", async ctx =>
            {
                var set = await ctx.CaptureAsync(["Netz_L1"], 100_000, 2000);
                var rms = set.Channels[0].Rms();
                return new StepOutcome(Math.Abs(rms - 230) < 5 ? ResultRating.Pass : ResultRating.Fail, set);
            }),
            probe.Gate("halten"));
        mess.ExitGroup = Group("mess.exit", "Exit – AC aus").Add(Steps.Output("aus", "AC aus", "AC_Ein", 0));
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }.Var("Kennung", VarType.String, "SN-HW").Var("U", VarType.Double, 0.0).Add(mess);
        var project = new TestProject(new TestDefinition("Hardware", "1.0", Group("init"), Group("loop", "Loop").Add(dut), Group("fin")));
        project.Simulation.Add(new SimulatedResource("Spannung_Pruefling", "VAC", "V", 0));
        return project;
    }

    [Fact]
    public async Task Ablauf_mit_realen_Treibern_Werkerabbruch_Exit_und_sicherer_Zustand()
    {
        await using var lab = new SimulatedLab();
        var setup = LabSetup(lab);
        var probe = new Probe();
        var project = LabProject(probe);
        var issues = StationHardware.Check(setup, project);
        Assert.DoesNotContain(issues, i => i.IsError);
        Assert.DoesNotContain(issues, i => i.Text.Contains("Ablauf erwartet", StringComparison.Ordinal));

        // Konfigurationsprüfung kennt die Kanäle der Treiber
        var wrong = setup.Clone();
        wrong.Stations[0].Resources[2].Channel = "VOLTS";
        Assert.Contains(StationHardware.Check(wrong, project), i => i.IsError && i.Text.Contains("Kanal „VOLTS“ gibt es bei 34465A nicht"));
        // Reales Gerät ohne „gemeinsam“ auf zwei Prüfplätzen → Fehler
        var twice = setup.Clone();
        twice.Stations.Add(new StationConfig { Name = "Prüfplatz 02", Resources = [new ResourceAssignment { Name = "Spannung_Pruefling", Device = "dmm", Channel = "VAC" }] });
        Assert.Contains(StationHardware.Check(twice, project), i => i.IsError && i.Text.Contains("nicht gemeinsam"));
        // Reale Geräte: Sperre für das ganze Gerät
        Assert.Equal("ac", setup.KeyOf("Prüfplatz 01")("AC_Quelle"));
        Assert.Equal("ac", setup.KeyOf("Prüfplatz 01")("AC_Ein"));

        var coordinator = new StationCoordinator(setup, new ResourceManager());
        Assert.Null(coordinator.TryBeginRun("Prüfplatz 01", project.Definition));
        var run = new TestRun(project.Definition, new TestRunOptions
        {
            Resources = coordinator.Resources,
            Hardware = coordinator.BuildRegistry("Prüfplatz 01", project),
            AccessJournal = coordinator.Journal,
            StationName = "Prüfplatz 01",
        }, "Run-HW");
        var task = run.RunAsync();
        await probe.WhenReached("halten");
        Assert.True(lab.Bench.AcOn);
        Assert.Equal(230, lab.Bench.AcRms);
        var u = run.Steps("u").Single();
        Assert.Equal(ResultRating.Pass, u.Rating);
        Assert.Equal(230, (double)u.Value!, 0.1);
        Assert.Equal(ResultRating.Pass, run.Steps("kurve").Single().Rating);
        Assert.Equal(2.0, InstrumentCatalog.Replica.AoValue(0));

        // Werkerabbruch: Exit schaltet die AC-Quelle ab (Sperre noch gehalten), danach sicherer Zustand für alle Geräte
        run.RequestAbort();
        probe.Release("halten");
        var status = await task.Within();
        Assert.True(status.OperatorAbort);
        Assert.False(lab.Bench.AcOn);
        Assert.Contains("OUTP OFF", lab.AcSource.Device.Received);
        Assert.True(lab.Bench.DcOn[0]);                                         // Netzgerät: kein Exit im Ablauf …
        coordinator.EndRun("Prüfplatz 01");
        var problems = await coordinator.SafeStateAsync("Prüfplatz 01");
        Assert.Empty(problems);
        Assert.False(lab.Bench.DcOn[0]);                                        // … aber der sichere Zustand nach dem Lauf
        Assert.Contains("OUTP OFF,(@1:3)", lab.Supply.Device.Received);
        Assert.Equal(0, InstrumentCatalog.Replica.AoValue(0));
        Assert.Equal(0, InstrumentCatalog.Replica.DoValue(0));
        Assert.Empty(coordinator.Journal.Conflicts);
        Assert.Contains(coordinator.Journal.Recent(), a => a.Key == "ac" && a.Locked);

        // Nicht erreichbares Gerät: sicherer Zustand meldet es, die anderen werden trotzdem geschaltet
        await lab.Supply.DisposeAsync();
        lab.Bench.AcOn = true;
        coordinator.Pool.Adapters.Values.OfType<ScpiInstrument>().First(i => i.Model == "E36312A").Session.Timeout = TimeSpan.FromMilliseconds(500);
        var reported = await coordinator.SafeStateAsync("Prüfplatz 01");
        Assert.Contains(reported, p => p.StartsWith("Netzgerät: sicherer Zustand nicht bestätigt", StringComparison.Ordinal));
        Assert.False(lab.Bench.AcOn);
        coordinator.Pool.Reset();
    }

    [Fact]
    public async Task Konfigurationsseite_Laborsimulatoren_Geraet_testen_Kanalauswahl_und_Pruefung()
    {
        var ui = new QueueDispatcher();
        var project = SampleProjects.AcPruefung();
        var coordinator = new StationCoordinator(StationSetup.Default(project), new ResourceManager()) { IsSuggestion = true };
        var shell = new Presentation.ViewModels.ShellViewModel(ui, project: project, clock: SystemClock.Instance, stations: coordinator);
        var page = shell.StationsPage;
        try
        {
            await page.StartLabAsync();
            Assert.Contains(page.Devices, d => d.Id == "lab-dmm" && d.IsReal && d.Shared);
            Assert.Contains(page.Devices, d => d.Id == "lab-daq" && d.Adapter == InstrumentCatalog.DaqmxReplica);
            var dmm = page.Devices.Single(d => d.Id == "lab-dmm");
            await dmm.ProbeAsync();
            Assert.True(dmm.ProbeOk == true, dmm.ProbeText);
            Assert.Contains("34465A", dmm.ProbeText);
            var daq = page.Devices.Single(d => d.Id == "lab-daq");
            await daq.ProbeAsync();
            Assert.True(daq.ProbeOk == true, daq.ProbeText);
            Assert.Contains("USB-6211", daq.ProbeText);

            // Spannung_Pruefling auf das Multimeter legen: Kanalauswahl aus dem Treiber, Einheit, Prüfung
            var row = page.Rows.Single(r => r.Name == "Spannung_Pruefling");
            row.Device = dmm;
            Assert.Contains("VAC", row.ChannelChoices);
            row.Channel = "VDCX";
            Assert.Contains(page.Issues, i => i.IsError && i.Text.Contains("Kanal „VDCX“"));
            row.Channel = "VAC";
            Assert.Equal("V", row.Unit);
            Assert.DoesNotContain(page.Issues, i => i.IsError && i.Text.Contains("VDCX"));
            Assert.True(row.RequiresLock);                                        // gemeinsames Gerät → Sperre

            // Gerät ohne Ressource → Fehler; Treiberoptionen als Zeilen
            page.AddDeviceCommand.Execute(null);
            var added = page.Devices.Last();
            added.Adapter = KeysightE36300Supply.AdapterName;
            Assert.Equal("E36311A", added.Model);
            Assert.Equal("TCPIP::192.168.1.22::5025::SOCKET", added.Resource);
            added.OptionsText = "Kurvenkanäle=L1=1;L2=2\nNPLC=10";
            Assert.Equal("L1=1;L2=2", added.Device.Options!["Kurvenkanäle"]);
            added.Resource = "";
            Assert.Contains(page.Issues, i => i.IsError && i.Text.Contains("keine Ressource"));
            page.SelectedDevice = added;
            page.RemoveDeviceCommand.Execute(null);
            Assert.DoesNotContain(page.Devices, d => d == added);
        }
        finally
        {
            await SimulatedLabHost_Stop();
        }
    }

    private static Task SimulatedLabHost_Stop() => Presentation.ViewModels.SimulatedLabHost.StopAsync();

    [Fact]
    public async Task Dreiphasige_AC_Quelle_aus_drei_Einzelquellen()
    {
        // drei unabhängige AC-Quellen (eigene Bench je Phase)
        await using var l1 = new SimulatedLab();
        await using var l2 = new SimulatedLab();
        await using var l3 = new SimulatedLab();
        var resource = string.Join(";", l1.AcSource.Resource, l2.AcSource.Resource, l3.AcSource.Resource);
        using var three = (ThreePhaseAcSource)InstrumentCatalog.Create(ThreePhaseAcSource.AdapterName, "AC6802B", resource,
            new Dictionary<string, string> { ["Bereich"] = "310" });

        Assert.Contains(three.Channels, c => c.Name == "L2:VOLT");
        Assert.Contains(three.Channels, c => c.Name == "ALLE:FREQ");
        var probe = await three.ProbeAsync(default);
        Assert.True(probe.Ok, string.Join(" ", probe.Details));

        // ALLE: setzt alle drei Phasen; einzelne Phase überschreibt nur L2
        await three.WriteAsync("ALLE:FREQ", 50, default);
        await three.WriteAsync("ALLE:VOLT", 230, default);
        await three.WriteAsync("ALLE:OUT", 1, default);
        await three.WriteAsync("L2:VOLT", 115, default);

        Assert.Equal(230, await three.ReadAsync("L1:VOLT", default), 0.5);
        Assert.Equal(115, await three.ReadAsync("L2:VOLT", default), 0.5);
        Assert.Equal(230, await three.ReadAsync("L3:VOLT", default), 0.5);
        Assert.Equal(50, await three.ReadAsync("L1:FREQ", default), 0.1);
        // jede Phase bekam den VOLT:RANG-Befehl (Option Bereich)
        foreach (var ac in new[] { l1, l2, l3 }) Assert.Contains("VOLT:RANG 310", ac.AcSource.Device.Received.First(r => r.StartsWith("VOLT:RANG", StringComparison.Ordinal)));

        // sicherer Zustand schaltet alle drei aus
        await three.SafeStateAsync(default);
        foreach (var ac in new[] { l1, l2, l3 }) Assert.Contains("OUTP OFF", ac.AcSource.Device.Received);

        // falsche Adressanzahl wird abgelehnt
        Assert.Throws<InstrumentException>(() => InstrumentCatalog.Create(ThreePhaseAcSource.AdapterName, "AC6802B", l1.AcSource.Resource));
    }

}
