using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Kommunikation mit dem Prüfling (Abschnitt 1/7, Adapterfamilie „Kommunikationsgeräte“): Byte-Strom (Seriell/TCP/UDP),
/// Modbus RTU/TCP, CAN/CAN FD – Kodierung, Bausteine gegen die Simulation, Treiber gegen lokale Gegenstellen.
/// </summary>
public sealed class CommunicationTests
{
    private static StepDefinition S(string type, string id, params (string Key, string Value)[] p)
        => BlockCatalog.Default.Create(type, id, id, p.ToDictionary(x => x.Key, x => x.Value));

    // ------------------------------------------------------------------ Kodierung

    [Fact]
    public void Text_Hex_Escapes_und_Rundreise()
    {
        Assert.Equal([(byte)'A', 13, 10, 0x1B, (byte)'\\'], CommText.Parse("A\\r\\n\\x1B\\\\", hex: false));
        Assert.Equal([0x01, 0x03, 0x00, 0x0A], CommText.Parse("01 03 00 0A", hex: true));
        Assert.Equal([0x01, 0x03], CommText.ParseHex("0x0103"));
        Assert.Equal([0x0A], CommText.ParseHex("A"));
        Assert.Equal("OK\\r\\n", CommText.Format("OK\r\n"u8, hex: false));
        Assert.Equal("01 FF", CommText.Format(new byte[] { 1, 255 }, hex: true));
        Assert.Throws<FormatException>(() => CommText.ParseHex("0G"));
    }

    [Fact]
    public void Modbus_Datentypen_Wortfolge_Adressen_und_CRC()
    {
        var regs = ModbusCodec.Encode(230.5, ModbusDataType.Float32);
        Assert.Equal([0x4366, 0x8000], regs);
        Assert.Equal(230.5, (double)ModbusCodec.Decode(regs, ModbusDataType.Float32), 1e-6);
        var swapped = ModbusCodec.Encode(230.5, ModbusDataType.Float32, lowWordFirst: true);
        Assert.Equal([0x8000, 0x4366], swapped);
        Assert.Equal(230.5, (double)ModbusCodec.Decode(swapped, ModbusDataType.Float32, lowWordFirst: true), 1e-6);
        Assert.Equal(-5L, ModbusCodec.Decode(ModbusCodec.Encode(-5, ModbusDataType.Int16), ModbusDataType.Int16));
        Assert.Equal(-70000L, ModbusCodec.Decode(ModbusCodec.Encode(-70000, ModbusDataType.Int32), ModbusDataType.Int32));
        Assert.Equal("ABC1", ModbusCodec.Decode(ModbusCodec.Encode("ABC1", ModbusDataType.Text), ModbusDataType.Text));
        Assert.Throws<OverflowException>(() => ModbusCodec.Encode(70000, ModbusDataType.UInt16));

        Assert.Equal((ModbusArea.HoldingRegister, 100), ModbusCodec.ParseAddress("HR100"));
        Assert.Equal((ModbusArea.InputRegister, 4), ModbusCodec.ParseAddress("30005"));
        Assert.Equal((ModbusArea.HoldingRegister, 0), ModbusCodec.ParseAddress("40001"));
        Assert.Equal((ModbusArea.Coil, 2), ModbusCodec.ParseAddress("c2"));
        Assert.Equal((ModbusArea.DiscreteInput, 7), ModbusCodec.ParseAddress("DI7"));

        // Referenzrahmen: 01 03 00 00 00 0A → CRC C5 CD
        Assert.Equal(0xCDC5, ModbusDevice.Crc16([0x01, 0x03, 0x00, 0x00, 0x00, 0x0A]));
    }

