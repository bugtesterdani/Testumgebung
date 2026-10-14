/**
 * @file
 * @brief Verbindet beschreibungsbasierte Bus- und Diagnosebausteine mit der reservierten Gerätevermittlung.
 */

using System.Globalization;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

/// <summary>
/// Bausteine für Feldbusse mit Beschreibung (DBC/LDF) und Diagnose: CAN-Signale nach DBC, UDS über ISO-TP, J1939 mit
/// Transportprotokoll, LIN. Zugriff wie alle Kommunikationsbausteine über <see cref="IStepContext.UseDeviceAsync{T}"/>.
/// </summary>
public static class BusBlocks
{
    public const string SignalRead = "can.signal.lesen";
    public const string MessageSend = "can.botschaft.senden";
    public const string UdsRequest = "uds.anfrage";
    public const string UdsSecurity = "uds.sicherheit";
    public const string J1939Claim = "j1939.adresse";
    public const string LinSchedule = "lin.fahrplan";
    public const string DoIpDiscover = "doip.suche";
    public const string OpcUaAccess = "opcua.zugriff";
    public const string LinDiagnostic = "lin.diagnose";
    public const string J1939Receive = "j1939.empfangen";
    public const string J1939Send = "j1939.senden";
    public const string LinRead = "lin.lesen";
    public const string LinSend = "lin.senden";

    public static readonly string[] UdsFormats = ["Hex", "Text (ASCII)", "Zahl", "Zahl mit Vorzeichen"];
    public static readonly string[] LinChecksums = ["Erweitert (LIN 2.x)", "Klassisch (LIN 1.x)"];
    public static readonly string[] DiagTransports = ["CAN (ISO-TP)", "DoIP (Ethernet)", "LIN (Transportprotokoll)"];
    public static readonly string[] SecurityMethods = ["Seed XOR Konstante", "Seed + Konstante", "Seed invertieren", "Add-Rotate-XOR", "Ausdruck je Byte"];
    public static readonly string[] OpcActions = ["Lesen", "Schreiben", "Methode aufrufen", "Abonnieren (auf Änderung warten)"];

    /// <summary>Bausteintypen, die eine Busdatenbank verwenden können (Editor-Diagnose).</summary>
    public static readonly string[] DatabaseBlocks = [SignalRead, MessageSend, J1939Receive, J1939Send, LinRead, LinSend];

    private static ParameterDescriptor Res(string example) => new("Ressource", "Ressource", ParameterKind.Resource, example, Required: true);

    private static ParameterDescriptor Database(bool required) => new("Datenbank", "Busdatenbank", ParameterKind.Text, Required: required,
        Hint: "Name einer im Projekt importierten DBC-/LDF-Datei (Projekteigenschaften → Busdatenbanken)");

    private static IEnumerable<ParameterDescriptor> Evaluation(bool expected = true) =>
    [
        .. expected ? [new ParameterDescriptor("Erwartet", "Erwartete Daten", ParameterKind.Text, Hint: "Hex, ** = beliebig, z. B. 62 F1 90 ** **")] : Array.Empty<ParameterDescriptor>(),
        new("Sollwert", "Sollwert", ParameterKind.Expression, Hint: "Gleichheit: Zahl oder Text der Wertetabelle (z. B. \"Aktiv\"); sonst Grenzwerte"),
        new("Ziel", "Zielvariable", ParameterKind.Variable),
        new("Untergrenze", "Untergrenze", ParameterKind.Expression),
        new("Obergrenze", "Obergrenze", ParameterKind.Expression),
        new("Einheit", "Einheit", ParameterKind.Text, Hint: "leer = Einheit aus der Datenbank"),
        new("BeiZeitlimit", "Bei Zeitüberschreitung", ParameterKind.Choice, "ERROR", Choices: CommunicationBlocks.TimeoutRatings),
    ];

    private static IEnumerable<ParameterDescriptor> RawSignal() =>
    [
        new("Startbit", "Signal: Startbit", ParameterKind.Number, Hint: "ohne Datenbank; J1939: Byte 4 Bit 1 = Startbit 24"),
        new("Bits", "Signal: Länge [Bit]", ParameterKind.Number),
        new("Byteorder", "Signal: Byteorder", ParameterKind.Choice, "Intel", Choices: CommunicationBlocks.ByteOrders),
        new("Vorzeichen", "Signal: vorzeichenbehaftet", ParameterKind.Bool, "false"),
        new("Faktor", "Signal: Faktor", ParameterKind.Number, "1"),
        new("Offset", "Signal: Offset", ParameterKind.Number, "0"),
    ];

    public static void Register(BlockCatalog c)
    {
        var cat = CommunicationBlocks.Category;

        c.Register(new(SignalRead, "CAN-Signal lesen (DBC)", cat, "measure",
            "Wartet auf die Botschaft des Signals (frisch nach Verwerfen alter Rahmen), dekodiert es nach der Datenbank (Faktor, Offset, Byteorder, "
            + "Multiplex, Wertetabelle) und bewertet gegen Sollwert oder Grenzwerte.",
            [
                Res("Pruefling_CAN"),
                Database(true),
                new("Signal", "Signal", ParameterKind.Text, Required: true, Hint: "Botschaft.Signal oder eindeutiger Signalname, z. B. EEC1.EngineSpeed"),
                new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, Hint: "leer = 3 × Zykluszeit (mindestens 100 ms) bzw. 1000 ms"),
                .. Evaluation(expected: false),
            ],
            p => new SignalReadAction(p.Text("Ressource"), p.Text("Datenbank"), p.Text("Signal"), p.Number("Zeitlimit"), SignalCheck.From(p, withBytes: false))));

        c.Register(new(MessageSend, "CAN-Botschaft senden (DBC)", cat, "arrow",
            "Setzt die Botschaft aus Signalwerten zusammen (nicht genannte Signale mit Startwert, Multiplexer automatisch) und sendet sie – "
            + "optional mehrfach im Zyklus. J1939-Botschaften über 8 Bytes laufen über das Transportprotokoll.",
            [
                Res("Pruefling_CAN"),
                Database(true),
                new("Botschaft", "Botschaft", ParameterKind.Text, Required: true, Hint: "Name oder ID laut Datenbank"),
                new("Werte", "Signalwerte", ParameterKind.Text, Hint: "Signal = Ausdruck; …, z. B. Drehzahl = Solldrehzahl * 2; Modus = Aktiv (Wertetabelle)"),
                new("Anzahl", "Anzahl", ParameterKind.Number, "1"),
                new("Abstand", "Abstand [ms]", ParameterKind.Number, Hint: "leer = Zykluszeit der Botschaft bzw. 100 ms"),
                new("Quelladresse", "J1939: Quelladresse", ParameterKind.Text, Hint: "überschreibt die Adresse aus der ID, z. B. 0xF9"),
            ],
            p => new MessageSendAction(p.Text("Ressource"), p.Text("Datenbank"), p.Text("Botschaft"), SignalValues.Parse(p.Optional("Werte")),
                Math.Max(1, (int)(p.Number("Anzahl") ?? 1)), p.Number("Abstand"), ParseAddress(p.Optional("Quelladresse"), "Quelladresse"))));

        RegisterDiagnosticBlocks(c, cat);

