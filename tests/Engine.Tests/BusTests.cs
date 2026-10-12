using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Offene Punkte aus ADR 0011, jetzt umgesetzt: DBC-/LDF-Import mit Signalbausteinen, ISO-TP/UDS, J1939 mit Transportprotokoll,
/// LIN, Vector-XL- und PLIN-Anbindung (ohne Hardware: Konfiguration), Kommunikation aus CodeBlocks über die zentrale Vermittlung.
/// </summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class BusTests
{
    internal const string Dbc = """
        VERSION "1.0"

        NS_ :
            NS_DESC_
            CM_

        BS_:

        BU_: Tester Motor

        BO_ 256 Motor_1: 8 Motor
         SG_ Drehzahl : 0|16@1+ (0.25,0) [0|16383.75] "1/min" Tester
         SG_ Temperatur : 16|8@1- (1,-40) [-40|215] "°C" Tester
         SG_ Modus : 24|2@1+ (1,0) [0|3] "" Tester
         SG_ Spannung : 39|16@0+ (0.01,0) [0|655.35] "V" Tester

        BO_ 512 Diagnose_Mux: 8 Motor
         SG_ Seite M : 0|8@1+ (1,0) [0|255] "" Tester
         SG_ Strom m1 : 8|16@1+ (0.1,0) [0|6553.5] "A" Tester
         SG_ Fehler m2 : 8|8@1+ (1,0) [0|255] "" Tester

        BO_ 2364539904 EEC1: 8 Motor
         SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" Tester

        BO_ 768 Sollwerte: 8 Tester
         SG_ SollDrehzahl : 0|16@1+ (1,0) [0|8000] "1/min" Motor
         SG_ Freigabe : 16|1@1+ (1,0) [0|1] "" Motor
         SG_ Leistung : 32|32@1- (1,0) [0|0] "W" Motor

        BO_ 3221225472 VECTOR__INDEPENDENT_SIG_MSG: 0 Vector__XXX
         SG_ Unbenutzt : 0|8@1+ (1,0) [0|0] "" Vector__XXX

        CM_ BO_ 256 "Motordaten";
        CM_ SG_ 256 Drehzahl "Ist-Drehzahl
        über zwei Zeilen";
        BA_DEF_ BO_ "GenMsgCycleTime" INT 0 10000;
        BA_DEF_DEF_ "GenMsgCycleTime" 0;
        BA_ "ProtocolType" "J1939";
        BA_ "GenMsgCycleTime" BO_ 256 100;
        BA_ "GenSigStartValue" SG_ 768 Freigabe 1;
        VAL_ 256 Modus 0 "Aus" 1 "Standby" 2 "Aktiv" 3 "Fehler" ;
        SIG_VALTYPE_ 768 Leistung : 1;
        """;

    internal const string Ldf = """
        LIN_description_file;
        LIN_protocol_version = "2.1";
        LIN_language_version = "2.1";
        LIN_speed = 19.2 kbps;
        /* Fensterheber-Beispiel */
        Nodes {
          Master: BCM, 5 ms, 0.1 ms;
          Slaves: Fensterheber;
        }
        Signals {
          Position: 8, 0, Fensterheber, BCM;
          Status: 2, 0, Fensterheber, BCM;
          Daten: 16, {0, 0}, Fensterheber, BCM;
          Befehl: 2, 0, BCM, Fensterheber;
          Geschwindigkeit: 8, 128, BCM, Fensterheber;
        }
        Frames {
          FH_Status: 0x21, Fensterheber, 4 {
            Position, 0;
            Status, 8;
            Daten, 16;
          }
          FH_Befehl: 0x10, BCM, 2 {
            Befehl, 0;
            Geschwindigkeit, 8;
          }
        }
        Event_triggered_frames {
          ETF_Status: 0x20, FH_Status;
        }
        Sporadic_frames {
          SF_Befehl: FH_Befehl;
        }
        Signal_encoding_types {
          PosEnc { physical_value, 0, 200, 0.5, 0, "%"; }
          StatusEnc { logical_value, 0, "Ruhe"; logical_value, 1, "Auf"; logical_value, 2, "Ab"; }
          BefehlEnc { logical_value, 0, "Stopp"; logical_value, 1, "Auf"; logical_value, 2, "Ab"; }
        }
        Signal_representation {
          PosEnc: Position;
          StatusEnc: Status;
          BefehlEnc: Befehl;
        }
        """;

    private static StepDefinition S(string type, string id, params (string Key, string Value)[] p)
        => BlockCatalog.Default.Create(type, id, id, p.ToDictionary(x => x.Key, x => x.Value));

    // ------------------------------------------------------------------ DBC/LDF

    [Fact]
    public void DBC_Botschaften_Signale_Kommentare_Wertetabellen_Gleitkomma_J1939()
    {
        var db = BusDatabase.Parse(Dbc, BusDatabaseFormat.Dbc);
        Assert.Equal(4, db.Messages.Count);                         // Pseudo-Botschaft übersprungen
        Assert.Equal(["Tester", "Motor"], db.Nodes);
        Assert.True(db.IsJ1939);
        var motor = db.RequireMessage("Motor_1");
        Assert.Equal(0x100u, motor.Id);
        Assert.Equal(100, motor.CycleMs);
        Assert.Equal("Motordaten", motor.Comment);
        Assert.Contains("zwei Zeilen", motor.RequireSignal("Drehzahl").Comment);
        Assert.Equal("°C", motor.RequireSignal("Temperatur").Unit);
        Assert.Equal("Aktiv", motor.RequireSignal("Modus").Values[2]);
        Assert.True(motor.RequireSignal("Spannung").Coding.Motorola);
        Assert.Same(motor, db.Message("0x100"));
        Assert.Same(motor, db.Message("256"));

        var eec1 = db.RequireMessage("EEC1");
        Assert.True(eec1.Extended);
        Assert.Equal(0x0CF00400u, eec1.Id);
        Assert.Equal(0xF004u, eec1.Pgn);
        Assert.Same(eec1, db.ByPgn(0xF004));
        Assert.True(db.RequireMessage("Sollwerte").RequireSignal("Leistung").Coding.IsFloat);

        // Dekodieren: A0 0F → 4000 × 0,25 = 1000; 5A → 90 − 40 = 50; Modus 2; Motorola 59 D8 → 230,00 V
        byte[] data = [0xA0, 0x0F, 0x5A, 0x02, 0x59, 0xD8, 0, 0];
        var values = motor.Decode(data).ToDictionary(d => d.Signal.Name, d => (d.Value, d.Text));
        Assert.Equal(1000.0, values["Drehzahl"].Value, 1e-9);
        Assert.Equal(50.0, values["Temperatur"].Value, 1e-9);
        Assert.Equal((2.0, "Aktiv"), values["Modus"]);
        Assert.Equal(230.0, values["Spannung"].Value, 1e-9);
        Assert.Equal(data, motor.Encode(new Dictionary<string, double> { ["Drehzahl"] = 1000, ["Temperatur"] = 50, ["Modus"] = 2, ["Spannung"] = 230 }));
        Assert.Equal(2.0, motor.RequireSignal("Modus").ParseValue("aktiv"));
        Assert.Throws<BusDatabaseException>(() => motor.Encode(new Dictionary<string, double> { ["Drehzahl"] = 20000 }));

        // Startwert, Gleitkomma
        var soll = db.RequireMessage("Sollwerte");
        var encoded = soll.Encode(new Dictionary<string, double> { ["SollDrehzahl"] = 1500, ["Leistung"] = 1234.5 });
        var decoded = soll.Decode(encoded).ToDictionary(d => d.Signal.Name, d => d.Value);
        Assert.Equal(1500.0, decoded["SollDrehzahl"]);
        Assert.Equal(1.0, decoded["Freigabe"]);
        Assert.Equal(1234.5, decoded["Leistung"], 1e-3);

        // Multiplex: Multiplexer wird gesetzt, nur gültige Signale werden dekodiert
        var mux = db.RequireMessage("Diagnose_Mux");
        var page = mux.Encode(new Dictionary<string, double> { ["Strom"] = 12.5 });
        Assert.Equal(1, page[0]);
        Assert.Equal(["Seite", "Strom"], mux.Decode(page).Select(d => d.Signal.Name));
        Assert.Throws<BusDatabaseException>(() => mux.Encode(new Dictionary<string, double> { ["Strom"] = 1, ["Fehler"] = 2 }));

        Assert.Equal((db.RequireMessage("Motor_1"), motor.RequireSignal("Modus")), db.FindSignal("Modus"));
        Assert.Throws<BusDatabaseException>(() => db.FindSignal("Gibtsnicht"));
        var bad = Assert.Throws<BusDatabaseException>(() => BusDatabase.Parse("BO_ 1 X: 8 Y\n SG_ kaputt", BusDatabaseFormat.Dbc));
        Assert.Contains("Zeile 2", bad.Message);
    }

    [Fact]
    public void LDF_Frames_Signale_Kodierung_und_Pruefsumme()
    {
        var db = BusDatabase.Parse(Ldf, BusDatabaseFormat.Ldf);
        Assert.Equal(BusDatabaseFormat.Ldf, BusDatabase.DetectFormat("x.txt", Ldf));
        Assert.Equal(19200, db.Bitrate);
        Assert.Equal("BCM", db.MasterNode);
        Assert.Equal("2.1", db.Version);
        var status = db.RequireMessage("FH_Status");
        Assert.Equal((0x21u, 4, "Fensterheber"), (status.Id, status.Length, status.Sender));
        var pos = status.RequireSignal("Position");
        Assert.Equal((0.5, "%", 100.0), (pos.Coding.Factor, pos.Unit, pos.Maximum!.Value));
        Assert.Equal("Auf", status.RequireSignal("Status").Values[1]);
        Assert.Equal(16, status.RequireSignal("Daten").Coding.Length);
        var befehl = db.RequireMessage("FH_Befehl");
        Assert.Equal([0x02, 128], befehl.Encode(new Dictionary<string, double> { ["Befehl"] = 2 }));  // Startwert 128

        Assert.Equal(0x50, Lin.ProtectedId(0x10));
        Assert.Equal(0x3C, Lin.ProtectedId(0x3C));
        Assert.Equal(0x7D, Lin.ProtectedId(0x3D));
        Assert.Equal(0xFC, Lin.Checksum(0x10, [0x01, 0x02], LinChecksum.Classic));
        Assert.Equal(0xAC, Lin.Checksum(0x10, [0x01, 0x02], LinChecksum.Enhanced));
        Assert.Equal(LinChecksum.Classic, Lin.Effective(0x3C, LinChecksum.Enhanced));
        Assert.Throws<BusDatabaseException>(() => BusDatabase.Parse("LIN_description_file; Frames { F: 0x50, M, 2 { } }", BusDatabaseFormat.Ldf));

        // Ereignisgesteuerte und sporadische Frames
        var etf = db.FrameGroup("ETF_Status")!;
        Assert.Equal(LinFrameKind.EventTriggered, etf.Kind);
        Assert.Equal(0x20u, etf.Id);
        Assert.Equal(["FH_Status"], etf.Members);
        Assert.Equal(LinFrameKind.Sporadic, db.FrameGroup("SF_Befehl")!.Kind);
    }

    // ------------------------------------------------------------------ Bausteine gegen die Simulation

    private static TestProject BusProject(GroupDefinition loop)
    {
        var project = new TestProject(Test(loop));
        project.Definition.BusDatabases.Add(new BusDatabaseDefinition("Antrieb", BusDatabaseFormat.Dbc, Dbc, "antrieb.dbc"));
        project.Definition.BusDatabases.Add(new BusDatabaseDefinition("Fenster", BusDatabaseFormat.Ldf, Ldf, "fenster.ldf"));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_CAN", "can", CommKind.Can, """
            zyklisch 0x100 A0 0F 5A 02 59 D8 00 00
            zyklisch 0x200 02 07 00 00 00 00 00 00
            zyklisch 0x200 01 7D 00 00 00 00 00 00
            zyklisch 0x18F00401 FF FF FF 80 3E FF FF FF
            # J1939: Anforderung → Antwort; BAM (VIN); RTS/CTS an F9
            0x18EA00F9 E5 FE 00 => 0x18FEE500 10 27 00 00 FF FF FF FF
            0x18EA00F9 EC FE 00 => 0x1CECFF00 20 0E 00 02 FF EC FE 00; 0x1CEBFF00 01 41 42 43 44 45 46 47; 0x1CEBFF00 02 48 49 4A 4B 4C 4D 4E
            0x18EA00F9 DA FE 00 => 0x1CECF900 10 09 00 02 FF DA FE 00
            0x1CEC00F9 11 02 01 FF FF DA FE 00 => 0x1CEBF900 01 01 02 03 04 05 06 07; 0x1CEBF900 02 08 09 FF FF FF FF FF
            0x18EA00F9 CA FE 00 => 0x18FECA00 FF FF FF FF FF FF FF FF
            0x1CEC00F9 10 0A 00 02 FF 00 EF 00 => 0x1CECF900 11 02 01 FF FF 00 EF 00
            0x1CEB00F9 02 ** ** ** ** ** ** ** => 0x1CECF900 13 0A 00 02 FF 00 EF 00
            # UDS-Steuergerät
            uds 0x7E0 0x7E8
            uds: 22 F1 90 => 62 F1 90 "WVWZZZ1JZXW000001"
            uds: 10 03 => 50 03 00 32 01 F4
            uds: 22 F1 91 => 7F 22 31
            uds: 31 01 FF 00 => 7F 31 78; 71 01 FF 00 00
            uds: 2E F1 98 ** ** ** ** ** ** ** ** ** ** => 6E F1 98
            uds: 22 01 0C => 62 01 0C 1F 40
            uds: 3E 80 => 7E 00
            """));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_LIN", "lin", CommKind.Lin, "0x21 => 50 01 34 12\n0x22 => @0x10"));
        return project;
    }

    private static async Task<(TestRun Run, SimulationAdapter Sim)> RunAsync(TestProject project)
    {
        var (sim, registry) = SampleProjects.CreateSimulation(project);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry });
        await run.RunAsync().Within();
        return (run, sim);
    }

    [Fact]
    public async Task CAN_Signale_nach_DBC_lesen_Multiplex_J1939_und_Botschaft_senden()
    {
        var loop = Group("loop").Var("Modus", VarType.String).Var("N", VarType.Double).Var("Soll", VarType.Double, 750.0).Add(
            S(BusBlocks.SignalRead, "drehzahl", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Motor_1.Drehzahl"),
                ("Untergrenze", "900"), ("Obergrenze", "1100"), ("Ziel", "N")),
            S(BusBlocks.SignalRead, "modus", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Modus"), ("Sollwert", "\"Aktiv\""), ("Ziel", "Modus")),
            S(BusBlocks.SignalRead, "modusFalsch", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Modus"), ("Sollwert", "\"Standby\"")),
            S(BusBlocks.SignalRead, "spannung", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Spannung"), ("Sollwert", "230")),
            S(BusBlocks.SignalRead, "temp", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Temperatur")),
            S(BusBlocks.SignalRead, "strom", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Strom"), ("Untergrenze", "12"), ("Obergrenze", "13")),
            S(BusBlocks.SignalRead, "fehler", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Fehler"), ("Sollwert", "7")),
            S(BusBlocks.SignalRead, "eec1", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "EngineSpeed"), ("Untergrenze", "1999"), ("Obergrenze", "2001")),
            S(BusBlocks.SignalRead, "keineDb", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Fehlt"), ("Signal", "X")),
            S(BusBlocks.SignalRead, "still", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "SollDrehzahl"), ("Zeitlimit", "20")),
            S(BusBlocks.MessageSend, "senden", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Botschaft", "Sollwerte"),
                ("Werte", "SollDrehzahl = Soll * 2; Leistung = 99.5"), ("Anzahl", "2")),
            S(BusBlocks.MessageSend, "zuGross", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Botschaft", "Sollwerte"), ("Werte", "SollDrehzahl = 70000")),
            S(BusBlocks.MessageSend, "tabelle", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Botschaft", "Motor_1"), ("Werte", "Modus = Fehler")));
        var (run, sim) = await RunAsync(BusProject(loop));

        var n = run.Steps("drehzahl").Single();
        Assert.Equal(ResultRating.Pass, n.Rating);
        Assert.Equal("1/min", n.Unit);
        Assert.Equal(1000.0, (double)n.Value!, 1e-9);
        var modus = run.Steps("modus").Single();
        Assert.Equal(ResultRating.Pass, modus.Rating);
        Assert.Equal("Aktiv", modus.Value);
        Assert.Equal(ResultRating.Fail, run.Steps("modusFalsch").Single().Rating);
        Assert.Equal(ResultRating.Pass, run.Steps("spannung").Single().Rating);
        Assert.Equal(50.0, (double)run.Steps("temp").Single().Value!, 1e-9);
        Assert.Equal(ResultRating.NResult, run.Steps("temp").Single().Rating);
        Assert.Equal(ResultRating.Pass, run.Steps("strom").Single().Rating);       // Seite 1 von zwei abwechselnden Rahmen
        Assert.Equal(ResultRating.Pass, run.Steps("fehler").Single().Rating);      // Seite 2
        Assert.Equal(ResultRating.Pass, run.Steps("eec1").Single().Rating);        // PGN-Filter: Quelle 01, Priorität 6
        Assert.Contains("nicht im Projekt", run.Steps("keineDb").Single().Message);
        Assert.Equal(ResultRating.Error, run.Steps("still").Single().Rating);
        Assert.Equal(ResultRating.NResult, run.Steps("senden").Single().Rating);
        Assert.Contains("SollDrehzahl=1500", run.Steps("senden").Single().Message);
        Assert.Equal(ResultRating.Error, run.Steps("zuGross").Single().Rating);
        Assert.Contains("Modus=Fehler", run.Steps("tabelle").Single().Message);

        var dut = (SimulatedCanDut)sim.CommunicationEndpoint("can")!;
        var sent = dut.Sent.Where(f => f.Id == 0x300).ToList();
        Assert.Equal(2, sent.Count);
        var db = BusDatabase.Parse(Dbc, BusDatabaseFormat.Dbc);
        var values = db.RequireMessage("Sollwerte").Decode(sent[0].Data).ToDictionary(d => d.Signal.Name, d => d.Value);
        Assert.Equal((1500.0, 1.0, 99.5), (values["SollDrehzahl"], values["Freigabe"], values["Leistung"]));
    }

    [Fact]
    public async Task UDS_ueber_ISO_TP_Segmentierung_negative_Antwort_Antwort_folgt()
    {
        var loop = Group("loop").Var("Vin", VarType.String).Add(
            S(BusBlocks.UdsRequest, "vin", ("Ressource", "Pruefling_CAN"), ("Anfrage", "22 F1 90"), ("Darstellung", "Text (ASCII)"),
                ("Sollwert", "\"WVWZZZ1JZXW000001\""), ("Ziel", "Vin")),
            S(BusBlocks.UdsRequest, "sitzung", ("Ressource", "Pruefling_CAN"), ("Anfrage", "10 03"), ("Erwartet", "50 03")),
            S(BusBlocks.UdsRequest, "abgelehnt", ("Ressource", "Pruefling_CAN"), ("Anfrage", "22 F1 91")),
            S(BusBlocks.UdsRequest, "erwartetAbgelehnt", ("Ressource", "Pruefling_CAN"), ("Anfrage", "22 F1 91"), ("ErwarteteNrc", "0x31")),
            S(BusBlocks.UdsRequest, "routine", ("Ressource", "Pruefling_CAN"), ("Anfrage", "31 01 FF 00"), ("Sollwert", "\"00\"")),
            S(BusBlocks.UdsRequest, "schreiben", ("Ressource", "Pruefling_CAN"), ("Anfrage", "2E F1 98 31 32 33 34 35 36 37 38 39 {48}")),
            S(BusBlocks.UdsRequest, "zahl", ("Ressource", "Pruefling_CAN"), ("Anfrage", "22 01 0C"), ("Darstellung", "Zahl"), ("Faktor", "0.25"),
                ("Untergrenze", "1900"), ("Obergrenze", "2100"), ("Einheit", "1/min")),
            S(BusBlocks.UdsRequest, "unterdrueckt", ("Ressource", "Pruefling_CAN"), ("Anfrage", "3E 80"), ("Zeitlimit", "20")),
            S(BusBlocks.UdsRequest, "still", ("Ressource", "Pruefling_CAN"), ("Anfrage", "22 FF FF"), ("Zeitlimit", "20")));
        var (run, sim) = await RunAsync(BusProject(loop));

        var vin = run.Steps("vin").Single();
        Assert.Equal(ResultRating.Pass, vin.Rating);
        Assert.Equal("WVWZZZ1JZXW000001", vin.Value);
        Assert.Equal(ResultRating.Pass, run.Steps("sitzung").Single().Rating);
        var refused = run.Steps("abgelehnt").Single();
        Assert.Equal(ResultRating.Fail, refused.Rating);
        Assert.Contains("requestOutOfRange", refused.Message);
        Assert.Equal(ResultRating.Pass, run.Steps("erwartetAbgelehnt").Single().Rating);
        Assert.Equal(ResultRating.Pass, run.Steps("routine").Single().Rating);
        Assert.Equal(ResultRating.Pass, run.Steps("schreiben").Single().Rating);    // 13 Bytes: Erst- und Folgerahmen mit Flusssteuerung
        var number = run.Steps("zahl").Single();
        Assert.Equal((ResultRating.Pass, 2000.0), (number.Rating, (double)number.Value!));
        Assert.Equal(ResultRating.Pass, run.Steps("unterdrueckt").Single().Rating); // Bit 7: keine positive Antwort erwartet
        Assert.Equal(ResultRating.Error, run.Steps("still").Single().Rating);

        var dut = (SimulatedCanDut)sim.CommunicationEndpoint("can")!;
        var write = dut.Sent.Where(f => f.Id == 0x7E0).ToList();
        Assert.Contains(write, f => f.Data[0] == 0x10 && f.Data[1] == 13);       // Erstrahmen
        Assert.Contains(write, f => f.Data[0] == 0x21);                          // Folgerahmen 1
        Assert.Contains(write, f => f.Data[0] == 0x30);                          // eigene Flusssteuerung beim VIN-Empfang
        Assert.All(write, f => Assert.Equal(8, f.Data.Length));                  // aufgefüllt
    }

    [Fact]
    public async Task J1939_Anforderung_BAM_RTS_CTS_nicht_verfuegbar_und_Senden()
    {
        var loop = Group("loop").Var("Vin", VarType.String).Add(
            S(BusBlocks.J1939Receive, "stunden", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xFEE5"), ("Quelle", "0x00"), ("Anfordern", "true"),
                ("Bits", "32"), ("Faktor", "0.05"), ("Untergrenze", "499"), ("Obergrenze", "501"), ("Einheit", "h")),
            S(BusBlocks.J1939Receive, "vin", ("Ressource", "Pruefling_CAN"), ("Pgn", "65260"), ("Quelle", "0x00"), ("Anfordern", "true"), ("Ziel", "Vin"),
                ("Erwartet", "41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E")),
            S(BusBlocks.J1939Receive, "rts", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xFEDA"), ("Quelle", "0"), ("Anfordern", "true")),
            S(BusBlocks.J1939Receive, "nv", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xFECA"), ("Quelle", "0"), ("Anfordern", "true"), ("Bits", "8")),
            S(BusBlocks.J1939Receive, "db", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "EEC1.EngineSpeed"), ("Sollwert", "2000")),
            S(BusBlocks.J1939Receive, "keiner", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xFEF1"), ("Zeitlimit", "20")),
            S(BusBlocks.J1939Send, "bam", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xFEEC"), ("Daten", "01 02 03 04 05 06 07 08 09 0A")),
            S(BusBlocks.J1939Send, "cmdt", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xEF00"), ("Ziel", "0x00"), ("Daten", "01 02 03 04 05 06 07 08 09 0A")),
            S(BusBlocks.J1939Send, "kurz", ("Ressource", "Pruefling_CAN"), ("Pgn", "0xEF00"), ("Ziel", "0x21"), ("Prioritaet", "3"), ("Daten", "AA BB")));
        var (run, sim) = await RunAsync(BusProject(loop));

        var hours = run.Steps("stunden").Single();
        Assert.Equal((ResultRating.Pass, 500.0), (hours.Rating, (double)hours.Value!));
        var vin = run.Steps("vin").Single();
        Assert.Equal(ResultRating.Pass, vin.Rating);
        Assert.Contains("[BAM]", vin.Message);
        var rts = run.Steps("rts").Single();
        Assert.Equal("01 02 03 04 05 06 07 08 09", rts.Value);
        Assert.Contains("[RTS/CTS]", rts.Message);
        var nv = run.Steps("nv").Single();
        Assert.Equal(ResultRating.Fail, nv.Rating);
        Assert.Contains("nicht verfügbar", nv.Message);
        Assert.Equal(ResultRating.Pass, run.Steps("db").Single().Rating);
        Assert.Equal(ResultRating.Error, run.Steps("keiner").Single().Rating);
        Assert.Contains("(BAM)", run.Steps("bam").Single().Message);
        Assert.Contains("(RTS/CTS)", run.Steps("cmdt").Single().Message);

        var dut = (SimulatedCanDut)sim.CommunicationEndpoint("can")!;
        var sent = dut.Sent.ToList();
        Assert.Contains(sent, f => f.Id == 0x1CECFFF9 && f.Data[0] == 0x20 && f.Data[1] == 10 && f.Data[3] == 2);   // BAM-Ankündigung
        Assert.Equal(2, sent.Count(f => f.Id == 0x1CEBFFF9));                                                       // 2 Datenpakete
        Assert.Contains(sent, f => f.Id == 0x1CEC00F9 && f.Data[0] == 0x11);                                         // eigenes CTS
        Assert.Contains(sent, f => f.Id == 0x1CEC00F9 && f.Data[0] == 0x13);                                         // Abschlussbestätigung
        Assert.Equal(2, sent.Count(f => f.Id == 0x1CEB00F9));                                                       // RTS/CTS-Datenpakete
        Assert.Contains(sent, f => f.Id == 0x0CEF21F9 && f.Data.SequenceEqual(new byte[] { 0xAA, 0xBB }));          // PDU1, Priorität 3
        Assert.Equal((0xEF00u, (byte)0xF9, (byte?)0x21, 3), (J1939.PgnOf(0x0CEF21F9), J1939.SourceOf(0x0CEF21F9), J1939.DestinationOf(0x0CEF21F9), J1939.PriorityOf(0x0CEF21F9)));
    }

    [Fact]
    public async Task LIN_lesen_und_senden_mit_LDF_und_ohne()
    {
        var loop = Group("loop").Var("Status", VarType.String).Add(
            S(BusBlocks.LinRead, "pos", ("Ressource", "Pruefling_LIN"), ("Frame", "FH_Status"), ("Datenbank", "Fenster"), ("Signal", "Position"),
                ("Untergrenze", "30"), ("Obergrenze", "50")),
            S(BusBlocks.LinRead, "status", ("Ressource", "Pruefling_LIN"), ("Frame", "FH_Status"), ("Datenbank", "Fenster"), ("Signal", "Status"),
                ("Sollwert", "\"Auf\""), ("Ziel", "Status")),
            S(BusBlocks.LinRead, "roh", ("Ressource", "Pruefling_LIN"), ("Frame", "0x21"), ("Laenge", "4"), ("Startbit", "16"), ("Bits", "16"), ("Sollwert", "4660")),
            S(BusBlocks.LinSend, "befehl", ("Ressource", "Pruefling_LIN"), ("Frame", "FH_Befehl"), ("Datenbank", "Fenster"), ("Werte", "Befehl = Ab; Geschwindigkeit = 200")),
            S(BusBlocks.LinRead, "echo", ("Ressource", "Pruefling_LIN"), ("Frame", "0x22"), ("Laenge", "2"), ("Erwartet", "02 C8")),
            S(BusBlocks.LinRead, "falscheLaenge", ("Ressource", "Pruefling_LIN"), ("Frame", "0x21"), ("Laenge", "2")),
            S(BusBlocks.LinRead, "keiner", ("Ressource", "Pruefling_LIN"), ("Frame", "0x30")),
            S(BusBlocks.LinSend, "hex", ("Ressource", "Pruefling_LIN"), ("Frame", "0x10"), ("Daten", "01 {255}")));
        var (run, sim) = await RunAsync(BusProject(loop));

        var pos = run.Steps("pos").Single();
        Assert.Equal((ResultRating.Pass, 40.0, "%"), (pos.Rating, (double)pos.Value!, pos.Unit));
        Assert.Equal(ResultRating.Pass, run.Steps("status").Single().Rating);
        Assert.Equal("Auf", run.Steps("status").Single().Value);
        Assert.Equal(ResultRating.Pass, run.Steps("roh").Single().Rating);
        Assert.Contains("Befehl=Ab", run.Steps("befehl").Single().Message);
        Assert.Equal(ResultRating.Pass, run.Steps("echo").Single().Rating);
        Assert.Equal(ResultRating.Error, run.Steps("falscheLaenge").Single().Rating);
        Assert.Equal(ResultRating.Error, run.Steps("keiner").Single().Rating);
        var lin = (SimulatedLinDut)sim.CommunicationEndpoint("lin")!;
        Assert.Contains(lin.Sent, f => f.Id == 0x10 && f.Data.SequenceEqual(new byte[] { 0x01, 0xFF }));
    }

    [Fact]
    public void Editor_prueft_Datenbankverweise_Format_Rundreise_und_Vergleich()
    {
        var loop = Group("loop").Add(
            S(BusBlocks.SignalRead, "ok", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Drehzahl")),
            S(BusBlocks.SignalRead, "fehltDb", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Gibtsnicht"), ("Signal", "Drehzahl")),
            S(BusBlocks.SignalRead, "fehltSignal", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Signal", "Motor_1.Gibtsnicht")),
            S(BusBlocks.MessageSend, "fehltWert", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Antrieb"), ("Botschaft", "Sollwerte"), ("Werte", "Falsch = 1")),
            S(BusBlocks.LinRead, "falschesFormat", ("Ressource", "Pruefling_LIN"), ("Frame", "0x21"), ("Datenbank", "Antrieb")));
        var project = BusProject(loop);
        var editor = new EditorViewModel(project);
        StepDefinition Step(string id) => (StepDefinition)project.Definition.Find(id)!;
        Assert.Empty(editor.DiagnosticsFor(Step("ok")));
        Assert.Contains("nicht im Projekt", Assert.Single(editor.DiagnosticsFor(Step("fehltDb"))));
        Assert.Contains("Gibtsnicht", Assert.Single(editor.DiagnosticsFor(Step("fehltSignal"))));
        Assert.Contains("Falsch", Assert.Single(editor.DiagnosticsFor(Step("fehltWert"))));
        Assert.Contains("erwartet LDF", Assert.Single(editor.DiagnosticsFor(Step("falschesFormat"))));
        Assert.NotNull(BlockCatalog.Default.Rebuild(S(BusBlocks.MessageSend, "x", ("Ressource", "R"), ("Datenbank", "A"), ("Botschaft", "B"), ("Werte", "nurText"))).FirstOrDefault());

        var serializer = new ProjectSerializer();
        var json = serializer.Serialize(project);
        Assert.Contains("busDatabases", json);
        var back = serializer.Deserialize(json);
        Assert.Equal(["Antrieb", "Fenster"], back.Definition.BusDatabases.Select(d => d.Name));
        Assert.Equal(BusDatabaseFormat.Ldf, back.Definition.FindBusDatabase("fenster")!.Format);
        Assert.Equal(4, back.Definition.FindBusDatabase("Antrieb")!.Parsed.Messages.Count);
        Assert.Contains(back.SimulationComm, c => c.Kind == CommKind.Lin);

        var changed = serializer.Clone(project);
        changed.Definition.BusDatabases[0] = new BusDatabaseDefinition("Antrieb", BusDatabaseFormat.Dbc, Dbc.Replace("BO_ 768 Sollwerte: 8 Tester", "BO_ 769 Sollwerte: 8 Tester"));
        changed.Definition.BusDatabases.RemoveAt(1);
        var diff = ProjectDiff.Compare(project, changed);
        Assert.Contains(diff, d => d.Area == "Busdatenbank" && d.Item == "Antrieb" && d.Kind == DiffKind.Changed);
        Assert.Contains(diff, d => d.Area == "Busdatenbank" && d.Item == "Fenster" && d.Kind == DiffKind.Removed);
    }

    [Fact]
    public void Import_in_Projekteigenschaften_ersetzt_und_benennt_um()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tu-bus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var dbc = Path.Combine(folder, "Motor CAN.dbc");
            File.WriteAllText(dbc, Dbc, System.Text.Encoding.Latin1);           // ältere DBC: Windows-1252 (°C)
            var ldf = Path.Combine(folder, "fenster.ldf");
            File.WriteAllText(ldf, Ldf);
            var broken = Path.Combine(folder, "kaputt.dbc");
            File.WriteAllText(broken, "keine Datenbank");

            var loop = Group("loop").Add(S(BusBlocks.SignalRead, "sig", ("Ressource", "Pruefling_CAN"), ("Datenbank", "Motor_CAN"), ("Signal", "Temperatur")));
            var editor = new EditorViewModel(new TestProject(Test(loop)));
            var props = new ProjectPropertiesViewModel(editor);
            var row = props.ImportBusDatabase(dbc)!;
            Assert.Equal("Motor_CAN", row.Name);
            Assert.Contains("4 Botschaft", row.Summary);
            Assert.Contains(row.Overview, l => l.StartsWith("Motor_1 (0x100, 8 Bytes, 100 ms)") && l.Contains("Temperatur [°C]"));
            Assert.Contains(row.Overview, l => l.Contains("PGN 0xF004"));
            Assert.Empty(editor.DiagnosticsFor((StepDefinition)editor.Project.Definition.Find("sig")!));
            Assert.True(editor.IsDirty);

            Assert.Equal("LDF (LIN)", props.ImportBusDatabase(ldf)!.Format);
            Assert.Null(props.ImportBusDatabase(broken));
            Assert.Contains("abgelehnt", props.BusStatus);
            Assert.Equal(2, props.BusDatabases.Count);

            props.ImportBusDatabase(dbc);                                    // gleicher Name: ersetzen
            Assert.Equal(2, editor.Project.Definition.BusDatabases.Count);
            Assert.Contains("aktualisiert", props.BusStatus);

            props.BusDatabases[0].Name = "Antrieb";                          // umbenennen zieht Verweise mit
            Assert.Equal("Antrieb", ((StepDefinition)editor.Project.Definition.Find("sig")!).Parameters["Datenbank"]);
            props.BusDatabases[0].Name = "fenster";                          // belegt (Groß-/Kleinschreibung egal)
            Assert.Equal("Antrieb", props.BusDatabases[0].Name);

            props.SelectedBus = props.BusDatabases[1];
            props.RemoveBusCommand.Execute(null);
            Assert.Single(editor.Project.Definition.BusDatabases);
            editor.Undo();
            Assert.Equal(2, editor.Project.Definition.BusDatabases.Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // ------------------------------------------------------------------ Treiber ohne Hardware

    [Fact]
    public async Task ISO_TP_UDS_und_J1939_zwischen_zwei_Geraeten_am_virtuellen_Bus()
    {
        var bus = $"iso-{Guid.NewGuid():N}";
        var options = new InstrumentOptions(new Dictionary<string, string> { ["Bitrate"] = "500k" });
        using var tester = new CanDevice(CanDevice.VirtualAdapter, bus, options);
        using var ecu = new CanDevice(CanDevice.VirtualAdapter, bus, options);
        await tester.ProbeAsync(CancellationToken.None);
        await ecu.ProbeAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Steuergerät: beantwortet 22 F1 90 mit 30 Bytes (segmentiert) nach einmal „Antwort folgt“, Blockgröße 2 bei eigener Flusssteuerung
        var ecuSide = new IsoTpSettings(0x7E8, 0x7E0);
        var server = Task.Run(async () =>
        {
            var request = await IsoTp.ReceiveAsync(ecu, "Bus", ecuSide, TimeSpan.FromSeconds(10), cts.Token);
            await IsoTp.SendAsync(ecu, "Bus", ecuSide, [0x7F, request[0], 0x78], cts.Token);
            await IsoTp.SendAsync(ecu, "Bus", ecuSide, [(byte)(request[0] + 0x40), .. request[1..], .. Enumerable.Range(1, 27).Select(i => (byte)i)], cts.Token);
        });
        var response = await Uds.RequestAsync(tester, "Bus", new IsoTpSettings(0x7E0, 0x7E8, BlockSize: 2), [0x22, 0xF1, 0x90],
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), cts.Token);
        await server.WaitAsync(cts.Token);
        Assert.Equal(30, response.Length);
        Assert.Equal([0x62, 0xF1, 0x90, 1, 2], response[..5]);
        Assert.Equal(27, response[^1]);

        // Steuergerät sendet segmentiert mit Blockgröße 2 (Flusssteuerung des Testers): Anfrage über 20 Bytes
        var big = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        var receive = IsoTp.ReceiveAsync(ecu, "Bus", ecuSide, TimeSpan.FromSeconds(10), cts.Token);
        await IsoTp.SendAsync(tester, "Bus", new IsoTpSettings(0x7E0, 0x7E8), big, cts.Token);
        Assert.Equal(big, await receive);

        // J1939 BAM und RTS/CTS zwischen echten Treiberinstanzen
        var data = Enumerable.Range(0, 20).Select(i => (byte)(0xA0 + i)).ToArray();
        tester.DiscardInput("Bus");
        var bam = J1939.ReceiveAsync(tester, "Bus", 0xFEEC, 0x00, 0xF9, TimeSpan.FromSeconds(5), cts.Token);
        await J1939.SendAsync(ecu, "Bus", 0xFEEC, data, 0x00, J1939.Global, 6, cts.Token, TimeSpan.FromMilliseconds(2));
        var got = await bam;
        Assert.Equal(data, got.Data);
        Assert.Equal("BAM", got.Transport);

        tester.DiscardInput("Bus");
        var direct = J1939.ReceiveAsync(tester, "Bus", 0xEF00, 0x00, 0xF9, TimeSpan.FromSeconds(5), cts.Token);
        await J1939.SendAsync(ecu, "Bus", 0xEF00, data, 0x00, 0xF9, 6, cts.Token);
        got = await direct;
        Assert.Equal(data, got.Data);
        Assert.Equal(("RTS/CTS", (byte?)0xF9), (got.Transport, got.Destination));
    }

    [Fact]
    public async Task LIN_Treiber_virtuell_und_Katalog_mit_Vector_und_PLIN()
    {
        using var lin = (LinDevice)InstrumentCatalog.Create(LinDevice.VirtualAdapter, "", "LIN1",
            new Dictionary<string, string> { ["Antworten"] = "0x21=01 02 03 04;0x22=@0x10", ["Baud"] = "19200" });
        Assert.True((await lin.ProbeAsync(CancellationToken.None)).Ok);
        Assert.Equal([1, 2, 3, 4], await lin.RequestAsync("Bus", 0x21, 4, LinChecksum.Enhanced, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        await lin.SendAsync("Bus", 0x10, [0x55, 0xAA], LinChecksum.Enhanced, CancellationToken.None);
        Assert.Equal([0x55, 0xAA], await lin.RequestAsync("Bus", 0x22, 2, LinChecksum.Enhanced, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() => lin.RequestAsync("Bus", 0x30, 2, LinChecksum.Enhanced, TimeSpan.FromMilliseconds(10), CancellationToken.None));
        await Assert.ThrowsAsync<CommunicationException>(() => lin.RequestAsync("Bus", 0x40, 2, LinChecksum.Enhanced, TimeSpan.FromMilliseconds(10), CancellationToken.None));
        Assert.Contains(lin.Log, l => l.Contains("Header 0x21"));

        Assert.True(InstrumentCatalog.IsCommunication(CanDevice.VectorAdapter));
        Assert.True(InstrumentCatalog.IsCommunication(LinDevice.PlinAdapter));
        Assert.NotNull(InstrumentCatalog.Family(CanDevice.VectorAdapter));
        Assert.Equal(("Testumgebung", 2u, false), VectorXlBackend.Parse("2"));
        Assert.Equal(("CANoe", 1u, false), VectorXlBackend.Parse("CANoe:1"));
        Assert.True(VectorXlBackend.Parse("virtuell:0").Virtual);
        Assert.Throws<InstrumentException>(() => VectorXlBackend.Parse("x:y"));
        Assert.Throws<InstrumentException>(() => InstrumentCatalog.Create(LinDevice.PlinAdapter, "", "null"));
        if (!OperatingSystem.IsWindows())
        {
            // ohne Treiber: verständliche Meldung statt Absturz
            using var vector = (CanDevice)InstrumentCatalog.Create(CanDevice.VectorAdapter, "", "Testumgebung:0");
            var probe = await vector.ProbeAsync(CancellationToken.None);
            Assert.False(probe.Ok);
            Assert.Contains(probe.Details, d => d.Contains("Treiber nicht installiert"));
            using var plin = (LinDevice)InstrumentCatalog.Create(LinDevice.PlinAdapter, "", "1");
            Assert.False((await plin.ProbeAsync(CancellationToken.None)).Ok);
        }
    }
    // ------------------------------------------------------------------ CodeBlocks über die zentrale Vermittlung

    private static TestProject CodeProject(StepDefinition code)
    {
        var project = BusProject(Group("loop").Add(code));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_Seriell", "seriell", CommKind.Stream, "\\*IDN\\? => ACME,DUT-1,SN123,1.0"));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_Modbus", "1", CommKind.Modbus, "IR10:float32=230.5\nHR20=0"));
        return project;
    }

    private static async Task<TestRun> RunCodeAsync(string language, string source, Testumgebung.Workers.CodeRuntime runtime)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Code, "code", "code", new Dictionary<string, string>
        {
            ["Sprache"] = language, ["Quelltext"] = source, ["Timeout"] = "30",
        });
        var project = CodeProject(step);
        var run = new TestRun(project.Definition, new TestRunOptions
        {
            Clock = new VirtualClock(), CodeRuntime = runtime, Hardware = SampleProjects.CreateSimulation(project).Registry,
        });
        await run.RunAsync().WaitAsync(TimeSpan.FromMinutes(3));
        return run;
    }

    [Fact]
    public async Task Python_CodeBlock_kommuniziert_ueber_die_Vermittlung()
    {
        const string source = """
            idn = comm_query("Pruefling_Seriell", "*IDN?")
            v = modbus_read("Pruefling_Modbus", "IR10", "Float32")
            modbus_write("Pruefling_Modbus", "HR20", 77)
            w = modbus_read("Pruefling_Modbus", "HR20")
            r = uds("Pruefling_CAN", "22 F1 90")
            f = can_receive("Pruefling_CAN", 0x100)
            lin = lin_read("Pruefling_LIN", 0x21, 4)
            lin_write("Pruefling_LIN", 0x10, bytes([1, 2]))
            echo = lin_read("Pruefling_LIN", 0x22, 2)
            j = j1939_receive("Pruefling_CAN", 0xFEE5, source=0, request=True)
            try:
                uds("Pruefling_CAN", bytes([0x22, 0xF1, 0x91]))
                neg = "keine"
            except Exception as e:
                neg = str(e)
            ok = (idn.startswith("ACME") and abs(v - 230.5) < 1e-6 and w == 77 and r[3:].decode() == "WVWZZZ1JZXW000001"
                  and f["bytes"][0] == 0xA0 and f["id"] == 0x100 and lin[0] == 0x50 and echo == bytes([1, 2]) and j["bytes"][:2] == bytes([0x10, 0x27])
                  and "NRC 0x31" in neg)
            return Pass(idn) if ok else Fail(f"{idn} {v} {w} {r} {f} {lin} {echo} {j} {neg}")
            """;
        var run = await RunCodeAsync("Python", source, CodeBlockTests.SharedRuntime.Value);
        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
        Assert.Equal("ACME,DUT-1,SN123,1.0", step.Value);
    }

    [Fact]
    public async Task CSharp_CodeBlock_kommuniziert_ueber_die_Vermittlung()
    {
        const string source = """
            var idn = CommQuery("Pruefling_Seriell", "*IDN?");
            var v = ModbusRead("Pruefling_Modbus", "IR10", "Float32");
            var r = Uds("Pruefling_CAN", new byte[] { 0x22, 0xF1, 0x90 });
            var f = CanReceive("Pruefling_CAN", 0x100);
            var lin = LinRead("Pruefling_LIN", 0x21, 4);
            var j = J1939Receive("Pruefling_CAN", 0xFEE5, source: 0, request: true);
            string neg;
            try { Uds("Pruefling_CAN", new byte[] { 0x22, 0xF1, 0x91 }); neg = "keine"; } catch (Exception e) { neg = e.Message; }
            var ok = idn.StartsWith("ACME") && Math.Abs(v - 230.5) < 1e-6 && System.Text.Encoding.ASCII.GetString(r, 3, r.Length - 3) == "WVWZZZ1JZXW000001"
                && f.Data[0] == 0xA0 && f.Id == 0x100 && lin[0] == 0x50 && j.Pgn == 0xFEE5 && j.Data[0] == 0x10 && neg.Contains("NRC 0x31");
            return ok ? Pass(idn) : Fail($"{idn} {v} {Convert.ToHexString(r)} {f.Id} {neg}");
            """;
        var run = await RunCodeAsync("C#", source, CodeBlockTests.SharedRuntime.Value);
        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
    }

    [Fact]
    public async Task Cpp_CodeBlock_kommuniziert_ueber_die_Vermittlung()
    {
        var runtime = CodeBlockTests.SharedRuntime.Value;
        if ((await runtime.GetRuntimeInfoAsync(Testumgebung.Domain.Code.CodeLanguage.Cpp, CancellationToken.None)).Contains("Keine C++-Toolchain", StringComparison.Ordinal))
            return;
        const string source = """
            std::string idn = CommQuery("Pruefling_Seriell", "*IDN?");
            double v = ModbusRead("Pruefling_Modbus", "IR10", "Float32");
            std::string r = Uds("Pruefling_CAN", "22F190");
            std::string f = CanReceive("Pruefling_CAN", 0x100);
            std::string lin = LinRead("Pruefling_LIN", 0x21, 4);
            std::string neg = "keine";
            try { Uds("Pruefling_CAN", "22 F1 91"); } catch (const tu::HostError& e) { neg = e.what(); }
            bool ok = idn.rfind("ACME", 0) == 0 && std::fabs(v - 230.5) < 1e-6 && r.substr(0, 6) == "62F190" && f.substr(0, 4) == "A00F"
                && lin == "50013412" && neg.find("NRC 0x31") != std::string::npos;
            return ok ? Pass() : Fail(idn + " | " + r + " | " + f + " | " + lin + " | " + neg);
            """;
        var run = await RunCodeAsync("C++", source, runtime);
        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
    }

    // ------------------------------------------------------------------ Offene Punkte: DoIP, erweiterte Adressierung, J1939-Adresse, LIN-Fahrplan/-Diagnose

    [Fact]
    public async Task UDS_ueber_DoIP_Routing_Quittung_und_Diagnose()
    {
        // Simuliertes DoIP-Steuergerät als Byte-Strom-Gegenstelle, direkt an das Diagnoseprotokoll angebunden.
        var ecu = new Testumgebung.Hardware.SimulatedDoIpDut("""
            doip 0x1000
            uds: 10 03 => 50 03 00 32 01 F4
            uds: 22 F1 90 => 62 F1 90 "WVWZZZ1JZXW000001"
            uds: 22 F1 91 => 7F 22 31
            """);
        var transport = new DoIpTransport(ecu, "tcp", new DoIpSettings(0x0E00, 0x1000));

        var session = await Uds.RequestAsync(transport, [0x10, 0x03], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), default).Within();
        Assert.Equal([0x50, 0x03], session[..2]);
        var vin = await Uds.RequestAsync(transport, [0x22, 0xF1, 0x90], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), default).Within();
        Assert.Equal("WVWZZZ1JZXW000001", System.Text.Encoding.ASCII.GetString(vin, 3, vin.Length - 3));
        Assert.True(ecu.Requests.Count >= 2);
        var ex = await Assert.ThrowsAsync<UdsNegativeResponseException>(() => Uds.RequestAsync(transport, [0x22, 0xF1, 0x91], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), default));
        Assert.Equal(0x31, ex.Code);

        // Routing abgelehnt → sprechende Meldung
        var refused = new DoIpTransport(new Testumgebung.Hardware.SimulatedDoIpDut("doip 0x1000\nrouting=abgelehnt\nuds: 10 03 => 50 03"), "tcp", new DoIpSettings());
        var err = await Assert.ThrowsAsync<CommunicationException>(() => Uds.RequestAsync(refused, [0x10, 0x03], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), default));
        Assert.Contains("Routing abgelehnt", err.Message);
    }

    [Fact]
    public async Task UDS_Block_ueber_DoIP_gegen_simuliertes_Steuergeraet()
    {
        var project = new TestProject(Test(Group("loop").Var("Vin", VarType.String).Add(
            S(BusBlocks.UdsRequest, "vin", ("Ressource", "Pruefling_DoIP"), ("Transport", "DoIP (Ethernet)"),
                ("SteuergeraetAdresse", "0x1000"), ("Anfrage", "22 F1 90"), ("Darstellung", "Text (ASCII)"), ("Ziel", "Vin")))));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_DoIP", "tcp", CommKind.DoIp, "doip 0x1000\nuds: 22 F1 90 => 62 F1 90 \"ABC123\""));
        var (run, _) = await RunAsync(project);
        var vin = run.Steps("vin").Single();
        Assert.Equal(ResultRating.Pass, vin.Rating);
        Assert.Equal("ABC123", vin.Value);
    }

    [Fact]
    public async Task UDS_mit_erweiterter_Adressierung()
    {
        // Steuergerät erwartet Adressbyte 0x10 in Anfragen, antwortet mit 0xF1.
        var dut = new SimulatedCanDut("uds 0x7E0 0x7E8 tx=0x10 rx=0xF1\nuds: 22 F1 90 => 62 F1 90 AB CD");
        var settings = new IsoTpSettings(0x7E0, 0x7E8) { TxAddress = 0x10, RxAddress = 0xF1 };
        var response = await Uds.RequestAsync(dut, "can", settings, [0x22, 0xF1, 0x90], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), default).Within();
        Assert.Equal([0x62, 0xF1, 0x90, 0xAB, 0xCD], response);
        Assert.Contains(dut.Sent, f => f.Data[0] == 0x10 && f.Data[1] == 0x03);   // Adressbyte + Einzelrahmen-PCI

        // Ohne passendes Adressbyte schweigt das Steuergerät
        await Assert.ThrowsAsync<CommunicationTimeoutException>(() =>
            Uds.RequestAsync(dut, "can", new IsoTpSettings(0x7E0, 0x7E8) { TxAddress = 0x11, RxAddress = 0xF1 }, [0x22, 0xF1, 0x90],
                TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), default));
    }

    [Fact]
    public async Task ISO_TP_lange_Nachricht_mit_erweiterter_Adressierung_am_virtuellen_Bus()
    {
        var bus = $"ea-{Guid.NewGuid():N}";
        var options = new InstrumentOptions(new Dictionary<string, string> { ["Bitrate"] = "500k" });
        using var tester = new CanDevice(CanDevice.VirtualAdapter, bus, options);
        using var ecu = new CanDevice(CanDevice.VirtualAdapter, bus, options);
        await tester.ProbeAsync(default);
        await ecu.ProbeAsync(default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var ecuSide = new IsoTpSettings(0x7E8, 0x7E0) { TxAddress = 0xF1, RxAddress = 0x10 };
        var testerSide = new IsoTpSettings(0x7E0, 0x7E8) { TxAddress = 0x10, RxAddress = 0xF1 };
        var payload = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();
        var receive = IsoTp.ReceiveAsync(ecu, "Bus", ecuSide, TimeSpan.FromSeconds(10), cts.Token);
        await IsoTp.SendAsync(tester, "Bus", testerSide, payload, cts.Token);
        Assert.Equal(payload, await receive);
    }

    [Fact]
    public async Task J1939_Adresse_beanspruchen_gewinnen_und_verlieren()
    {
        var bus = $"claim-{Guid.NewGuid():N}";
        var options = new InstrumentOptions(new Dictionary<string, string> { ["Bitrate"] = "250k" });
        using var self = new CanDevice(CanDevice.VirtualAdapter, bus, options);
        using var other = new CanDevice(CanDevice.VirtualAdapter, bus, options);
        await self.ProbeAsync(default);
        await other.ProbeAsync(default);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Niemand widerspricht → Anspruch gelingt
        await J1939.ClaimAddressAsync(self, "Bus", 0x80, 0x00000000000000FF, cts.Token, TimeSpan.FromMilliseconds(100)).Within();

        // Gegenstelle mit kleinerem NAME verteidigt die Adresse → eigener Anspruch scheitert
        var defender = Task.Run(async () =>
        {
            var f = await other.ReceiveAsync("Bus", J1939.Filter(J1939.AddressClaimPgn, null), TimeSpan.FromSeconds(5), cts.Token);
            _ = f;
            await other.SendAsync("Bus", new CanFrame(J1939.Id(J1939.AddressClaimPgn, 0x81, J1939.Global, 6), J1939.NameBytes(0x0000000000000001), true), cts.Token);
        });
        var cannotClaim = other.ReceiveAsync("Bus", J1939.Filter(J1939.AddressClaimPgn, J1939.NullAddress), TimeSpan.FromSeconds(5), cts.Token);
        var ex = await Assert.ThrowsAsync<CommunicationException>(() => J1939.ClaimAddressAsync(self, "Bus", 0x81, 0x00000000000000FF, cts.Token, TimeSpan.FromSeconds(2)));
        await defender.WaitAsync(cts.Token);
        Assert.Contains("höherer Priorität", ex.Message);
        Assert.Equal(J1939.NullAddress, J1939.SourceOf((await cannotClaim.Within()).Id));   // „Cannot Claim“ gesendet
    }

    /// <summary>LIN-Gegenstelle für die Diagnose: antwortet auf 0x3D mit der zuletzt über 0x3C angefragten UDS-Antwort (Einzelrahmen).</summary>
    private sealed class LinDiagnosticStub : ILinAdapter
    {
        private readonly Dictionary<string, byte[]> _rules = new(StringComparer.Ordinal);
        private byte[]? _pending;

        public void Respond(byte[] request, byte[] responseMessage)
            => _rules[Convert.ToHexString(request)] = responseMessage;

        public Task SendAsync(string channel, byte frameId, byte[] data, LinChecksum checksum, CancellationToken cancellationToken)
        {
            if (frameId == LinDiagnosticTransport.MasterRequestFrame && data.Length >= 2)
            {
                var length = data[1] & 0x0F;
                var request = data.Skip(2).Take(length).ToArray();
                if (_rules.TryGetValue(Convert.ToHexString(request), out var response))
                    _pending = [data[0], (byte)response.Length, .. response];
            }
            return Task.CompletedTask;
        }

        public Task<byte[]> RequestAsync(string channel, byte frameId, int length, LinChecksum checksum, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (frameId == LinDiagnosticTransport.SlaveResponseFrame && _pending is { } p)
            {
                _pending = null;
                var frame = new byte[8];
                Array.Fill(frame, (byte)0xFF);
                p.CopyTo(frame, 0);
                return Task.FromResult(frame);
            }
            throw new CommunicationTimeoutException("keine Antwort", []);
        }
    }

    [Fact]
    public async Task LIN_Fahrplan_laeuft_und_meldet_fehlende_Antworten()
    {
        var project = new TestProject(Test(Group("loop").Add(
            S(BusBlocks.LinSchedule, "plan", ("Ressource", "Pruefling_LIN"), ("Tabelle", "0x21:4 2; 0x10=01 C8 2; 0x30 2"), ("Durchlaeufe", "2"), ("FehlendFail", "false")),
            S(BusBlocks.LinSchedule, "planFail", ("Ressource", "Pruefling_LIN"), ("Tabelle", "0x30 2"), ("FehlendFail", "true")))));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_LIN", "lin", CommKind.Lin, "0x21 => 50 01 34 12"));
        var (run, _) = await RunAsync(project);
        var plan = run.Steps("plan").Single();
        Assert.Contains("Frames", plan.Message ?? "");
        Assert.Equal(ResultRating.NResult, plan.Rating);     // 0x30 ohne Antwort, FehlendFail=false
        Assert.Equal(ResultRating.Fail, run.Steps("planFail").Single().Rating);
    }

    [Fact]
    public async Task UDS_ueber_LIN_Transportprotokoll()
    {
        var stub = new LinDiagnosticStub();
        stub.Respond([0x22, 0xF1, 0x87], [0x62, 0xF1, 0x87, 0x2A]);          // positive Antwort (4 Bytes, Einzelrahmen)
        var transport = new LinDiagnosticTransport(stub, "lin", 0x01, TimeSpan.FromMilliseconds(1));
        var response = await Uds.RequestAsync(transport, [0x22, 0xF1, 0x87], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), default).Within();
        Assert.Equal([0x62, 0xF1, 0x87, 0x2A], response);
    }

    [Fact]
    public async Task UDS_Sicherheitszugang_Seed_Key_erfolgreich_und_falsch()
    {
        var project = new TestProject(Test(Group("loop").Var("Key", VarType.String).Add(
            S(BusBlocks.UdsSecurity, "frei", ("Ressource", "Pruefling_CAN"), ("Stufe", "0x01"), ("Verfahren", "Seed XOR Konstante"), ("Konstante", "5A"), ("Ziel", "Key")),
            S(BusBlocks.UdsSecurity, "add", ("Ressource", "Pruefling_CAN"), ("Stufe", "0x03"), ("Verfahren", "Seed + Konstante"), ("Konstante", "10")),
            S(BusBlocks.UdsSecurity, "ausdruck", ("Ressource", "Pruefling_CAN"), ("Stufe", "0x05"), ("Verfahren", "Ausdruck je Byte"), ("Ausdruck", "s + 1")),
            S(BusBlocks.UdsSecurity, "falsch", ("Ressource", "Pruefling_CAN"), ("Stufe", "0x07"), ("Verfahren", "Seed XOR Konstante"), ("Konstante", "FF")))));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_CAN", "can", CommKind.Can, """
            uds 0x7E0 0x7E8
            sicherheit 01 seed=11 22 33 44 xor=5A
            sicherheit 03 seed=01 02 03 04 add=10
            sicherheit 05 seed=40 41 42 43 add=01
            sicherheit 07 seed=AA BB CC DD xor=12
            """));
        // "ausdruck" (s+1) muss zum Simulationsschlüssel passen → eigene Stufe mit passendem Verfahren
        var (run, _) = await RunAsync(project);
        var frei = run.Steps("frei").Single();
        Assert.Equal(ResultRating.NResult, frei.Rating);
        Assert.Equal("4B78691E", frei.Value);            // 11^5A etc.
        Assert.Equal(ResultRating.NResult, run.Steps("add").Single().Rating);
        Assert.Equal(ResultRating.NResult, run.Steps("ausdruck").Single().Rating);   // s+1 entspricht add=01
        var falsch = run.Steps("falsch").Single();
        Assert.Equal(ResultRating.Fail, falsch.Rating);  // Schlüssel passt nicht (invalidKey 0x35)
        Assert.Contains("invalidKey", falsch.Message);
    }


    [Fact]
    public async Task LIN_Fahrplan_loest_ereignisgesteuerte_Frames_auf()
    {
        var project = new TestProject(Test(Group("loop").Add(
            S(BusBlocks.LinSchedule, "plan", ("Ressource", "Pruefling_LIN"), ("Datenbank", "Fenster"), ("Tabelle", "ETF_Status 2"), ("FehlendFail", "false")))));
        project.Definition.BusDatabases.Add(new BusDatabaseDefinition("Fenster", BusDatabaseFormat.Ldf, Ldf, "fenster.ldf"));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_LIN", "lin", CommKind.Lin, "0x21 => 50 01 34 12"));
        var (run, _) = await RunAsync(project);
        // ETF_Status enthält FH_Status (0x21) → ein Frame im Plan, Slave antwortet
        Assert.Contains("1 Frames", run.Steps("plan").Single().Message ?? "");
    }

}