    [Fact]
    public void CAN_Signale_Intel_Motorola_Vorzeichen_und_Rahmenpruefung()
    {
        byte[] intel = [0x34, 0x12, 0, 0, 0, 0, 0, 0];
        Assert.Equal(0x1234, new CanSignal(0, 16).Raw(intel));
        byte[] motorola = [0x12, 0x34, 0, 0, 0, 0, 0, 0];
        Assert.Equal(0x1234, new CanSignal(7, 16, Motorola: true).Raw(motorola));
        Assert.Equal(-2, new CanSignal(0, 8, Signed: true).Raw([0xFE]));
        Assert.Equal(23.0, new CanSignal(8, 8, Factor: 0.5, Offset: -10).Value([0, 66]), 1e-9);

        var data = new byte[8];
        new CanSignal(4, 12, Factor: 0.1).Write(data, 230.5);
        Assert.Equal(230.5, new CanSignal(4, 12, Factor: 0.1).Value(data), 1e-9);
        var m = new byte[8];
        new CanSignal(7, 16, Motorola: true).Write(m, 0xABCD);
        Assert.Equal([0xAB, 0xCD], m[..2]);

        Assert.Null(new CanFrame(0x7FF, new byte[8]).Validate());
        Assert.NotNull(new CanFrame(0x800, []).Validate());
        Assert.NotNull(new CanFrame(0x100, new byte[9]).Validate());
        Assert.Null(new CanFrame(0x100, new byte[12], Fd: true).Validate());
        Assert.NotNull(new CanFrame(0x100, new byte[10], Fd: true).Validate());
        Assert.True(new CanFilter(0x700, 0x7F0).Matches(new CanFrame(0x70A, [])));
    }

    // ------------------------------------------------------------------ Bausteine gegen die Simulation