        c.Register(new(J1939Receive, "J1939 empfangen", cat, "measure",
            "Empfängt eine Parametergruppe (PGN) – einzeln, als BAM oder per RTS/CTS – optional nach einer Anforderung (PGN 59904). "
            + "Signal aus der Datenbank oder über Startbit/Länge; „nicht verfügbar“ (alle Bits 1) ist FAIL.",
            [
                Res("Pruefling_CAN"),
                new("Pgn", "PGN", ParameterKind.Text, Hint: "z. B. 0xFEEE oder 65262; leer bei Signal aus der Datenbank"),
                new("Quelle", "Quelladresse", ParameterKind.Text, Hint: "leer = von jedem Teilnehmer"),
                new("Anfordern", "Vorher anfordern (Request)", ParameterKind.Bool, "false"),
                new("Eigene", "Eigene Adresse", ParameterKind.Text, "0xF9"),
                new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, "1000", Required: true),
                Database(false),
                new("Signal", "Signal (Datenbank)", ParameterKind.Text, Hint: "Botschaft.Signal oder Signalname"),
                .. RawSignal(),
                .. Evaluation(),
            ],
            p => new J1939ReceiveAction(p.Text("Ressource"), p.Optional("Pgn") is { } pgn ? ParsePgn(pgn) : null, ParseAddress(p.Optional("Quelle"), "Quelladresse"),
                p.Bool("Anfordern"), ParseAddress(p.Optional("Eigene"), "Eigene Adresse") ?? 0xF9, TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000),
                p.Optional("Datenbank"), p.Optional("Signal"), RawSignalFrom(p), SignalCheck.From(p, withBytes: true))));

        c.Register(new(J1939Send, "J1939 senden", cat, "arrow",
            "Sendet eine Parametergruppe mit Priorität, Quell- und Zieladresse; mehr als 8 Bytes über das Transportprotokoll (global BAM, sonst RTS/CTS).",
            [
                Res("Pruefling_CAN"),
                new("Pgn", "PGN", ParameterKind.Text, Hint: "leer = PGN der Datenbank-Botschaft"),
                new("Prioritaet", "Priorität", ParameterKind.Number, "6"),
                new("Quelle", "Quelladresse", ParameterKind.Text, "0xF9", Required: true),
                new("Ziel", "Zieladresse", ParameterKind.Text, "0xFF", Hint: "nur PDU1 (PF < 240); 0xFF = global"),
                new("Daten", "Daten (Hex)", ParameterKind.Text, Hint: "z. B. FF FF 00 7D; {Ausdruck} fügt ein Byte ein"),
                Database(false),
                new("Botschaft", "Botschaft (Datenbank)", ParameterKind.Text),
                new("Werte", "Signalwerte", ParameterKind.Text, Hint: "Signal = Ausdruck; …"),
            ],
            p =>
            {
                CommunicationBlocks.CheckHex(p.Text("Daten"), "Daten");
                var prio = (int)(p.Number("Prioritaet") ?? 6);
                if (prio is < 0 or > 7) throw new BlockConfigurationException("Priorität 0–7.");
                if (p.Optional("Daten") is null && p.Optional("Botschaft") is null) throw new BlockConfigurationException("Daten oder Botschaft aus der Datenbank angeben.");
                return new J1939SendAction(p.Text("Ressource"), p.Optional("Pgn") is { } pgn ? ParsePgn(pgn) : null, prio,
                    ParseAddress(p.Optional("Quelle"), "Quelladresse") ?? 0xF9, ParseAddress(p.Optional("Ziel"), "Zieladresse") ?? J1939.Global,
                    p.Text("Daten"), p.Optional("Datenbank"), p.Optional("Botschaft"), SignalValues.Parse(p.Optional("Werte")));
            }));

        c.Register(new(LinRead, "LIN lesen", cat, "measure",
            "Sendet als Master den Header und liest die Slave-Antwort; prüft Daten oder ein Signal (aus der LDF oder Startbit/Länge).",
            [
                Res("Pruefling_LIN"),
                new("Frame", "Frame", ParameterKind.Text, Required: true, Hint: "ID (0x00–0x3F) oder Name aus der LDF"),
                Database(false),
                new("Signal", "Signal (Datenbank)", ParameterKind.Text),
                new("Laenge", "Länge [Bytes]", ParameterKind.Number, Hint: "leer = laut LDF bzw. 8"),
                new("Pruefsumme", "Prüfsumme", ParameterKind.Choice, LinChecksums[0], Choices: LinChecksums),
                new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, "100"),
                .. RawSignal(),
                .. Evaluation(),
            ],
            p => new LinReadAction(p.Text("Ressource"), p.Text("Frame"), p.Optional("Datenbank"), p.Optional("Signal"), (int?)p.Number("Laenge"),
                p.Choice("Pruefsumme") == LinChecksums[1] ? LinChecksum.Classic : LinChecksum.Enhanced, TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 100),
                RawSignalFrom(p), SignalCheck.From(p, withBytes: true))));

        c.Register(new(LinSend, "LIN senden", cat, "arrow",
            "Sendet als Master einen Frame mit Daten (Hex) oder aus Signalwerten der LDF.",
            [
                Res("Pruefling_LIN"),
                new("Frame", "Frame", ParameterKind.Text, Required: true, Hint: "ID (0x00–0x3F) oder Name aus der LDF"),
                Database(false),
                new("Daten", "Daten (Hex)", ParameterKind.Text, Hint: "z. B. 01 00 FF; {Ausdruck} fügt ein Byte ein"),
                new("Werte", "Signalwerte", ParameterKind.Text, Hint: "Signal = Ausdruck; … (mit Datenbank)"),
                new("Pruefsumme", "Prüfsumme", ParameterKind.Choice, LinChecksums[0], Choices: LinChecksums),
            ],
            p =>
            {
                CommunicationBlocks.CheckHex(p.Text("Daten"), "Daten");
                if (p.Optional("Daten") is null && p.Optional("Werte") is null) throw new BlockConfigurationException("Daten oder Signalwerte angeben.");
                return new LinSendAction(p.Text("Ressource"), p.Text("Frame"), p.Optional("Datenbank"), p.Text("Daten"), SignalValues.Parse(p.Optional("Werte")),
                    p.Choice("Pruefsumme") == LinChecksums[1] ? LinChecksum.Classic : LinChecksum.Enhanced);
            }));

        c.Register(new(J1939Claim, "J1939 Adresse beanspruchen", cat, "arrow",
            "Beansprucht eine Quelladresse nach J1939-81 (Address Claimed, PGN 60928) mit dem eigenen 64-Bit-NAME und prüft 250 ms "
            + "lang auf Gegenansprüche. Verliert die eigene Station (kleinerer NAME der Gegenstelle), ist der Schritt FAIL. Optional "
            + "vorher alle Teilnehmer abfragen und in eine Variable schreiben.",
            [
                Res("Pruefling_CAN"),
                new("Adresse", "Quelladresse", ParameterKind.Text, "0x80", Required: true, Hint: "0x00–0xFD"),
                new("Name", "NAME (64 Bit)", ParameterKind.Text, "0x00000000000000FF", Required: true, Hint: "kleiner = höhere Priorität"),
                new("Abfragen", "Teilnehmer vorher abfragen", ParameterKind.Bool, "false"),
                new("Ziel", "Teilnehmer in Variable", ParameterKind.Variable, Hint: "Liste „Adresse=NAME“ (bei Abfrage)"),
            ],
            p => new J1939ClaimAction(p.Text("Ressource"), ParseAddress(p.Text("Adresse"), "Quelladresse") ?? 0x80, ParseName(p.Text("Name")),
                p.Bool("Abfragen"), p.Optional("Ziel"))));

        c.Register(new(LinSchedule, "LIN-Fahrplan", cat, "measure",
            "Arbeitet eine LIN-Fahrplantabelle ab: je Zeile ein Frame (empfangen oder senden) mit festem Abstand. Optional mehrere Durchläufe. "
            + "Unbeantwortete Subscriber-Frames sind FAIL (oder nur Hinweis).",
            [
                Res("Pruefling_LIN"),
                Database(false),
                new("Tabelle", "Fahrplan", ParameterKind.Text, Required: true,
                    Hint: "Je Zeile: ID [Abstand ms]  bzw. ID=DATEN (senden). Name aus der LDF erlaubt. Beispiel: 0x21 15; FH_Befehl=01 C8 15"),
                new("Durchlaeufe", "Durchläufe", ParameterKind.Number, "1"),
                new("FehlendFail", "Fehlende Antwort = FAIL", ParameterKind.Bool, "true"),
            ],
            p => new LinScheduleAction(p.Text("Ressource"), p.Optional("Datenbank"), p.Text("Tabelle"), Math.Max(1, (int)(p.Number("Durchlaeufe") ?? 1)),
                p.Bool("FehlendFail"))));

        c.Register(new(DoIpDiscover, "DoIP Fahrzeugsuche", cat, "measure",
            "Sendet eine DoIP-Fahrzeugsuche (Vehicle Identification Request, UDP 13400) und sammelt die antwortenden Steuergeräte/Gateways "
            + "(VIN, logische Adresse). Ohne Hardware-Reservierung. Optional auf eine erwartete VIN oder Adresse prüfen.",
            [
                new("Host", "Adresse", ParameterKind.Text, "255.255.255.255", Hint: "Broadcast (…255) oder gezielt host/IP"),
                new("Port", "Port", ParameterKind.Number, "13400"),
                new("Zeitlimit", "Zeitlimit [ms]", ParameterKind.Number, "1000", Required: true),
                new("ErwarteteVin", "Erwartete VIN", ParameterKind.Text, Hint: "leer = keine Prüfung; sonst muss ein Fahrzeug diese VIN melden"),
                new("ErwarteteAdresse", "Erwartete Adresse", ParameterKind.Text, Hint: "z. B. 0x1000"),
                new("Mindestens", "Mindestanzahl", ParameterKind.Number, Hint: "mindestens so viele Fahrzeuge erwartet"),
                new("Ziel", "Ergebnis in Variable", ParameterKind.Variable, Hint: "Liste „VIN@0xAdresse“"),
            ],
            p => new DoIpDiscoverAction(p.Text("Host"), (int)(p.Number("Port") ?? 13400), TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000),
                p.Optional("ErwarteteVin"), p.Optional("ErwarteteAdresse") is { } a ? ParseWord(a, 0, "Erwartete Adresse") : null,
                (int?)p.Number("Mindestens"), p.Optional("Ziel"))));

        c.Register(new(OpcUaAccess, "OPC UA", cat, "measure",
            "Liest oder schreibt einen OPC-UA-Variablenknoten, ruft eine Methode auf oder wartet auf eine Datenänderung (Abo). "
            + "Gelesene/zurückgegebene Werte können gegen Sollwert oder Grenzwerte geprüft werden.",
            [
                Res("Pruefling_OPCUA"),
                new("Aktion", "Aktion", ParameterKind.Choice, OpcActions[0], Choices: OpcActions),
                new("Knoten", "Knoten (NodeId)", ParameterKind.Text, "ns=2;s=Spannung", Hint: "Lesen/Schreiben/Abonnieren; z. B. ns=2;s=Name oder ns=2;i=42"),
                new("Wert", "Wert (Schreiben)", ParameterKind.Expression, Hint: "nur bei Schreiben"),
                new("Objekt", "Methode: Objektknoten", ParameterKind.Text, Hint: "nur bei Methode aufrufen"),
                new("Methode", "Methode: Methodenknoten", ParameterKind.Text),
                new("Argumente", "Methode: Argumente", ParameterKind.Text, Hint: "mit ; getrennt, z. B. 2;3 oder true;\"Text\""),
                new("Zeitlimit", "Abo: Zeitlimit [ms]", ParameterKind.Number, "1000"),
                new("Sollwert", "Sollwert", ParameterKind.Expression, Hint: "Gleichheit (Zahl/Text/Bool)"),
                new("Ziel", "Zielvariable", ParameterKind.Variable),
                new("Untergrenze", "Untergrenze", ParameterKind.Expression),
                new("Obergrenze", "Obergrenze", ParameterKind.Expression),
                new("Einheit", "Einheit", ParameterKind.Text),
            ],
            p => new OpcUaAction(p.Text("Ressource"), Array.IndexOf(OpcActions, p.Choice("Aktion")), p.Optional("Knoten"), p.Expr("Wert"),
                p.Optional("Objekt"), p.Optional("Methode"), p.Optional("Argumente"), TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000),
                p.Expr("Sollwert"), p.Optional("Ziel"), p.Expr("Untergrenze"), p.Expr("Obergrenze"), p.Text("Einheit"))));
    }

    // ------------------------------------------------------------------ Hilfen

    private static void RegisterDiagnosticBlocks(BlockCatalog c, string cat)
    {
        c.Register(new(UdsRequest, "UDS-Diagnose", cat, "measure",
            "Sendet eine Diagnoseanfrage über ISO-TP auf CAN (Segmentierung, Flusssteuerung, erweiterte Adressierung) oder DoIP auf Ethernet "
            + "und wertet die positive Antwort aus (Daten ab Byte als Hex, Text oder Zahl) oder prüft eine erwartete negative Antwort.",
            [
                Res("Pruefling_Diagnose"),
                .. DiagParameters(),
                new("Anfrage", "Anfrage (Hex)", ParameterKind.Text, "22 F1 90", Required: true, Hint: "z. B. 10 03, 22 F1 90; LIN: ohne NAD/PCI (automatisch); {Ausdruck} fügt ein Byte ein"),
                new("Zeitlimit", "P2 [ms]", ParameterKind.Number, "1000", Required: true),
                new("ZeitlimitLang", "P2* nach „Antwort folgt“ [ms]", ParameterKind.Number, "5000"),
                new("ErwarteteNrc", "Erwartete negative Antwort (NRC)", ParameterKind.Text, Hint: "z. B. 0x33 – dann ist genau diese Ablehnung PASS"),
                new("AbByte", "Daten ab Byte", ParameterKind.Number, Hint: "1 = Dienstkennung; leer: 22/2E → 4, 31 → 5, 19 → 3, sonst 2"),
                new("Laenge", "Länge [Bytes]", ParameterKind.Number, Hint: "leer = bis zum Ende"),
                new("Darstellung", "Darstellung", ParameterKind.Choice, "Hex", Choices: UdsFormats),
                new("Faktor", "Faktor (Zahl)", ParameterKind.Number, "1"),
                new("Offset", "Offset (Zahl)", ParameterKind.Number, "0"),
                .. Evaluation(),
            ],
            p =>
            {
                CommunicationBlocks.CheckHex(p.Text("Anfrage"), "Anfrage");
                return new UdsAction(p.Text("Ressource"), DiagSpec.From(p), p.Text("Anfrage"), TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000),
                    TimeSpan.FromMilliseconds(p.Number("ZeitlimitLang") ?? 5000), ParseAddress(p.Optional("ErwarteteNrc"), "Erwartete NRC"),
                    (int?)p.Number("AbByte"), (int?)p.Number("Laenge"), Array.IndexOf(UdsFormats, p.Choice("Darstellung")), p.Number("Faktor") ?? 1, p.Number("Offset") ?? 0,
                    SignalCheck.From(p, withBytes: true));
            }));

        c.Register(new(UdsSecurity, "UDS-Sicherheitszugang", cat, "arrow",
            "Fordert einen Seed an (Dienst 0x27, Stufe ungerade), berechnet den Schlüssel und sendet ihn (Stufe+1). Schlüsselverfahren "
            + "wählbar (XOR/Addition/Invertieren/Add-Rotate-XOR) oder „Ausdruck je Byte“ (s = Seedbyte, i = Index). Seed aus lauter Nullen "
            + "= bereits freigeschaltet. Negative Antwort (z. B. falscher Schlüssel) = FAIL.",
            [
                Res("Pruefling_Diagnose"),
                .. DiagParameters(),
                new("Stufe", "Seed-Stufe", ParameterKind.Text, "0x01", Required: true, Hint: "ungerade: 0x01, 0x03, …; Schlüssel geht an Stufe+1"),
                new("Verfahren", "Schlüsselverfahren", ParameterKind.Choice, SecurityMethods[0], Choices: SecurityMethods),
                new("Konstante", "Konstante (Hex)", ParameterKind.Text, "5A", Hint: "für XOR/Addition/Add-Rotate-XOR; byteweise wiederholt"),
                new("Ausdruck", "Ausdruck je Byte", ParameterKind.Expression, Hint: "nur bei „Ausdruck je Byte“: Funktion von s (Seedbyte 0–255) und i (Index), z. B. (s + 7) * 3"),
                new("Zeitlimit", "P2 [ms]", ParameterKind.Number, "1000", Required: true),
                new("ZeitlimitLang", "P2* [ms]", ParameterKind.Number, "5000"),
                new("Ziel", "Schlüssel in Variable", ParameterKind.Variable),
            ],
            p =>
            {
                var method = Array.IndexOf(SecurityMethods, p.Choice("Verfahren"));
                Expression? expr = method == 4 ? p.Expr("Ausdruck") : null;
                if (method == 4 && expr is null) throw new BlockConfigurationException("„Ausdruck je Byte“: Ausdruck angeben.");
                var constant = method is 0 or 1 or 3 ? CheckHexBytes(p.Optional("Konstante"), "Konstante") : [];
                return new UdsSecurityAction(p.Text("Ressource"), DiagSpec.From(p), ParseByte(p.Text("Stufe"), "Seed-Stufe"), method, constant, expr,
                    TimeSpan.FromMilliseconds(p.Number("Zeitlimit") ?? 1000), TimeSpan.FromMilliseconds(p.Number("ZeitlimitLang") ?? 5000), p.Optional("Ziel"));
            }));

    }

    private static IEnumerable<ParameterDescriptor> DiagParameters() =>
    [
        new("Transport", "Transport", ParameterKind.Choice, DiagTransports[0], Choices: DiagTransports,
            Hint: "DoIP: Ressource ist ein TCP-Client-Gerät (Port 13400)"),
        new("AnfrageId", "CAN: Anfrage-ID (Tester)", ParameterKind.Text, "0x7E0"),
        new("AntwortId", "CAN: Antwort-ID (Steuergerät)", ParameterKind.Text, "0x7E8"),
        new("Erweitert", "CAN: 29-Bit-IDs", ParameterKind.Bool, "false"),
        new("Fd", "CAN FD (bis 64 Bytes je Rahmen)", ParameterKind.Bool, "false"),
        new("AdresseTx", "CAN: Adressbyte Anfrage", ParameterKind.Text, Hint: "erweiterte/gemischte Adressierung, z. B. 0x10; leer = normal"),
        new("AdresseRx", "CAN: Adressbyte Antwort", ParameterKind.Text, Hint: "erstes Datenbyte der Antworten, z. B. 0xF1"),
        new("Fuellbyte", "CAN: Füllbyte", ParameterKind.Text, "CC"),
        new("TesterAdresse", "DoIP: Tester-Adresse", ParameterKind.Text, "0x0E00"),
        new("SteuergeraetAdresse", "DoIP: Steuergerät-Adresse", ParameterKind.Text, "0x1000"),
        new("Nad", "LIN: NAD (Knotenadresse)", ParameterKind.Text, "0x01"),
    ];

    internal static byte ParseByte(string text, string label)
        => ParseAddress(text, label) ?? throw new BlockConfigurationException($"{label}: Wert fehlt.");

    internal static byte[] CheckHexBytes(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            return CommText.ParseHex(text);
        }
        catch (FormatException ex)
        {
            throw new BlockConfigurationException($"{label}: {ex.Message}");
        }
    }

    internal static ushort ParseWord(string? text, ushort fallback, string label)
    {
        if (text is null) return fallback;
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            : ushort.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        return ok ? v : throw new BlockConfigurationException($"{label}: „{text}“ ist keine 16-Bit-Adresse (z. B. 0x0E00).");
    }

    internal static byte? ParseAddress(string? text, string label)
    {
        if (text is null) return null;
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)
            : byte.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out b) || (t.Length == 2 && byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b));
        return ok ? b : throw new BlockConfigurationException($"{label}: „{text}“ ist kein Byte (z. B. 0xF9 oder 249).");
    }

    internal static ulong ParseName(string text)
    {
        var t = text.Trim();
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            : ulong.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        return ok ? v : throw new BlockConfigurationException($"NAME „{text}“ ist keine 64-Bit-Zahl (z. B. 0x00000000000000FF).");
    }

    internal static uint ParsePgn(string text)
    {
        try
        {
            return J1939.ParsePgn(text);
        }
        catch (FormatException ex)
        {
            throw new BlockConfigurationException(ex.Message);
        }
    }

    private static CanSignal? RawSignalFrom(BlockParameters p)
        => p.Number("Bits") is { } bits
            ? (bits is < 1 or > 64 ? throw new BlockConfigurationException("Signallänge 1–64 Bit.")
                : new CanSignal((int)(p.Number("Startbit") ?? 0), (int)bits, p.Choice("Byteorder") == CommunicationBlocks.ByteOrders[1], p.Bool("Vorzeichen"),
                    p.Number("Faktor") ?? 1, p.Number("Offset") ?? 0))
            : null;

    internal static byte[] Bytes(string template, IStepContext ctx)
        => CommText.ParseHex(CommunicationBlocks.Interpolate(template, ctx,
            v => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("X2", CultureInfo.InvariantCulture)));

    internal static BusDatabase Database(IStepContext ctx, string name)
    {
        var def = ctx.FindBusDatabase(name) ?? throw new BusDatabaseException($"Busdatenbank „{name}“ ist nicht im Projekt (Projekteigenschaften → Busdatenbanken).");
        return def.Parsed;
    }

    internal static string Hex(byte[] data) => string.Join(" ", data.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Editor-Diagnose: Datenbank, Botschaft und Signal der Schrittparameter im Projekt vorhanden?</summary>
    public static IReadOnlyList<string> CheckReferences(StepDefinition step, TestDefinition definition)
    {
        if (!DatabaseBlocks.Contains(step.BlockType)) return [];
        var name = step.Parameters.GetValueOrDefault("Datenbank");
        if (string.IsNullOrWhiteSpace(name)) return [];
        var def = definition.FindBusDatabase(name.Trim());
        if (def is null) return [$"Busdatenbank „{name.Trim()}“ ist nicht im Projekt."];
        try
        {
            var db = def.Parsed;
            var expected = step.BlockType is LinRead or LinSend ? BusDatabaseFormat.Ldf : BusDatabaseFormat.Dbc;
            if (db.Format != expected) return [$"„{def.Name}“ ist eine {db.Format.ToString().ToUpperInvariant()}-Datei; erwartet {expected.ToString().ToUpperInvariant()}."];
            CheckMessageReferences(step, db);
            return [];
        }
        catch (BusDatabaseException ex)
        {
            return [ex.Message];
        }
        catch (BlockConfigurationException ex)
        {
            return [ex.Message];
        }
    }

    private static void CheckMessageReferences(StepDefinition step, BusDatabase db)
    {
        if (step.Parameters.GetValueOrDefault("Signal") is { Length: > 0 } signal && !signal.Contains('{')) _ = db.FindSignal(signal.Trim());
        var message = step.Parameters.GetValueOrDefault("Botschaft") ?? (step.BlockType is LinRead or LinSend ? step.Parameters.GetValueOrDefault("Frame") : null);
        if (message is { Length: > 0 } && !(step.BlockType is LinRead or LinSend && byte.TryParse(message.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
        {
            var m = db.RequireMessage(message.Trim());
            foreach (var (signalName, _) in SignalValues.Parse(step.Parameters.GetValueOrDefault("Werte")).Items) _ = m.RequireSignal(signalName);
        }
    }

    /// <summary>Daten so lange empfangen, bis <paramref name="accept"/> zutrifft (Multiplex).</summary>
    internal static async Task<CanFrame> ReceiveAsync(ICanAdapter can, string channel, CanFilter filter, Func<CanFrame, bool> accept, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        for (var skipped = 0; ; skipped++)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || skipped > 1000)
                throw new CommunicationTimeoutException($"Kein passender Rahmen (ID 0x{filter.Id:X}, Multiplex) innerhalb von {timeout.TotalMilliseconds:0} ms.", []);
            var frame = await can.ReceiveAsync(channel, filter, remaining, token).ConfigureAwait(false);
            if (accept(frame)) return frame;
        }
    }
}

// ---------------------------------------------------------------------- Signalwerte „Signal = Ausdruck; …“

internal sealed class SignalValues
{
    public static readonly SignalValues Empty = new([]);

    private SignalValues(List<(string Name, string Value)> items) => Items = items;

    public IReadOnlyList<(string Name, string Value)> Items { get; }

    public static SignalValues Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var list = new List<(string, string)>();
        foreach (var part in text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || part[eq..].StartsWith("==", StringComparison.Ordinal))
                throw new BlockConfigurationException($"Signalwerte: „{part}“ – erwartet „Signal = Ausdruck“.");
            var value = part[(eq + 1)..].Trim();
            if (value.Length == 0) throw new BlockConfigurationException($"Signalwerte: Wert für „{part[..eq].Trim()}“ fehlt.");
            list.Add((part[..eq].Trim(), value));
        }
        return new SignalValues(list);
    }

    /// <summary>Werte auswerten: Eintrag der Wertetabelle oder Ausdruck (Zahl, Bool, Text der Wertetabelle).</summary>
    public Dictionary<string, double> Evaluate(BusMessage message, IStepContext ctx)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, text) in Items)
        {
            var signal = message.RequireSignal(name);
            var label = text.Trim('"');
            if (signal.Values.Any(v => string.Equals(v.Value, label, StringComparison.OrdinalIgnoreCase)))
            {
                result[signal.Name] = signal.ParseValue(label);
                continue;
            }
            object value;
            try
            {
                value = Expression.Parse(text).Evaluate(new ContextResolver(ctx));
            }
            catch (ExpressionException ex)
            {
                throw new BusDatabaseException($"Signal „{name}“: {ex.Message}");
            }
            result[signal.Name] = value switch
            {
                bool b => b ? 1 : 0,
                string s => signal.ParseValue(s),
                _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            };
        }
        return result;
    }
}

// ---------------------------------------------------------------------- gemeinsame Bewertung

internal sealed record SignalCheck(ExpectedBytes? Bytes, Expression? Expected, string? Target, Expression? Low, Expression? High, string Unit, bool TimeoutAsFail)
{
    public static SignalCheck From(BlockParameters p, bool withBytes)
        => new(withBytes ? ExpectedBytes.Parse(p.Optional("Erwartet")) : null, p.Expr("Sollwert"), p.Optional("Ziel"), p.Expr("Untergrenze"), p.Expr("Obergrenze"),
            p.Text("Einheit"), p.Choice("BeiZeitlimit") == "FAIL");

    public bool RatesValue => Expected is not null || Low is not null || High is not null;

    /// <summary>Daten prüfen (erwartete Bytes) – Fehlerergebnis oder null.</summary>
    public StepOutcome? CheckBytes(byte[] data, string shown)
        => Bytes is not null && !Bytes.Matches(data)
            ? new StepOutcome(ResultRating.Fail, BusBlocks.Hex(data), Message: $"{shown} – erwartet {Bytes}")
            : null;

    private bool TargetIsText(IStepContext ctx)
        => Target is not null && ctx.VisibleVariables().Any(v => v.Name == Target && v.Type == VarType.String);

    /// <summary>Signalwert bewerten (Sollwert mit Wertetabelle, Grenzwerte) und Zielvariable setzen.</summary>
    public StepOutcome Rate(IStepContext ctx, double value, string? text, string? defaultUnit, double resolution, string what)
    {
        var unit = string.IsNullOrWhiteSpace(Unit) ? defaultUnit ?? "" : Unit;
        if (Target is not null) CommunicationBlocks.SetTarget(ctx, Target, text is not null && TargetIsText(ctx) ? text : value);
        var shown = $"{value.ToString("0.######", CultureInfo.InvariantCulture)}{(unit.Length > 0 ? " " + unit : "")}{(text is null ? "" : $" ({text})")}";
        if (Expected is not null)
            return RateExpected(ctx, value, text, unit, resolution, what, shown);
        var rated = CommunicationBlocks.Rate(ctx, value, Low, High, unit, $"{what} = {shown}");
        return Bytes is not null && rated.Rating == ResultRating.NResult ? rated with { Rating = ResultRating.Pass } : rated;
    }