    private static TestProject CommProject(GroupDefinition loop)
    {
        var project = new TestProject(Test(loop));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_Seriell", "seriell", CommKind.Stream,
            "# Textprotokoll des Prüflings\n\\*IDN\\? => ACME,DUT-1,SN123,1.0\nMEAS:VOLT\\? => {zufall:229.5:230.5:2}\nSET:OUT (\\d) => OK $1\nERR => FEHLER 17"));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_Modbus", "1", CommKind.Modbus,
            "HR0=1234\nIR10:float32=230.5\nC3=1\nHR20=0\nHR30:text=FW1.2"));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_CAN", "can", CommKind.Can,
            "0x7E0 02 01 0C => 0x7E8 04 41 0C $3 F8\nzyklisch 0x100 FA 08 00 00"));
        return project;
    }

    private static async Task<TestRun> RunAsync(TestProject project)
    {
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = SampleProjects.CreateSimulation(project).Registry });
        await run.RunAsync().Within();
        return run;
    }

    [Fact]
    public async Task Abfrage_Muster_Zahlenwert_Grenzwerte_und_Zeitlimit()
    {
        var loop = Group("loop").Var("Typ", VarType.String).Var("U", VarType.Double).Var("Kanal", VarType.Int, 3L).Add(
            S(CommunicationBlocks.Query, "idn", ("Ressource", "Pruefling_Seriell"), ("Daten", "*IDN?"), ("Muster", "^ACME,(DUT-\\d+),"), ("Ziel", "Typ")),
            S(CommunicationBlocks.Query, "volt", ("Ressource", "Pruefling_Seriell"), ("Daten", "MEAS:VOLT?"), ("Ziel", "U"),
                ("Untergrenze", "229"), ("Obergrenze", "231"), ("Einheit", "V")),
            S(CommunicationBlocks.Query, "out", ("Ressource", "Pruefling_Seriell"), ("Daten", "SET:OUT {Kanal}"), ("Muster", "^OK 3$")),
            S(CommunicationBlocks.Query, "err", ("Ressource", "Pruefling_Seriell"), ("Daten", "ERR"), ("Muster", "^OK")),
            S(CommunicationBlocks.Query, "still", ("Ressource", "Pruefling_Seriell"), ("Daten", "UNBEKANNT"), ("Zeitlimit", "50")),
            S(CommunicationBlocks.Query, "stillFail", ("Ressource", "Pruefling_Seriell"), ("Daten", "UNBEKANNT"), ("Zeitlimit", "50"), ("BeiZeitlimit", "FAIL")),
            S(CommunicationBlocks.Send, "send", ("Ressource", "Pruefling_Seriell"), ("Daten", "*IDN?")),
            S(CommunicationBlocks.Receive, "recv", ("Ressource", "Pruefling_Seriell"), ("Muster", "SN(\\d+)")),
            Steps.Code("check", "check", ctx => ctx.Get("Typ") is "DUT-1" && ctx.Get("U") is double u && u is > 229 and < 231
                ? StepOutcome.Pass() : StepOutcome.Fail($"{ctx.Get("Typ")} {ctx.Get("U")}")));
        var run = await RunAsync(CommProject(loop));

        Assert.Equal(ResultRating.Pass, run.Steps("idn").Single().Rating);
        var volt = run.Steps("volt").Single();
        Assert.Equal(ResultRating.Pass, volt.Rating);
        Assert.Equal("V", volt.Unit);
        Assert.Equal(229.0, volt.LowLimit);
        Assert.Equal(ResultRating.Pass, run.Steps("out").Single().Rating);
        var err = run.Steps("err").Single();
        Assert.Equal(ResultRating.Fail, err.Rating);
        Assert.Contains("FEHLER 17", err.Message);
        Assert.Equal(ResultRating.Error, run.Steps("still").Single().Rating);
        Assert.Contains("Keine vollständige Antwort", run.Steps("still").Single().Message);
        Assert.Equal(ResultRating.Fail, run.Steps("stillFail").Single().Rating);
        Assert.Equal(ResultRating.NResult, run.Steps("send").Single().Rating);
        Assert.Equal("123", run.Steps("recv").Single().Value);
        Assert.Equal(ResultRating.Pass, run.Steps("check").Single().Rating);
    }

    [Fact]
    public async Task Modbus_lesen_schreiben_zuruecklesen_und_Ausnahme()
    {
        var loop = Group("loop").Var("U", VarType.Double).Var("Fw", VarType.String).Add(
            S(CommunicationBlocks.ModbusRead, "u", ("Ressource", "Pruefling_Modbus"), ("Adresse", "IR10"), ("Datentyp", "Float32"), ("Ziel", "U"),
                ("Untergrenze", "229"), ("Obergrenze", "231"), ("Einheit", "V")),
            S(CommunicationBlocks.ModbusRead, "status", ("Ressource", "Pruefling_Modbus"), ("Adresse", "40001"), ("Sollwert", "1234")),
            S(CommunicationBlocks.ModbusRead, "skaliert", ("Ressource", "Pruefling_Modbus"), ("Adresse", "HR0"), ("Faktor", "0.1")),
            S(CommunicationBlocks.ModbusRead, "coil", ("Ressource", "Pruefling_Modbus"), ("Adresse", "C3"), ("Sollwert", "true")),
            S(CommunicationBlocks.ModbusRead, "fw", ("Ressource", "Pruefling_Modbus"), ("Adresse", "HR30"), ("Datentyp", "Text"), ("Laenge", "6"), ("Ziel", "Fw")),
            S(CommunicationBlocks.ModbusWrite, "w", ("Ressource", "Pruefling_Modbus"), ("Adresse", "HR20"), ("Wert", "70 + 7"), ("Zuruecklesen", "true")),
            S(CommunicationBlocks.ModbusRead, "r", ("Ressource", "Pruefling_Modbus"), ("Adresse", "HR20"), ("Sollwert", "77")),
            S(CommunicationBlocks.ModbusWrite, "zuGross", ("Ressource", "Pruefling_Modbus"), ("Adresse", "HR20"), ("Wert", "70000")),
            S(CommunicationBlocks.ModbusRead, "fehlt", ("Ressource", "Pruefling_Modbus"), ("Adresse", "HR999")));
        var run = await RunAsync(CommProject(loop));

        var u = run.Steps("u").Single();
        Assert.Equal(ResultRating.Pass, u.Rating);
        Assert.Equal(230.5, (double)u.Value!, 1e-6);
        Assert.Equal(ResultRating.Pass, run.Steps("status").Single().Rating);
        Assert.Equal(123.4, (double)run.Steps("skaliert").Single().Value!, 1e-9);
        Assert.Equal(ResultRating.Pass, run.Steps("coil").Single().Rating);
        Assert.Equal("FW1.2", run.Steps("fw").Single().Value);
        Assert.Equal(ResultRating.NResult, run.Steps("w").Single().Rating);
        Assert.Equal(ResultRating.Pass, run.Steps("r").Single().Rating);
        Assert.Equal(ResultRating.Error, run.Steps("zuGross").Single().Rating);
        var missing = run.Steps("fehlt").Single();
        Assert.Equal(ResultRating.Error, missing.Rating);
        Assert.Contains("unzulässige Datenadresse", missing.Message);
    }

    [Fact]
    public async Task CAN_Anfrage_Antwort_Datenmuster_Signal_und_zyklischer_Rahmen()
    {
        var loop = Group("loop").Var("Drehzahl", VarType.Double).Add(
            S(CommunicationBlocks.CanReceive, "obd", ("Ressource", "Pruefling_CAN"), ("Id", "0x7E8"), ("AnfrageId", "0x7E0"), ("AnfrageDaten", "02 01 0C"),
                ("Erwartet", "04 41 0C ** **"), ("Startbit", "31"), ("Bits", "16"), ("Byteorder", "Motorola"), ("Faktor", "0.25"), ("Ziel", "Drehzahl")),
            S(CommunicationBlocks.CanReceive, "zyklisch", ("Ressource", "Pruefling_CAN"), ("Id", "0x100"), ("Bits", "16"), ("Faktor", "0.1"),
                ("Untergrenze", "220"), ("Obergrenze", "240"), ("Einheit", "V")),
            S(CommunicationBlocks.CanReceive, "falsch", ("Ressource", "Pruefling_CAN"), ("Id", "0x7E8"), ("AnfrageId", "0x7E0"), ("AnfrageDaten", "02 01 0C"),
                ("Erwartet", "04 41 0D ** **")),
            S(CommunicationBlocks.CanReceive, "keiner", ("Ressource", "Pruefling_CAN"), ("Id", "0x555"), ("Zeitlimit", "20")),
            S(CommunicationBlocks.CanSend, "send", ("Ressource", "Pruefling_CAN"), ("Id", "0x18DAF110"), ("Daten", "03 22 F1 {144}")),
            S(CommunicationBlocks.CanSend, "fd", ("Ressource", "Pruefling_CAN"), ("Id", "0x123"), ("Fd", "true"), ("Daten", "01 02 03 04 05 06 07 08 09 0A")));
        var project = CommProject(loop);
        var (sim, registry) = SampleProjects.CreateSimulation(project);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry });
        await run.RunAsync().Within();

        var obd = run.Steps("obd").Single();
        Assert.Equal(ResultRating.Pass, obd.Rating);
        Assert.Equal(0x0CF8 / 4.0, (double)obd.Value!, 1e-9); // Antwortbyte 4 = Anfragebyte 3 (0C), Byte 5 = F8
        var zyklisch = run.Steps("zyklisch").Single();
        Assert.Equal(ResultRating.Pass, zyklisch.Rating);
        Assert.Equal(229.8, (double)zyklisch.Value!, 1e-9);
        Assert.Equal(ResultRating.Fail, run.Steps("falsch").Single().Rating);
        Assert.Equal(ResultRating.Error, run.Steps("keiner").Single().Rating);
        Assert.Equal(ResultRating.NResult, run.Steps("send").Single().Rating);
        Assert.Equal(ResultRating.Error, run.Steps("fd").Single().Rating);   // 10 Bytes: keine gültige FD-Länge
        var can = (SimulatedCanDut)sim.CommunicationEndpoint("can")!;
        Assert.Contains(can.Sent, f => f.Id == 0x18DAF110 && f.Extended && f.Data.SequenceEqual(new byte[] { 3, 0x22, 0xF1, 0x90 }));
    }

    [Fact]
    public async Task Gemeinsame_Schnittstelle_nur_mit_Reservierung_und_Projektformat()
    {
        var unreserved = Group("loop").Add(S(CommunicationBlocks.Query, "q", ("Ressource", "Bus"), ("Daten", "*IDN?")));
        var project = new TestProject(Test(unreserved));
        project.SimulationComm.Add(new SimulatedComm("Bus", "rs485", CommKind.Stream, "\\*IDN\\? => X", RequiresLock: true));
        var run = await RunAsync(project);
        Assert.Contains("ohne Reservierung", run.Steps("q").Single().Message);

        var reserved = Group("res", "Reserviert");
        reserved.Resources.Add("Bus");
        reserved.Add(S(CommunicationBlocks.Query, "q2", ("Ressource", "Bus"), ("Daten", "*IDN?"), ("Muster", "X")));
        var project2 = new TestProject(Test(Group("loop").Add(reserved)));
        project2.SimulationComm.Add(project.SimulationComm[0]);
        var resources = new ResourceManager();
        var run2 = new TestRun(project2.Definition, new TestRunOptions { Clock = new VirtualClock(), Resources = resources, Hardware = SampleProjects.CreateSimulation(project2).Registry });
        await run2.RunAsync().Within();
        Assert.Equal(ResultRating.Pass, run2.Steps("q2").Single().Rating);

        // Projektformat: Simulationsregeln bleiben erhalten
        var serializer = new ProjectSerializer();
        var back = serializer.Deserialize(serializer.Serialize(CommProject(Group("loop"))));
        Assert.Equal(3, back.SimulationComm.Count);
        Assert.Equal(CommKind.Modbus, back.SimulationComm[1].Kind);
        Assert.Contains("zufall", back.SimulationComm[0].Rules);
        Assert.Contains("Pruefling_CAN", StationSetup.UsedResources(back));
        Assert.Contains(StationSetup.Default(back).Stations[0].Resources, r => r.Name == "Pruefling_Modbus" && r.Channel == "1");

        // Ungültige Regeln werden gemeldet
        Assert.NotEmpty(CommSimulation.Validate(CommKind.Stream, "ohne Pfeil"));
        Assert.NotEmpty(CommSimulation.Validate(CommKind.Modbus, "XY5=1"));
        Assert.Empty(CommSimulation.Validate(CommKind.Can, "zyklisch 0x100 01 02"));
    }

    [Fact]
    public void Bausteinkonfiguration_wird_geprueft()
    {
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.Query, "a", ("Ressource", "R"), ("Daten", "X"), ("Muster", "(unbalanciert"))));
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.Receive, "b", ("Ressource", "R"), ("Ende", "Länge"))));
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.ModbusWrite, "c", ("Ressource", "R"), ("Adresse", "IR3"), ("Wert", "1"))));
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.ModbusRead, "d", ("Ressource", "R"), ("Adresse", "ZZ"))));
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.CanSend, "e", ("Ressource", "R"), ("Id", "0x20000000"))));
        Assert.NotEmpty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.Send, "f", ("Ressource", "R"), ("Format", "Hex"), ("Daten", "0Z"))));
        Assert.Empty(BlockCatalog.Default.Rebuild(S(CommunicationBlocks.Query, "g", ("Ressource", "R"), ("Format", "Hex"), ("Daten", "01 03 00 00 00 01"), ("Ende", "Länge"), ("Laenge", "7"))));
        Assert.Contains(BlockCatalog.Default.Categories, c => c == CommunicationBlocks.Category);
    }

    // ------------------------------------------------------------------ Treiber gegen lokale Gegenstellen

    [Fact]
    public async Task TCP_Textprotokoll_mit_echter_Verbindung()
    {
        await using var server = new LineServer(line => line switch
        {
            "*IDN?" => "ACME,DUT-1\r\n",
            "TEIL" => "AB",
            _ => null,
        });
        using var device = (StreamDevice)InstrumentCatalog.Create(StreamDevice.TcpAdapter, "", $"127.0.0.1:{server.Port}");
        var rule = new ReceiveRule([13, 10], null, TimeSpan.FromSeconds(2));
        await device.SendAsync("", "*IDN?\r\n"u8.ToArray(), default);
        Assert.Equal("ACME,DUT-1", CommText.Raw(await device.ReceiveAsync("", rule, default)));

        await device.SendAsync("", "TEIL\r\n"u8.ToArray(), default);
        var ex = await Assert.ThrowsAsync<CommunicationTimeoutException>(() => device.ReceiveAsync("", rule with { Timeout = TimeSpan.FromMilliseconds(200) }, default));
        Assert.Equal("AB"u8.ToArray(), ex.Partial);

        // Ruhe als Ende, Länge
        await device.SendAsync("", "TEIL\r\n"u8.ToArray(), default);
        Assert.Equal("AB", CommText.Raw(await device.ReceiveAsync("", new ReceiveRule(null, null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50)), default)));
        await device.SendAsync("", "*IDN?\r\n"u8.ToArray(), default);
        Assert.Equal("ACME", CommText.Raw(await device.ReceiveAsync("", new ReceiveRule(null, 4, TimeSpan.FromSeconds(2)), default)));
        device.DiscardInput("");
        Assert.Contains(device.Log, l => l.Contains("→ *IDN?"));

        var probe = await ((StreamDevice)InstrumentCatalog.Create(StreamDevice.TcpAdapter, "", $"127.0.0.1:{server.Port}",
            new Dictionary<string, string> { ["Prüfbefehl"] = "*IDN?" })).ProbeAsync(default);
        Assert.True(probe.Ok, string.Join("; ", probe.Details));
        Assert.Equal("ACME,DUT-1", probe.Identity);

        var refused = await ((StreamDevice)InstrumentCatalog.Create(StreamDevice.TcpAdapter, "", "127.0.0.1:1")).ProbeAsync(default);
        Assert.False(refused.Ok);
    }

    [Fact]
    public async Task Modbus_TCP_Rahmenbildung_gegen_Simulatorserver()
    {
        var backend = new SimulatedModbusDut("HR0=1234\nIR10:float32=230.5\nC0=0\nC1=1\nHR100=0\nHR101=0");
        await using var server = new ModbusTcpSimulatorServer(backend);
        using var device = (ModbusDevice)InstrumentCatalog.Create(ModbusDevice.TcpAdapter, "", $"127.0.0.1:{server.Port}", null, 500);

        // Awaits in lokale Variablen, da xUnit sonst die ReadOnlySpan-Überladung von Assert.Equal wählt (Span über await = CS4007).
        var hr0 = await device.ReadRegistersAsync("1", ModbusArea.HoldingRegister, 0, 1, default);
        Assert.Equal([(ushort)1234], hr0);
        Assert.Equal(230.5, await device.ReadAsync("1/IR10:float32", default), 1e-6);
        var coils = await device.ReadBitsAsync("1", ModbusArea.Coil, 0, 2, default);
        Assert.Equal([false, true], coils);
        await device.WriteRegistersAsync("1", 100, [7, 8], default);
        var hr100 = await device.ReadRegistersAsync("1", ModbusArea.HoldingRegister, 100, 2, default);
        Assert.Equal([(ushort)7, 8], hr100);
        await device.WriteAsync("1/C0", 1, default);
        Assert.True((await device.ReadBitsAsync("1", ModbusArea.Coil, 0, 1, default))[0]);
        var ex = await Assert.ThrowsAsync<ModbusException>(() => device.ReadRegistersAsync("1", ModbusArea.HoldingRegister, 500, 1, default));
        Assert.Equal(2, ex.Code);

        server.Silent = true;
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() => device.ReadRegistersAsync("1", ModbusArea.HoldingRegister, 0, 1, default));
        Assert.Contains(device.Log, l => l.Contains("Wiederholung"));
        Assert.True(device.AcceptsChannel("2/HR5:int32"));
        Assert.True(device.AcceptsChannel("17"));
        Assert.False(device.AcceptsChannel("HR"));
    }

    [Fact]
    public async Task Modbus_RTU_CRC_und_Wiederholung_ueber_Testschleife()
    {
        var backend = new SimulatedModbusDut("HR0=42\nHR1=43");
        var (client, dut) = LoopbackTransport.Pair();
        await using var responder = new ModbusRtuResponder(dut, backend, unit: 5);
        using var device = new ModbusDevice(ModbusDevice.RtuAdapter, "COM9", new InstrumentOptions(new Dictionary<string, string> { ["Einheit"] = "5" }), 300, client);

        var rtu0 = await device.ReadRegistersAsync("5", ModbusArea.HoldingRegister, 0, 2, default);
        Assert.Equal([(ushort)42, 43], rtu0);
        responder.CorruptNext = true;
        var rtu1 = await device.ReadRegistersAsync("5", ModbusArea.HoldingRegister, 0, 1, default); // CRC-Fehler → Wiederholung
        Assert.Equal([(ushort)42], rtu1);
        Assert.Contains(device.Log, l => l.Contains("Prüfsummenfehler"));
        await device.WriteRegistersAsync("5", 1, [99], default);
        var rtu2 = await device.ReadRegistersAsync("5", ModbusArea.HoldingRegister, 1, 1, default);
        Assert.Equal([(ushort)99], rtu2);
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() => device.ReadRegistersAsync("6", ModbusArea.HoldingRegister, 0, 1, default)); // andere Unit antwortet nicht
    }

    [Fact]
    public async Task CAN_virtueller_Bus_Filter_FD_und_Einstellungen()
    {
        var bus = "Test-" + Guid.NewGuid().ToString("N")[..6];
        using var tester = (CanDevice)InstrumentCatalog.Create(CanDevice.VirtualAdapter, "", bus, new Dictionary<string, string> { ["FD"] = "ja" });
        using var dut = (CanDevice)InstrumentCatalog.Create(CanDevice.VirtualAdapter, "", bus, new Dictionary<string, string> { ["FD"] = "ja" });
        Assert.True((await dut.ProbeAsync(default)).Ok);

        await tester.SendAsync("Bus", new CanFrame(0x100, [1]), default);
        await tester.SendAsync("Bus", new CanFrame(0x7E0, [2, 1, 0x0C]), default);
        await tester.SendAsync("Bus", new CanFrame(0x123, new byte[16], Fd: true, BitRateSwitch: true), default);
        var request = await dut.ReceiveAsync("Bus", new CanFilter(0x7E0), TimeSpan.FromSeconds(2), default);
        Assert.Equal([2, 1, 0x0C], request.Data);
        Assert.Equal(0x100u, (await dut.ReceiveAsync("Bus", new CanFilter(0x100), TimeSpan.FromSeconds(2), default)).Id); // ältere bleiben verfügbar
        var fd = await dut.ReceiveAsync("Bus", new CanFilter(0x120, 0x7F0), TimeSpan.FromSeconds(2), default);
        Assert.True(fd.Fd && fd.BitRateSwitch && fd.Data.Length == 16);
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() => dut.ReceiveAsync("Bus", new CanFilter(0x7E0), TimeSpan.FromMilliseconds(50), default));
        await Assert.ThrowsAsync<CommunicationException>(() => tester.SendAsync("Bus", new CanFrame(0x100, new byte[9]), default));

        using var classic = (CanDevice)InstrumentCatalog.Create(CanDevice.VirtualAdapter, "", bus);
        await Assert.ThrowsAsync<CommunicationException>(() => classic.SendAsync("Bus", new CanFrame(0x1, new byte[12], Fd: true), default));

        Assert.Equal((ushort)0x51, PcanBackend.ParseHandle("PCAN_USBBUS1"));
        Assert.Equal((ushort)0x509, PcanBackend.ParseHandle("PCAN_USBBUS9"));
        Assert.Equal((ushort)0x801, PcanBackend.ParseHandle("pcan_lanbus1"));
        Assert.Equal((ushort)0x001C, PcanBackend.BitrateCode(500_000));
        Assert.Equal("f_clock_mhz=80,nom_brp=2,nom_tseg1=63,nom_tseg2=16,nom_sjw=16,data_brp=2,data_tseg1=15,data_tseg2=4,data_sjw=4", PcanBackend.FdBitrate(500_000, 2_000_000));
        Assert.Equal(-2, KvaserBackend.Bitrate(500_000));
        Assert.Equal(-1002, KvaserBackend.FdData(2_000_000));
        Assert.Equal(1_000_000, CanBusSettings.ParseRate("1M"));
        Assert.Equal(250_000, CanBusSettings.ParseRate("250 kbit/s"));

        // Ohne Treiber: verständlicher Fehler statt Absturz
        var pcan = await ((CanDevice)InstrumentCatalog.Create(CanDevice.PcanAdapter, "", "PCAN_USBBUS1")).ProbeAsync(default);
        Assert.False(pcan.Ok);
        Assert.Contains(pcan.Details, d => d.Contains("Treiber nicht installiert"));
    }

    [Fact]
    public void Pruefplatzkonfiguration_mit_Kommunikationsgeraeten()
    {
        var project = CommProject(Group("loop"));
        var setup = StationSetup.Default(project, 1);
        setup.Devices.Add(new DeviceConfig { Id = "mb", Name = "Modbus-Prüfling", Adapter = ModbusDevice.TcpAdapter, Resource = "127.0.0.1:1502" });
        setup.Devices.Add(new DeviceConfig { Id = "rs", Name = "RS-232", Adapter = StreamDevice.SerialAdapter, Resource = "COM3", OptionsText = "Baud=115200" });
        var station = setup.Stations[0];
        station.Resources.RemoveAll(r => r.Name is "Pruefling_Modbus" or "Pruefling_Seriell");
        station.Resources.Add(new ResourceAssignment { Name = "Pruefling_Modbus", Device = "mb", Channel = "1/HR0:float32" });
        station.Resources.Add(new ResourceAssignment { Name = "Pruefling_Seriell", Device = "rs", Channel = "Daten" });
        var issues = StationHardware.Check(setup, project);
        Assert.DoesNotContain(issues, i => i.Severity == ConfigSeverity.Error);

        station.Resources.Single(r => r.Name == "Pruefling_Modbus").Channel = "HRx";
        Assert.Contains(StationHardware.Check(setup, project), i => i.Severity == ConfigSeverity.Error && i.Text.Contains("ungültig"));
        Assert.Contains(InstrumentCatalog.Families, f => f.Adapter == CanDevice.PcanAdapter);
        Assert.Throws<InstrumentException>(() => InstrumentCatalog.Create(StreamDevice.SerialAdapter, "", "COM1", new Dictionary<string, string> { ["Parität"] = "X" }));
    }

    [Fact]
    public void Editor_Kommunikations_Simulation_bearbeiten()
    {
        var editor = new EditorViewModel(SampleProjects.AcPruefung());
        var props = new ProjectPropertiesViewModel(editor);
        Assert.Empty(props.CommSimulations);
        props.AddCommCommand.Execute(null);
        var row = Assert.Single(props.CommSimulations);
        Assert.False(row.HasProblems);
        row.Kind = CommKind.Modbus;
        Assert.True(row.HasProblems);                 // Textregeln sind kein Registerabbild
        row.Rules = "HR0=1\nIR3:float32=230±2";
        Assert.False(row.HasProblems);
        row.Name = "Pruefling_Modbus";
        row.Channel = "1";
        Assert.Equal(new SimulatedComm("Pruefling_Modbus", "1", CommKind.Modbus, "HR0=1\nIR3:float32=230±2"), editor.Project.SimulationComm.Single());
        Assert.Contains("HR0=1234", row.Help);
        props.RemoveCommCommand.Execute(null);   // ohne Auswahl: nichts
        props.SelectedComm = row;
        props.RemoveCommCommand.Execute(null);
        Assert.Empty(editor.Project.SimulationComm);
    }
}