    /// <summary>Ohne Signal: Daten in die Zielvariable; PASS bei erwarteten Bytes, sonst unbewertet.</summary>
    public StepOutcome RateData(IStepContext ctx, byte[] data, string shown)
    {
        if (Target is not null) CommunicationBlocks.SetTarget(ctx, Target, Convert.ToHexString(data));
        return new StepOutcome(Bytes is not null ? ResultRating.Pass : ResultRating.NResult, BusBlocks.Hex(data), Message: shown);
    }

    public StepOutcome Timeout(CommunicationTimeoutException ex) => CommunicationBlocks.Timeout(ex, TimeoutAsFail, hex: true);
    private StepOutcome RateExpected(IStepContext ctx, double value, string? text, string unit, double resolution, string what, string shown)
    {
        var expected = Expected!.Evaluate(new ContextResolver(ctx));
        bool ok;
        if (expected is string s && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            ok = text is not null && string.Equals(text, s, StringComparison.OrdinalIgnoreCase);
        else
        {
            var e = expected is string es ? double.Parse(es, NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDouble(expected, CultureInfo.InvariantCulture);
            ok = Math.Abs(value - e) <= Math.Max(Math.Abs(resolution) / 2, 1e-9);
        }
        return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, (object?)text ?? value, unit.Length > 0 ? unit : null,
            Message: ok ? $"{what} = {shown}" : $"{what} = {shown}, erwartet {CommunicationBlocks.Text(expected)}");
    }

}

// ---------------------------------------------------------------------- CAN mit DBC

internal sealed class SignalReadAction(string resource, string database, string signal, double? timeoutMs, SignalCheck check) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        BusMessage message;
        BusSignal sig;
        BusDatabase db;
        try
        {
            db = BusBlocks.Database(context, database);
            (message, sig) = db.FindSignal(signal);
        }
        catch (BusDatabaseException ex)
        {
            return StepOutcome.Error(ex.Message);
        }
        var timeout = TimeSpan.FromMilliseconds(timeoutMs ?? (message.CycleMs is > 0 and var c ? Math.Max(100, 3.0 * c) : 1000));
        var filter = db.IsJ1939 && message.Extended ? J1939.Filter(message.Pgn, null) : new CanFilter(message.Id, 0x1FFFFFFF, message.Extended);
        CanFrame frame;
        try
        {
            frame = await context.UseDeviceAsync(resource, $"CAN-Signal {message.Name}.{sig.Name}", async (adapter, channel, token) =>
            {
                var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
                can.DiscardInput(channel);
                return await BusBlocks.ReceiveAsync(can, channel, filter, f => message.IsActive(sig, f.Data), timeout, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (CommunicationTimeoutException ex)
        {
            return check.Timeout(ex);
        }
        double value;
        string? text;
        try
        {
            value = sig.Physical(frame.Data);
            text = sig.Describe(frame.Data);
        }
        catch (ArgumentException ex)
        {
            return new StepOutcome(ResultRating.Fail, frame.DataHex, Message: $"{message.Name}: {ex.Message} ({frame})");
        }
        return check.Rate(context, value, text, sig.Unit, sig.Coding.Factor, $"{message.Name}.{sig.Name}");
    }
}

internal sealed class MessageSendAction(string resource, string database, string messageName, SignalValues values, int count, double? gapMs, byte? source) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        BusMessage message;
        BusDatabase db;
        byte[] data;
        try
        {
            db = BusBlocks.Database(context, database);
            message = db.RequireMessage(messageName);
            data = message.Encode(values.Evaluate(message, context));
        }
        catch (Exception ex) when (ex is BusDatabaseException or FormatException or InvalidCastException or OverflowException)
        {
            return StepOutcome.Error(ex.Message);
        }
        var id = message.Id;
        if (source is { } sa && message.Extended) id = id & ~0xFFu | sa;
        var j1939 = db.IsJ1939 && message.Extended;
        var gap = TimeSpan.FromMilliseconds(gapMs ?? (message.CycleMs is > 0 and var c ? c : 100));
        var frame = new CanFrame(id, data, message.Extended, message.Fd, message.Fd);
        if (!j1939 || data.Length <= 8)
            if (frame.Validate() is { } error) return StepOutcome.Error(error);
        await context.UseDeviceAsync(resource, $"CAN senden {message.Name}", async (adapter, channel, token) =>
        {
            var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) await context.Clock.Delay(gap, token).ConfigureAwait(false);
                if (j1939 && data.Length > 8)
                    await J1939.SendAsync(can, channel, J1939.PgnOf(id), data, J1939.SourceOf(id), J1939.DestinationOf(id) ?? J1939.Global, J1939.PriorityOf(id), token).ConfigureAwait(false);
                else
                    await can.SendAsync(channel, frame, token).ConfigureAwait(false);
            }
            return 0;
        }).ConfigureAwait(false);
        var decoded = string.Join(", ", message.Decode(data).Select(d => $"{d.Signal.Name}={d.Text ?? d.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
        return StepOutcome.Unrated(frame.DataHex, $"{message.Name}{(count > 1 ? $" {count}×" : "")}: {decoded}");
    }
}

// ---------------------------------------------------------------------- UDS

/// <summary>Diagnosetransport aus den Bausteinparametern (ISO-TP auf CAN oder DoIP).</summary>
internal sealed record DiagSpec(int Transport, IsoTpSettings IsoTp, DoIpSettings DoIpSettings, byte Nad)
{
    public static DiagSpec From(BlockParameters p)
    {
        var transport = Array.IndexOf(BusBlocks.DiagTransports, p.Choice("Transport"));
        var ext = p.Bool("Erweitert");
        var tx = CanIdSpec.Parse(p.Optional("AnfrageId") ?? "0x7E0", ext);
        var rx = CanIdSpec.Parse(p.Optional("AntwortId") ?? "0x7E8", ext);
        var iso = new IsoTpSettings(tx.Id, rx.Id, tx.Extended || rx.Extended, p.Bool("Fd"), BusBlocks.ParseAddress(p.Optional("Fuellbyte"), "Füllbyte") ?? 0xCC)
        {
            TxAddress = BusBlocks.ParseAddress(p.Optional("AdresseTx"), "Adressbyte Anfrage"),
            RxAddress = BusBlocks.ParseAddress(p.Optional("AdresseRx"), "Adressbyte Antwort"),
        };
        var dip = new DoIpSettings(BusBlocks.ParseWord(p.Optional("TesterAdresse"), 0x0E00, "Tester-Adresse"),
            BusBlocks.ParseWord(p.Optional("SteuergeraetAdresse"), 0x1000, "Steuergerät-Adresse"));
        return new DiagSpec(transport, iso, dip, BusBlocks.ParseAddress(p.Optional("Nad"), "NAD") ?? 0x01);
    }

    public IDiagnosticTransport Create(IHardwareAdapter adapter, string channel)
        => Transport switch
        {
            1 => new DoIpTransport(CommunicationBlocks.As<IByteStreamAdapter>(adapter, "Byte-Strom (TCP)"), channel, DoIpSettings),
            2 => new LinDiagnosticTransport(CommunicationBlocks.As<ILinAdapter>(adapter, "LIN"), channel, Nad),
            _ => new IsoTpTransport(CommunicationBlocks.As<ICanAdapter>(adapter, "CAN"), channel, IsoTp),
        };
}

internal sealed class UdsAction(string resource, DiagSpec settings, string request, TimeSpan p2, TimeSpan p2Star, byte? expectedNrc, int? fromByte,
    int? length, int format, double factor, double offset, SignalCheck check) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        byte[] bytes;
        try
        {
            bytes = BusBlocks.Bytes(request, context);
        }
        catch (FormatException ex)
        {
            return StepOutcome.Error("Anfrage: " + ex.Message);
        }
        if (bytes.Length == 0) return StepOutcome.Error("UDS-Anfrage ist leer.");
        var label = $"UDS {BusBlocks.Hex(bytes)}";
        byte[] response;
        try
        {
            response = await context.UseDeviceAsync(resource, label, (adapter, channel, token)
                => Uds.RequestAsync(settings.Create(adapter, channel), bytes, p2, p2Star, token)).ConfigureAwait(false);
        }
        catch (UdsNegativeResponseException ex)
        {
            if (check.Target is not null) CommunicationBlocks.SetTarget(context, check.Target, $"7F{ex.Service:X2}{ex.Code:X2}");
            var ok = expectedNrc == ex.Code;
            return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, $"7F {ex.Service:X2} {ex.Code:X2}",
                Message: ok ? $"{label}: erwartete Ablehnung NRC 0x{ex.Code:X2} {Uds.Describe(ex.Code)}" : $"{label}: {ex.Message}");
        }
        catch (CommunicationTimeoutException ex)
        {
            return check.Timeout(ex);
        }
        return EvaluateResponse(context, bytes, response, label);
    }

    private StepOutcome EvaluateResponse(IStepContext context, byte[] bytes, byte[] response, string label)
    {
        var shown = $"{label} → {BusBlocks.Hex(response)}";
        if (expectedNrc is { } nrc) return new StepOutcome(ResultRating.Fail, BusBlocks.Hex(response), Message: $"{shown}: positive Antwort, erwartet NRC 0x{nrc:X2}");
        if (response.Length == 0) return new StepOutcome(ResultRating.Pass, Message: $"{label}: positive Antwort unterdrückt, keine Ablehnung");
        if (check.CheckBytes(PrefixOf(response), shown) is { } mismatch) return mismatch;

        var start = (fromByte ?? DefaultStart(bytes[0])) - 1;
        if (start < 0) return StepOutcome.Error("„Daten ab Byte“ beginnt bei 1.");
        if (start > response.Length) return new StepOutcome(ResultRating.Fail, BusBlocks.Hex(response), Message: $"{shown}: Antwort kürzer als Byte {start + 1}.");
        var take = length ?? response.Length - start;
        if (start + take > response.Length) return new StepOutcome(ResultRating.Fail, BusBlocks.Hex(response), Message: $"{shown}: Antwort kürzer als {start + take} Bytes.");
        var data = response.AsSpan(start, take).ToArray();

        switch (format)
        {
            case 2 or 3:
                {
                    return RateNumber(context, data, shown, label);
                }
            case 1:
                {
                    var text = Encoding.Latin1.GetString(data).TrimEnd('\0', '\xFF', ' ');
                    return RateText(context, text, shown);
                }
            default:
                return RateText(context, BusBlocks.Hex(data), shown, isHex: true);
        }
    }

    private StepOutcome RateNumber(IStepContext context, byte[] data, string shown, string label)
    {
        if (data.Length is 0 or > 8) return StepOutcome.Error($"{shown}: Zahl braucht 1–8 Bytes (erhalten {data.Length}).");
        ulong raw = 0;
        foreach (var b in data) raw = raw << 8 | b;
        double number = format == 3 && data.Length < 8 && (data[0] & 0x80) != 0 ? (long)(raw | ulong.MaxValue << data.Length * 8) : format == 3 ? (long)raw : raw;
        return check.Rate(context, number * factor + offset, null, null, factor, label);
    }

    /// <summary>Für „Erwartete Daten“: Vergleich mit dem Antwortbeginn (Länge des Musters).</summary>
    private byte[] PrefixOf(byte[] response)
        => check.Bytes is { } e && response.Length >= e.Bytes.Length ? response.AsSpan(0, e.Bytes.Length).ToArray() : response;

    private static int DefaultStart(byte service) => service switch
    {
        0x22 or 0x2E => 4,
        0x31 => 5,
        0x19 => 3,
        _ => 2,
    };

    private StepOutcome RateText(IStepContext ctx, string text, string shown, bool isHex = false)
    {
        if (check.Target is not null) CommunicationBlocks.SetTarget(ctx, check.Target, isHex ? text.Replace(" ", "") : text);
        if (check.Expected is not null)
        {
            var expected = CommunicationBlocks.Text(check.Expected.Evaluate(new ContextResolver(ctx)));
            var ok = isHex
                ? string.Equals(expected.Replace(" ", ""), text.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
                : string.Equals(expected, text, StringComparison.Ordinal);
            return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, text, Message: ok ? shown : $"{shown}: „{text}“, erwartet „{expected}“");
        }
        if (check.Low is not null || check.High is not null)
        {
            if (CommunicationBlocks.ParseNumber(text) is not { } n) return new StepOutcome(ResultRating.Fail, text, Message: $"{shown}: kein Zahlenwert");
            return CommunicationBlocks.Rate(ctx, n, check.Low, check.High, check.Unit, shown);
        }
        return new StepOutcome(ResultRating.Pass, text, Message: shown);
    }
}

// ---------------------------------------------------------------------- J1939

internal sealed class J1939ReceiveAction(string resource, uint? pgn, byte? source, bool request, byte own, TimeSpan timeout, string? database, string? signal,
    CanSignal? raw, SignalCheck check) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        BusMessage? message = null;
        BusSignal? sig = null;
        if (signal is not null)
        {
            if (database is null) return StepOutcome.Error("Signal aus der Datenbank: Busdatenbank angeben.");
            try
            {
                (message, sig) = BusBlocks.Database(context, database).FindSignal(signal);
            }
            catch (BusDatabaseException ex)
            {
                return StepOutcome.Error(ex.Message);
            }
        }
        var group = pgn ?? message?.Pgn;
        if (group is not { } p) return StepOutcome.Error("PGN angeben (oder Signal aus der Datenbank).");
        J1939Message received;
        try
        {
            received = await context.UseDeviceAsync(resource, $"J1939 PGN 0x{p:X4}", async (adapter, channel, token) =>
            {
                var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
                can.DiscardInput(channel);
                if (request) await J1939.RequestAsync(can, channel, p, own, source ?? J1939.Global, token).ConfigureAwait(false);
                return await J1939.ReceiveAsync(can, channel, p, source, own, timeout, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (CommunicationTimeoutException ex)
        {
            return check.Timeout(ex);
        }
        return RateReceived(context, received, sig, message, p);
    }

    private StepOutcome RateReceived(IStepContext context, J1939Message received, BusSignal? sig, BusMessage? message, uint p)
    {
        var shown = received.ToString();
        if (check.CheckBytes(received.Data, shown) is { } mismatch) return mismatch;
        var coding = sig?.Coding ?? raw;
        if (coding is null) return check.RateData(context, received.Data, shown);
        double value;
        try
        {
            if (Unavailable(coding, received.Data))
                return new StepOutcome(ResultRating.Fail, BusBlocks.Hex(received.Data), Message: $"{sig?.Name ?? "Signal"}: nicht verfügbar (alle Bits 1) · {shown}");
            value = coding.Value(received.Data);
        }
        catch (ArgumentException ex)
        {
            return new StepOutcome(ResultRating.Fail, BusBlocks.Hex(received.Data), Message: $"{shown}: {ex.Message}");
        }
        return check.Rate(context, value, sig?.Describe(received.Data), sig?.Unit, coding.Factor, sig is null ? $"PGN 0x{p:X4}" : $"{message!.Name}.{sig.Name}");
    }

    private static bool Unavailable(CanSignal coding, byte[] data)
    {
        if (coding.IsFloat || coding.Signed || coding.Length < 2) return false;
        var all = coding.Length == 64 ? ulong.MaxValue : (1UL << coding.Length) - 1;
        return ((ulong)coding.Raw(data) & all) == all;
    }

}

internal sealed class J1939SendAction(string resource, uint? pgn, int priority, byte source, byte destination, string data, string? database, string? messageName,
    SignalValues values) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        byte[] bytes;
        uint group;
        try
        {
            (bytes, group) = PrepareSend(context);
        }
        catch (Exception ex) when (ex is BusDatabaseException or FormatException or InvalidCastException or OverflowException)
        {
            return StepOutcome.Error(ex.Message);
        }
        await context.UseDeviceAsync(resource, $"J1939 senden PGN 0x{group:X4}", async (adapter, channel, token) =>
        {
            await J1939.SendAsync(CommunicationBlocks.As<ICanAdapter>(adapter, "CAN"), channel, group, bytes, source, destination, priority, token).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
        var transport = bytes.Length <= 8 ? "" : destination == J1939.Global ? " (BAM)" : " (RTS/CTS)";
        return StepOutcome.Unrated(BusBlocks.Hex(bytes), $"PGN 0x{group:X4} von 0x{source:X2}{(J1939.IsPdu1(group) ? $" an 0x{destination:X2}" : "")}{transport}: {BusBlocks.Hex(bytes)}");
    }
    private (byte[] Bytes, uint Group) PrepareSend(IStepContext context)
    {
        byte[] bytes;
        uint group;
        if (messageName is not null)
        {
            if (database is null) throw new BusDatabaseException("Botschaft aus der Datenbank: Busdatenbank angeben.");
            var message = BusBlocks.Database(context, database).RequireMessage(messageName);
            bytes = string.IsNullOrWhiteSpace(data) ? message.Encode(values.Evaluate(message, context)) : BusBlocks.Bytes(data, context);
            group = pgn ?? message.Pgn;
        }
        else
        {
            bytes = BusBlocks.Bytes(data, context);
            group = pgn ?? throw new BusDatabaseException("PGN angeben.");
        }
        return (bytes, group);
    }

}

// ---------------------------------------------------------------------- LIN

internal static class LinFrames
{
    /// <summary>Frame-ID aus Zahl oder Name der LDF.</summary>
    public static (byte Id, BusMessage? Message) Resolve(string frame, BusDatabase? db)
    {
        var t = frame.Trim();
        if (db?.Message(t) is { } m && (m.Name.Equals(t, StringComparison.OrdinalIgnoreCase) || !char.IsDigit(t[0]))) return ((byte)m.Id, m);
        var ok = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            : byte.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        if (!ok || id > 0x3F) throw new BusDatabaseException($"LIN-Frame „{frame}“: ID 0x00–0x3F oder Name aus der LDF.");
        return (id, db?.Messages.FirstOrDefault(x => x.Id == id));
    }
}

internal sealed class LinReadAction(string resource, string frame, string? database, string? signal, int? length, LinChecksum checksum, TimeSpan timeout,
    CanSignal? raw, SignalCheck check) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        byte id;
        BusMessage? message;
        BusSignal? sig = null;
        try
        {
            var db = database is null ? null : BusBlocks.Database(context, database);
            (id, message) = LinFrames.Resolve(frame, db);
            if (signal is not null)
            {
                if (message is null) return StepOutcome.Error($"Signal „{signal}“: Frame nicht in der Datenbank (Busdatenbank angeben).");
                sig = message.RequireSignal(signal.Contains('.') ? signal[(signal.IndexOf('.') + 1)..] : signal);
            }
        }
        catch (BusDatabaseException ex)
        {
            return StepOutcome.Error(ex.Message);
        }
        var bytes = length ?? message?.Length ?? 8;
        if (Lin.Validate(id, bytes) is { } error) return StepOutcome.Error(error);
        byte[] data;
        try
        {
            data = await context.UseDeviceAsync(resource, $"LIN lesen 0x{id:X2}", (adapter, channel, token)
                => CommunicationBlocks.As<ILinAdapter>(adapter, "LIN").RequestAsync(channel, id, bytes, checksum, timeout, token)).ConfigureAwait(false);
        }
        catch (CommunicationTimeoutException ex)
        {
            return check.Timeout(ex);
        }
        return RateReceived(context, data, message, sig, id);
    }

    private StepOutcome RateReceived(IStepContext context, byte[] data, BusMessage? message, BusSignal? sig, byte id)
    {
        var name = message?.Name ?? $"0x{id:X2}";
        var shown = $"LIN {name}: {BusBlocks.Hex(data)}";
        if (check.CheckBytes(data, shown) is { } mismatch) return mismatch;
        var coding = sig?.Coding ?? raw;
        if (coding is null) return check.RateData(context, data, shown);
        try
        {
            return check.Rate(context, coding.Value(data), sig?.Describe(data), sig?.Unit, coding.Factor, sig is null ? $"LIN {name}" : $"{name}.{sig.Name}");
        }
        catch (ArgumentException ex)
        {
            return new StepOutcome(ResultRating.Fail, BusBlocks.Hex(data), Message: $"{shown}: {ex.Message}");
        }
    }

}

internal sealed class LinSendAction(string resource, string frame, string? database, string data, SignalValues values, LinChecksum checksum) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        byte id;
        BusMessage? message;
        byte[] bytes;
        try
        {
            var db = database is null ? null : BusBlocks.Database(context, database);
            (id, message) = LinFrames.Resolve(frame, db);
            if (!string.IsNullOrWhiteSpace(data)) bytes = BusBlocks.Bytes(data, context);
            else if (message is not null) bytes = message.Encode(values.Evaluate(message, context));
            else return StepOutcome.Error("Signalwerte brauchen einen Frame aus der Datenbank.");
        }
        catch (Exception ex) when (ex is BusDatabaseException or FormatException or InvalidCastException or OverflowException)
        {
            return StepOutcome.Error(ex.Message);
        }
        if (Lin.Validate(id, bytes.Length) is { } error) return StepOutcome.Error(error);
        await context.UseDeviceAsync(resource, $"LIN senden 0x{id:X2}", async (adapter, channel, token) =>
        {
            await CommunicationBlocks.As<ILinAdapter>(adapter, "LIN").SendAsync(channel, id, bytes, checksum, token).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
        var decoded = message is null ? "" : " (" + string.Join(", ", message.Decode(bytes).Select(d => $"{d.Signal.Name}={d.Text ?? d.Value.ToString("0.###", CultureInfo.InvariantCulture)}")) + ")";
        return StepOutcome.Unrated(BusBlocks.Hex(bytes), $"LIN {message?.Name ?? $"0x{id:X2}"}: {BusBlocks.Hex(bytes)}{decoded}");
    }
}

// ---------------------------------------------------------------------- J1939 Address Claim

internal sealed class J1939ClaimAction(string resource, byte address, ulong name, bool query, string? target) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        try
        {
            var participants = await context.UseDeviceAsync(resource, $"J1939 Adresse 0x{address:X2}", async (adapter, channel, token) =>
            {
                var can = CommunicationBlocks.As<ICanAdapter>(adapter, "CAN");
                IReadOnlyList<(byte Address, ulong Name)> list = query
                    ? await J1939.RequestClaimsAsync(can, channel, address, TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false)
                    : [];
                await J1939.ClaimAddressAsync(can, channel, address, name, token).ConfigureAwait(false);
                return list;
            }).ConfigureAwait(false);
            if (target is not null && query)
                CommunicationBlocks.SetTarget(context, target, string.Join("; ", participants.Select(x => $"0x{x.Address:X2}=0x{x.Name:X16}")));
            return StepOutcome.Unrated($"0x{name:X16}", $"Adresse 0x{address:X2} beansprucht{(query ? $" ({participants.Count} Teilnehmer gesehen)" : "")}");
        }
        catch (CommunicationException ex)
        {
            return new StepOutcome(ResultRating.Fail, $"0x{address:X2}", Message: ex.Message);
        }
    }
}

// ---------------------------------------------------------------------- LIN-Fahrplan

internal sealed class LinScheduleAction(string resource, string? database, string table, int runs, bool missingFail) : IStepAction
{
    private sealed record Entry(byte Id, string Name, byte[]? Send, int? Length, int GapMs);

    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        BusDatabase? db;
        List<Entry> entries;
        try
        {
            db = database is null ? null : BusBlocks.Database(context, database);
            entries = Parse(db, context);
        }
        catch (BusDatabaseException ex)
        {
            return StepOutcome.Error(ex.Message);
        }
        if (entries.Count == 0) return StepOutcome.Error("LIN-Fahrplan: keine Zeilen.");

        var missing = new List<string>();
        var count = await context.UseDeviceAsync(resource, "LIN-Fahrplan", async (adapter, channel, token) =>
        {
            var lin = CommunicationBlocks.As<ILinAdapter>(adapter, "LIN");
            var done = 0;
            for (var run = 0; run < runs; run++)
                foreach (var e in entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (e.Send is { } data) await lin.SendAsync(channel, e.Id, data, LinChecksum.Enhanced, token).ConfigureAwait(false);
                    else
                    {
                        try
                        {
                            await lin.RequestAsync(channel, e.Id, e.Length ?? 8, LinChecksum.Enhanced, TimeSpan.FromMilliseconds(Math.Max(10, e.GapMs)), token).ConfigureAwait(false);
                        }
                        catch (CommunicationTimeoutException)
                        {
                            if (!missing.Contains(e.Name)) missing.Add(e.Name);
                        }
                        catch (CommunicationException) // falsche Länge o. Ä.: als auffälliger Frame vermerken, Fahrplan läuft weiter
                        {
                            if (!missing.Contains(e.Name)) missing.Add(e.Name);
                        }
                    }
                    done++;
                    if (e.GapMs > 0) await context.Clock.Delay(TimeSpan.FromMilliseconds(e.GapMs), token).ConfigureAwait(false);
                }
            return done;
        }).ConfigureAwait(false);

        if (missing.Count > 0)
            return new StepOutcome(missingFail ? ResultRating.Fail : ResultRating.NResult, missing.Count,
                Message: $"{count} Frames; keine Antwort auf {string.Join(", ", missing)}");
        return StepOutcome.Unrated(count, $"{count} Frames in {runs} Durchlauf/Durchläufen");
    }

    private static (string Frame, byte[]? Send, int Gap, int? Length) ParseLine(string raw)
    {
        var line = raw;
        byte[]? send = null;
        var eq = line.IndexOf('=');
        string frame;
        var gap = 10;
        int? length = null;
        if (eq > 0)
        {
            frame = line[..eq].Trim();
            var rest = line[(eq + 1)..].Trim();
            var sp = rest.LastIndexOf(' ');
            if (sp > 0 && int.TryParse(rest[(sp + 1)..], out var g)) { gap = g; rest = rest[..sp].Trim(); }
            send = CommText.ParseHex(rest);
        }
        else
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            frame = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var g)) gap = g;
        }
        var colon = frame.LastIndexOf(':');
        if (colon > 0 && int.TryParse(frame[(colon + 1)..], out var len)) { length = len; frame = frame[..colon]; }
        return (frame, send, gap, length);
    }

    private static bool ExpandFrameGroup(BusDatabase? db, string frame, byte[]? send, int gap, List<Entry> list)
    {
        if (send is null && db?.FrameGroup(frame) is { } group)
        {
            foreach (var member in group.Members)
            {
                var (mid, mmsg) = LinFrames.Resolve(member, db);
                list.Add(new Entry(mid, $"{group.Name}:{mmsg?.Name ?? member}", null, mmsg?.Length, gap));
            }
            return true;
        }
        return false;
    }

    private List<Entry> Parse(BusDatabase? db, IStepContext ctx)
    {
        var list = new List<Entry>();
        foreach (var raw in table.Split(['\n', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var (frame, send, gap, length) = ParseLine(raw);
            // Ereignisgesteuerte/sporadische Frames aus der LDF in ihre Einzelframes auflösen
            if (ExpandFrameGroup(db, frame, send, gap, list)) continue;
            var (id, message) = LinFrames.Resolve(frame, db);
            if (send is null) length ??= message?.Length;
            if (Lin.Validate(id, send?.Length ?? length ?? 8) is { } error) throw new BusDatabaseException($"LIN-Fahrplan „{frame}“: {error}");
            list.Add(new Entry(id, message?.Name ?? $"0x{id:X2}", send, length, gap));
        }
        _ = ctx;
        return list;
    }
}

// ---------------------------------------------------------------------- UDS-Sicherheitszugang

internal sealed class UdsSecurityAction(string resource, DiagSpec settings, byte level, int method, byte[] constant, Expression? expr,
    TimeSpan p2, TimeSpan p2Star, string? target) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        byte[] Key(byte[] seed)
        {
            if (method != 4) return SecurityKey.Compute((SecurityKey.Method)method, seed, constant);
            var key = new byte[seed.Length];
            for (var i = 0; i < seed.Length; i++)
            {
                var resolver = new ContextResolver(context, new Dictionary<string, object?> { ["s"] = (double)seed[i], ["i"] = (double)i });
                var value = expr!.Evaluate(resolver);
                key[i] = unchecked((byte)(long)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
            }
            return key;
        }

        byte[] computed;
        try
        {
            computed = await context.UseDeviceAsync(resource, $"UDS Sicherheitszugang 0x{level:X2}", (adapter, channel, token)
                => Uds.SecurityAccessAsync(settings.Create(adapter, channel), level, Key, p2, p2Star, token)).ConfigureAwait(false);
        }
        catch (UdsNegativeResponseException ex)
        {
            return new StepOutcome(ResultRating.Fail, $"7F 27 {ex.Code:X2}", Message: $"Sicherheitszugang 0x{level:X2}: {ex.Message}");
        }
        catch (CommunicationTimeoutException ex)
        {
            return StepOutcome.Error($"Sicherheitszugang 0x{level:X2}: {ex.Message}");
        }
        catch (CommunicationException ex)
        {
            return StepOutcome.Error($"Sicherheitszugang 0x{level:X2}: {ex.Message}");
        }
        if (target is not null) CommunicationBlocks.SetTarget(context, target, computed.Length == 0 ? "" : Convert.ToHexString(computed));
        return StepOutcome.Unrated(computed.Length == 0 ? "frei" : Convert.ToHexString(computed),
            computed.Length == 0 ? $"Stufe 0x{level:X2} bereits freigeschaltet" : $"Stufe 0x{level:X2} freigeschaltet (Schlüssel {BusBlocks.Hex(computed)})");
    }
}


// ---------------------------------------------------------------------- DoIP-Fahrzeugsuche

internal sealed class DoIpDiscoverAction(string host, int port, TimeSpan timeout, string? expectedVin, ushort? expectedAddress, int? minimum, string? target)
    : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        IReadOnlyList<DoIpVehicle> vehicles;
        try
        {
            using var channel = new UdpDatagramChannel(host, port);
            vehicles = await DoIpDiscovery.DiscoverAsync(channel, timeout, 0x02, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or CommunicationException)
        {
            return StepOutcome.Error("DoIP-Fahrzeugsuche: " + ex.Message);
        }
        var list = string.Join("; ", vehicles.Select(v => $"{v.Vin}@0x{v.LogicalAddress:X4}"));
        if (target is not null) CommunicationBlocks.SetTarget(context, target, list);
        var problems = new List<string>();
        if (expectedVin is not null && vehicles.All(v => !string.Equals(v.Vin, expectedVin.Trim(), StringComparison.OrdinalIgnoreCase)))
            problems.Add($"VIN {expectedVin.Trim()} nicht gefunden");
        if (expectedAddress is { } addr && vehicles.All(v => v.LogicalAddress != addr))
            problems.Add($"Adresse 0x{addr:X4} nicht gefunden");
        if (minimum is { } min && vehicles.Count < min)
            problems.Add($"{vehicles.Count} statt mindestens {min} Fahrzeuge");
        var message = vehicles.Count == 0 ? "Keine Antwort auf die Fahrzeugsuche" : $"{vehicles.Count} Fahrzeug(e): {list}";
        if (problems.Count > 0) return new StepOutcome(ResultRating.Fail, vehicles.Count, Message: $"{message} – {string.Join(", ", problems)}");
        var rated = expectedVin is not null || expectedAddress is not null || minimum is not null;
        return new StepOutcome(rated ? ResultRating.Pass : ResultRating.NResult, vehicles.Count, Message: message);
    }
}


// ---------------------------------------------------------------------- OPC UA

internal sealed class OpcUaAction(string resource, int action, string? node, Expression? value, string? objectNode, string? method, string? arguments,
    TimeSpan timeout, Expression? expected, string? target, Expression? low, Expression? high, string unit) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        try
        {
            return await context.UseDeviceAsync(resource, "OPC UA " + OpcActions(action), async (adapter, channel, token) =>
            {
                var opc = adapter is IOpcUaClient d ? d : adapter is IOpcUaDevice dev ? dev.OpcUa(channel)
                    : throw new CommunicationException($"Gerät „{adapter.Name}“ ist kein OPC-UA-Gerät.");
                await opc.ConnectAsync(token).ConfigureAwait(false);
                switch (action)
                {
                    case 1: // Schreiben
                        if (node is null) return StepOutcome.Error("OPC UA: Knoten angeben.");
                        var w = value?.Evaluate(new ContextResolver(context)) ?? throw new CommunicationException("OPC UA Schreiben: Wert angeben.");
                        await opc.WriteAsync(node, w, token).ConfigureAwait(false);
                        return StepOutcome.Unrated(CommunicationBlocks.Text(w), $"{node} ← {CommunicationBlocks.Text(w)}");
                    case 2: // Methode
                        if (objectNode is null || method is null) return StepOutcome.Error("OPC UA Methode: Objekt- und Methodenknoten angeben.");
                        var args = string.IsNullOrWhiteSpace(arguments) ? new List<object?>()
                            : arguments.Split([';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Scalar).ToList();
                        var outputs = await opc.CallAsync(objectNode, method, args, token).ConfigureAwait(false);
                        var joined = string.Join("; ", outputs.Select(CommunicationBlocks.Text));
                        return Evaluate(context, outputs.Count == 1 ? outputs[0] : joined, $"{method} → {joined}");
                    case 3: // Abonnieren
                        if (node is null) return StepOutcome.Error("OPC UA: Knoten angeben.");
                        return await WaitForSubscriptionAsync(opc, context, token).ConfigureAwait(false);
                    default: // Lesen
                        if (node is null) return StepOutcome.Error("OPC UA: Knoten angeben.");
                        var read = await opc.ReadAsync(node, token).ConfigureAwait(false);
                        return Evaluate(context, read, $"{node} = {CommunicationBlocks.Text(read)}");
                }
            }).ConfigureAwait(false);
        }
        catch (OpcUaException ex)
        {
            return StepOutcome.Error(ex.Message);
        }
    }

    private async Task<StepOutcome> WaitForSubscriptionAsync(IOpcUaClient opc, IStepContext context, CancellationToken token)
    {
        await using (var sub = await opc.SubscribeAsync(node!, 100, token).ConfigureAwait(false))
        {
            try
            {
                var changed = await sub.WaitForChangeAsync(timeout, token).ConfigureAwait(false);
                return Evaluate(context, changed, $"{node} geändert auf {CommunicationBlocks.Text(changed)}");
            }
            catch (CommunicationTimeoutException ex)
            {
                return StepOutcome.Error(ex.Message);
            }
        }
    }

    private static string OpcActions(int a) => a switch { 1 => "Schreiben", 2 => "Methode", 3 => "Abo", _ => "Lesen" };

    private static object? Scalar(string text)
    {
        if (text.StartsWith('"') && text.EndsWith('"') && text.Length >= 2) return text[1..^1];
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return text;
    }

    private StepOutcome Evaluate(IStepContext ctx, object? read, string label)
    {
        if (target is not null) CommunicationBlocks.SetTarget(ctx, target, read is string s ? s : read);
        if (expected is not null)
        {
            return EvaluateExpected(ctx, read, label);
        }
        if ((low is not null || high is not null) && read is double or long)
            return CommunicationBlocks.Rate(ctx, Convert.ToDouble(read, CultureInfo.InvariantCulture), low, high, unit, label);
        return new StepOutcome(ResultRating.NResult, read, string.IsNullOrWhiteSpace(unit) ? null : unit, Message: label);
    }
    private StepOutcome EvaluateExpected(IStepContext ctx, object? read, string label)
    {
        var exp = expected!.Evaluate(new ContextResolver(ctx));
        var ok = read is double or long && exp is double or long
            ? Math.Abs(Convert.ToDouble(read, CultureInfo.InvariantCulture) - Convert.ToDouble(exp, CultureInfo.InvariantCulture)) < 1e-9
            : string.Equals(CommunicationBlocks.Text(read), CommunicationBlocks.Text(exp), StringComparison.Ordinal);
        return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, read, string.IsNullOrWhiteSpace(unit) ? null : unit,
            Message: ok ? label : $"{label}, erwartet {CommunicationBlocks.Text(exp)}");
    }

}
