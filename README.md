# Testumgebung (Etappen 0–10, Pilotvorbereitung, Ausbau)

GitHub-Einrichtung, automatische Wiki-Dokumentation und signierte MSIX-Releases: [GitHub-Anleitung](docs/github.md). Die Dokumentation wird aus `docs/` erzeugt; das Repository bleibt zunächst privat.

Versionen, Release Notes direkt auf GitHub und der Abschluss über ein Release-Issue:
[Release-Ablauf](docs/release-ablauf.md). Änderungen an `main` erfolgen nach der
Einrichtung ausschließlich über Pull Requests.

Lizenz- und Veröffentlichungsprüfung: [Prüfbericht](docs/licensing/pruefbericht.md), [vollständiges Abhängigkeitsinventar](docs/licensing/inventar.md), [Drittanbieterhinweise](THIRD-PARTY-NOTICES.md) und [Status der eigenen Lizenz](LICENSE-STATUS.md). [Vorbereitung der öffentlichen GitHub-Freigabe](docs/github-veroeffentlichung.md) beschreibt die technischen Maßnahmen und verbleibenden Vertragsfragen. [Datenschutz](docs/datenschutz.md), [Sicherheitsmeldungen](SECURITY.md) und [Beiträge](CONTRIBUTING.md) sind gesondert dokumentiert.

Codeabdeckung und Wartbarkeit je Modul: `Report-Coverage.cmd` erstellt einen datierten HTML-Bericht. Anleitung und Messgrenzen: [Testabdeckung und Wartbarkeit](docs/testabdeckung-und-wartbarkeit.md).

Aktueller Betriebsmodus: standardmäßig Produktivbetrieb; lokale simulierte Entwicklung ausdrücklich mit `--development` starten. Details zu Zugang, Fehlerprotokollen und externen Laufzeitdateien: [Betriebsmodus](docs/betriebsmodus-und-laufzeit.md). Der mitgelieferte C++-Compiler und die native GDB-Inspektion sind unter [Portable C++-Toolchain](docs/portable-cpp-toolchain.md) beschrieben.

Umsetzung nach `info_/Software_Umsetzungsplan.md`:

* **Etappe 0–2:** Ausführungs-Engine mit Simulation und automatisierten Abnahmetests.
* **Etappe 3:** WPF-Oberfläche – Werkeransicht, Ablaufeditor mit Bausteinpalette und Eigenschaften,
  Werkerinteraktion, Debugger. Tests lassen sich ohne Quellcode erstellen, speichern (JSON) und ausführen.
* **Etappe 4:** CodeBlocks in C# und Python, jeweils in einem isolierten Worker-Prozess: CodeBlock im Ablauf
  (Zugriff auf Ordnervariablen), Baustein-Bibliothek mit typisierter Schnittstelle, „Build prüfen“ mit Compilerdiagnosen,
  Testausführung, „In Baustein umwandeln“. Details: `docs/adr/0002-codeblocks-und-worker.md`.
* **Etappe 4 (Nachtrag):** C++-CodeBlocks (eigener Worker, stabile C-Schnittstelle), LabVIEW-Bausteine als Prototyp
  (gebaute VI-DLL über C++-Glue), grafische Bausteine „Aus Gruppe erstellen“, Code-/Parameter-Patch während der Pause
  (D06). Details: `docs/adr/0003-cpp-labview-gruppenbausteine-patch.md`.
* **Etappe 5:** Binäre Ergebnisdatei `.tres` je Prüflingstest mit Reader, unabhängige Filter für Anzeige/Datei/Datenbank,
  Kurven (Mehrkanal, gemeinsame Zeitbasis) als Chunks, lokale dauerhafte Outbox mit Hintergrund-Wiederholung.
  Details: `docs/adr/0004-ergebnisdatei-filter-outbox.md`.
* **Etappe 6:** Datenbankadapter für SQL Server, PostgreSQL, MySQL/MariaDB und SQLite (gemeinsames Schema, automatische
  Migration, idempotent je Run), MQTT-Meldeweg mit TLS (best effort), Einstellungsseite mit Verbindungstest und
  Vertragsprüfung. Details: `docs/adr/0005-datenbanken-und-mqtt.md`, Schemaskripte unter `docs/sql/`.
* **Etappe 7:** Anmeldung mit Firmenkonto (Entra ID über MSAL/WAM, bis zur Registrierung simuliert), Rechte
  „Entwickeln“ und „Freigeben“ je Prüfplatz, unveränderliche Release-Pakete mit Hashes in der Datenbank, Workflow
  Entwurf → Entwicklungsprüfung → Freigabe, Audit-Log mit Hash-Kette, Werkerbetrieb nur mit freigegebenen Versionen,
  Versionswechsel erst am nächsten Prüfling. Details: `docs/adr/0006-identitaet-und-freigabe.md`,
  Einrichtung: `docs/entra-registrierung.md`.
* **Etappe 8:** Kurven auf drei Wegen – Dateiimport (CSV/Text, COMTRADE IEEE C37.111, Excel .xlsx, Ergebnisdatei .tres)
  mit Spalten-, Einheiten- und Zeitbasiszuordnung, aufgenommene Messkurve als neue Ausgabekurve, Editor mit Sinus, Rampe,
  Sprung und Formel. Vorschau mit Kanalauswahl, Zoom und Cursor; Zuschnitt, Skalierung, Zeitverschiebung und
  Abtastratenänderung nur ausdrücklich und mit Historie; unveränderliche Kurvenversionen im Projekt und Release-Paket;
  Ausgabe getrennt in Laden, Übertragen, Starten, Stoppen mit Geräteeignungsprüfung (keine stille Skalierung oder
  Abtastratenänderung); Kurvenanzeige L1–L3 mit Cursor und Effektivwerten in der Werkeransicht.
  Details: `docs/adr/0007-kurven.md`.
* **Etappe 9:** Parallelbetrieb – Prüfplatzkonfiguration je Rechner (`%ProgramData%\Testumgebung\pruefplaetze.json`):
  konfigurierbare Prüfplätze, lokale und gemeinsame Geräte, Zuordnung Name → Gerät/Kanal. Gesperrt wird das Gerät, nicht
  der Name; Zyklusrisiko verschachtelter Sperren wird statisch erkannt und der Start verweigert, Verklemmungen zur
  Laufzeit ergeben ERROR statt Hängen; Zugriffsprotokoll weist nach, dass kein widersprüchlicher Gerätezugriff erfolgt.
  Details: `docs/adr/0008-parallelbetrieb.md`.
* **Etappe 10:** Reale Geräte (Treiberprototypen, ohne vorhandene Hardware verifiziert): Keysight/Agilent
  Multimeter 3446xA/34401A, Netzgeräte E36300/E3631A, AC-Quellen AC6800B, Generatoren 33500B/33600A, Oszilloskope
  InfiniiVision (SCPI über LAN-Socket oder VISA) sowie NI-DAQmx (USB/PCIe/CompactDAQ, C-API). Gerät testen, Geräte
  suchen, Kanalauswahl aus dem Treiber, sicherer Zustand nach jedem Lauf, Abbruch während Gerätezugriffen,
  Laborsimulatoren und DAQmx-Nachbildung zum Ausprobieren. Details: `docs/adr/0009-reale-geraete.md`,
  Geräteliste und Einrichtung: `docs/geraete.md`.
* **Kommunikation:** Prüflingskommunikation über RS-232/RS-485, TCP, UDP, Modbus RTU/TCP und CAN/CAN FD (PEAK, Kvaser,
  virtueller Bus) mit Bausteinen Senden/Empfangen/Abfrage, Modbus lesen/schreiben, CAN senden/empfangen, simuliertem
  Prüfling nach Regeln und denselben Sperren wie Messgeräte. Details: `docs/adr/0011-kommunikation.md`, `docs/geraete.md`.
* **Busdatenbanken, Diagnose, LIN:** DBC-/LDF-Import ins Projekt, Bausteine „CAN-Signal lesen“/„CAN-Botschaft senden“
  (Multiplex, Wertetabellen, Gleitkomma), „UDS-Diagnose“ über ISO-TP, „J1939 empfangen/senden“ (Anforderung, BAM, RTS/CTS),
  „LIN lesen/senden“; Vector XL und PEAK PLIN als Geräte; Kommunikation aus Python-, C#- und C++-CodeBlocks über die
  zentrale Vermittlung. Details: `docs/adr/0013-busdatenbanken-diagnose-lin.md`.
* **Auswertung aus der Datenbank, weitere Meldewege, Diagnoseausbau:** Auswertung und Prüfberichte auch aus SQL-/simulierter
  Datenbank (serverseitige Filter, ohne Kurven); Zurückkopieren aus dem Wiederherstellungsordner; Meldewege HTTP-Webhook und
  OPC UA neben MQTT; UDS über DoIP (Ethernet) und LIN sowie ISO-TP mit erweiterter Adressierung; J1939-Adressvergabe (Address
  Claim), LIN-Fahrplan. Details: `docs/adr/0014-auswertung-db-meldewege-diagnose-doip.md`.
* **Sicherheitszugang, Dreiphasenquelle, DoIP-Suche/TLS, CAN FD (Vector), DB-Kurven:** Baustein „UDS-Sicherheitszugang“
  (Seed/Key, Verfahren wählbar); dreiphasige AC-Versorgung aus drei Quellen (L1/L2/L3); DoIP-Fahrzeugsuche (UDP) und TLS am
  TCP-Client; CAN FD über Vector XL; LDF-Ereignis-/sporadische Frames; Kurven aus der Datenbank im Reader. Details:
  `docs/adr/0015-sicherheitszugang-dreiphasig-doip-suche-fd.md`.
* **Run-Patch Teil 2 und OPC UA (Feldzugriff):** Run-Patch im pausierten Lauf nun auch für Bibliotheksbausteine (Quelltext/Name,
  wirkt auf folgende Aufrufe) und die Ablaufstruktur nicht aktiver Gruppen (Löschen/Verschieben); OPC UA als Gerät und Baustein
  „OPC UA“ (Lesen/Schreiben, Methodenaufrufe, Abonnieren) sowie aus CodeBlocks (`opcua_read/write/call`) – gegen einen
  in-Prozess-Simulationsserver getestet, realer Client über den OPC-Foundation-Stack in der Windows-Anwendung. Details:
  `docs/adr/0016-run-patch-teil2-und-opc-ua.md`.
* **Quellcode-Debugger für CodeBlocks:** einheitliche Oberfläche für alle Sprachen (Haltepunkt je Zeile, Einzelschritt,
  „bei Eintritt halten“, Variablen-Anzeige). Volles Zeilen-Stepping mit Variablen-Inspektion bei Python (`sys.settrace`),
  C# (Roslyn-Instrumentierung) und C++ (Quelltext-Instrumentierung; Variablen für einzeilige Zuweisungen); LabVIEW bleibt
  reduziert (Halt bei Eintritt/Austritt, da kompilierte VI ohne Quelltext). Details:
  `docs/adr/0017-quellcode-debugger-fremdsprachen.md`.
* **Auswertung & Editor:** Auswertung über Ergebnisdateien (Ausbeute, FPY, Pareto, Cpk, Histogramm) mit PDF/CSV, Prüfbericht
  je Prüfling als PDF; Editor mit Rückgängig/Wiederholen, Kopieren/Einfügen/Duplizieren, Suche, Versionsvergleich; offene
  Punkte aus Abschnitt 15 entschieden (Zeitspanne in der Iteration, Einzel-/Prozedurschritt, Abmelden in Pause,
  Entra-App-Rollen). Details: `docs/adr/0012-auswertung-editor-offene-punkte.md`.
* **Plan-Lücken geschlossen:** Auswahl- und Auftragsablauf je Prüfplatz (Abschnitt 5: manuell, Kennungsregeln, eigener
  Ablauf mit Baustein „Test auswählen“ **oder einem einzigen CodeBlock** über `SelectTest(...)`/`select_test(...)`, ADR 0018,
  Beispiel `docs/beispiele/auswahl-codeblock.tproj.json`; nur eindeutige Zuordnung startet; ein Prüfling je Zuordnung mit
  Vorbelegung von Kennung/Auftrag/Prüfparametern; aktiv bis Werkerabbruch) und konfigurierbare Werkeransicht (Abschnitt 4:
  Fortschritt oben/links/unten, Übersicht/Protokoll ein/aus, alle oder nur auffällige Messwerte).
* **Pilot (Etappe 11, vorbereitet):** Zwischenaufzeichnung je Prüfling und Wiederherstellung unvollständiger Läufe nach
  Absturz/Stromausfall, Systemprüfung, Sicherung/Wiederherstellung der Rechnerkonfiguration, Installer (Inno Setup) mit
  `build/publish.ps1`, Handbücher (`docs/handbuch/`), Abnahmeprotokoll (`docs/pilot/`). Details:
  `docs/adr/0010-auswahl-werkeransicht-pilot.md`, Installation: `docs/installation.md`.

## Anwendung starten

```powershell
dotnet run --project src/Testumgebung.Desktop
```

* Beim Start ist das Beispielprojekt „AC-Prüfung“ geladen (Simulation, zwei Prüfplätze).
* **Start** unten rechts startet den gewählten Prüfplatz; die Werkerinteraktionen erscheinen in der Mitte.
* **Anmelden** (oben rechts): ohne Zugangsdatei simuliert mit allen Rechten (Entwicklungsmodus); mit
  `%ProgramData%\Testumgebung\zugang.json` über Entra ID und nur mit den dort vergebenen Rechten.
* **Releases** (Entwickeln oder Freigeben): Entwurf aus dem Editorstand erstellen, Entwicklungsprüfung (Integrität,
  Validierung, Build der Codeartefakte), ausdrücklich freigeben; Paketinhalt mit Hashes, Audit-Log, Versionen der Prüfplätze.
* **Werkerbetrieb:** Start nur mit freigegebener Version. Entwickler können „Entwurf testen“ wählen (Kopfzeile);
  „Simulation starten“ im Editor nutzt den Entwurf. Neue Freigaben gelten ab dem nächsten Prüfling.
* Haltepunkt: Karte wählen, **F9**; danach **Simulation starten**.
* **CodeBlocks:** in der Palette „CodeBlock“ einfügen, Sprache wählen, Code schreiben, „Build prüfen“.
  Die Bibliothek liegt unter Entwicklung → CodeBlocks.
* Python-Interpreter: Standard ist die mitgelieferte portable Python-Laufzeit; ein anderes Environment über die Umgebungsvariable
  `TESTUMGEBUNG_PYTHON` (z. B. `C:\venvs\pruef\Scripts\python.exe`).
* C++-Compiler: automatisch MSVC (über `vswhere`/`vcvars64.bat`), sonst `clang++`, sonst `g++`. Fest vorgeben mit
  `TESTUMGEBUNG_CXX` (`msvc`, `clang++`, `g++` oder voller Pfad). Build-Cache: `%TEMP%\Testumgebung\cpp`,
  änderbar über `TESTUMGEBUNG_CPP_CACHE`.
* **LabVIEW (Prototyp):** Baustein „LabVIEW“ in der Bibliothek; im Quelltextfeld `vi`, `dll`, `funktion`, `aufruf`
  angeben, Ports entsprechen dem Connector. Ohne gebaute DLL bzw. LabVIEW-Runtime meldet „Build prüfen“ bzw. die
  Ausführung eine klare Diagnose (ERROR), nichts stürzt ab.
* **Grafischer Baustein:** Gruppe im Editor wählen → Eigenschaften → „Aus Gruppe erstellen“. Ports werden aus den
  verwendeten Variablen erkannt, der Körper erscheint als eigener Abschnitt im Editorbaum.
* **Ergebnisse:** Am Ende jedes Prüflings wird automatisch eine Datei geschrieben (auch bei WERROR), standardmäßig nach
  `Dokumente\Testumgebung\Ergebnisse`, änderbar über `TESTUMGEBUNG_ERGEBNISSE`. Scheitert das Speichern, wird deutlich
  angezeigt und in `%LOCALAPPDATA%\Testumgebung\Wiederherstellung` gesichert. Entwicklung → **Ergebnisse** zeigt die
  Dateien mit Kopf, Ereignissen und Kurvenplot, dazu Outbox und Meldungen je Run.
* **Einstellungen** (Entwicklung → Einstellungen, Datei `%LOCALAPPDATA%\Testumgebung\einstellungen.json`):
  Ergebnisordner, Datenbank (Keine, Simuliert, SQL Server, PostgreSQL, MySQL/MariaDB, SQLite) mit Verbindungsstring,
  „Verbindung testen“ und „Vertragsprüfung“ (schreibt und entfernt Test-Runs `VERTRAG-…`), Outbox-Parameter, MQTT
  (Broker, Port, TLS, Firmen-CA, Benutzer/Passwort, Topic, Testmeldung). „Speichern und übernehmen“ wirkt sofort, ohne
  Neustart. Verbindungsstring und Passwort werden per DPAPI für den Windows-Benutzer verschlüsselt.
* **Datenbank:** Das Schema wird beim ersten Zugriff angelegt (`tu_schema_version`, Tabellen `tu_runs`, `tu_definitions`,
  `tu_step_executions`, `tu_measurements`, `tu_waveform_chunks`, `tu_operator_inputs`, `tu_development_events`). Wer es
  lieber vorab vom DBA anlegen lässt: `docs/sql/schema-<provider>.sql`. Ohne Einstellungsdatei bleibt die simulierte
  Datenbank aktiv (Ordner `%LOCALAPPDATA%\Testumgebung\SimulierteDatenbank`, auf der Seite Ergebnisse „offline“ schaltbar);
  `TESTUMGEBUNG_DB=aus` schaltet sie ab. Die Outbox liegt unter `%LOCALAPPDATA%\Testumgebung\Outbox` und wird beim
  nächsten Öffnen der Anwendung fortgesetzt.
* **MQTT:** Je Run-Ereignis eine JSON-Nachricht (runId, dutId, event, time, attempt, error, station) an das Topic
  (Standard `testumgebung/{station}/runs`), QoS 0, nicht retained, ein Sendeversuch, keine Zwischenspeicherung.
* **Filter:** Gruppe oder Schritt wählen → Eigenschaften → „Speicherung & Anzeige“ (Erben/An/Aus je Ziel, Kurven separat).
* **Run-Patch (D06):** im Debugger bei Pause einen nicht aktiven Knoten wählen, Parameter/Code/Name/Bedingung ändern, „Prüfen“
  und „Anwenden“. Zweiter Teil: auch Bibliotheksbausteine (Quelltext/Name, wirkt auf folgende Aufrufe) und die Ablaufstruktur
  nicht aktiver Gruppen (Löschen/Verschieben). Gilt nur für den laufenden Durchlauf, wird nummeriert protokolliert; das Projekt
  bleibt unverändert.

## Bauen und testen

Voraussetzung: .NET 10 SDK.

```powershell
dotnet build Testumgebung.sln
dotnet test  Testumgebung.sln
```

Datenbank-Vertragstests gegen echte Server laufen zusätzlich, wenn Verbindungsstrings gesetzt sind (sonst übersprungen):

```powershell
$env:TESTUMGEBUNG_TEST_MSSQL    = "Server=(localdb)\MSSQLLocalDB;Database=TuTest;Integrated Security=true;TrustServerCertificate=true"
$env:TESTUMGEBUNG_TEST_POSTGRES = "Host=localhost;Database=tu_test;Username=postgres;Password=…"
$env:TESTUMGEBUNG_TEST_MYSQL    = "Server=localhost;Database=tu_test;User ID=root;Password=…"
dotnet test Testumgebung.sln --filter DatabaseAdapterTests
```

Die Datenbanken müssen existieren (z. B. `CREATE DATABASE tu_test`); die Tabellen legt der Test selbst an.

## Aufbau

```text
src/
  Testumgebung.Contracts   ResultRating, RunStatus, EngineState, VarType/TypedValue, IClock (+VirtualClock),
                           IResourceManager, IHardwareAdapter, ResourceBinding
  Testumgebung.Domain      GroupDefinition/StepDefinition/TestDefinition, Repetition, Scope (Namenssuche, ../),
                           typisierte Ausdruckssprache, PendingResult/ResultAccumulator, IStepAction/IStepContext
  Testumgebung.Hardware    ResourceManager (atomar, FIFO-fair, Referenzzählung, Timeout), SimulationAdapter, HardwareRegistry
  Testumgebung.Engine      TestRun (Ausführungsstack, Wiederholung, Exit/Abbruch, Haltepunkte, gezielte Ausführung),
                           DebugController, Ausführungsereignisse, Bausteinkatalog (Blocks/BlockCatalog)
  Testumgebung.Persistence Projektdateien (JSON, *.tproj.json), Beispielprojekt, Simulationsaufbau,
                           Results/: Recorder, Filter, Ergebnisdatei .tres (MessagePack, CRC, Index), Outbox, DB-Vertrag,
                           Curves/: Import CSV/Text, COMTRADE, Excel, Ergebnisdatei; Releases/: Pakete, Audit;
                           Stations/: Prüfplatzkonfiguration, Gerätepool, Konfigurationsprüfung, StationCoordinator
  Testumgebung.DatabaseAdapters  SQL-Dialekte, Migration, SqlRunDatabase (ADO.NET), Vertragsprüfung – ohne Treiberabhängigkeit
  Testumgebung.Notifications     MQTT-3.1.1-Client (TLS, QoS 0) und MqttNotifier
  Testumgebung.Instruments       SCPI (Socket/VISA), Keysight/Agilent-Treiber, NI-DAQmx (C-API), Gerätesimulatoren
  Testumgebung.Identity          Anmeldung (IIdentityProvider, simuliert), Zugangsdatei, Rechte, SessionService
  Testumgebung.Presentation ViewModels ohne WPF-Abhängigkeit (Shell, Prüfplatz, Editor, Debugger, Bibliothek, Einstellungen,
                           Auswahl & Auftrag, System & Sicherung) – testbar; Pilot/: Systemprüfung, Konfigurationssicherung
  Testumgebung.Workers     Host-Seite der Code-Worker (Prozess, Protokoll, Pool, Abbruch) + python/worker.py
  Testumgebung.Workers.CSharp  Isolierter .NET-Worker (Roslyn)
  Testumgebung.Workers.Native  Isolierter C++-Worker (Toolchain-Erkennung, Build-Cache, DLL laden, C-ABI) – auch für LabVIEW
  Testumgebung.Desktop     WPF-Oberfläche (Windows)
tests/
  Engine.Tests             Abnahmetests aus Abschnitt 14 (xUnit)
docs/adr/                  Entscheidungen zu den „Vorschlag“-Punkten
docs/sql/                  Referenz-Schemaskripte je Datenbanksystem
build/CodeWorkers.targets  kopiert die Worker nach bin\…\workers\ (Desktop und Tests) und beim Veröffentlichen mit
build/publish.ps1          Tests, Veröffentlichung win-x64, Prüfsummen, Installer (installer/Testumgebung.iss, Inno Setup 6)
docs/handbuch/             Handbücher für Werker, Entwickler, Administrator
docs/pilot/                Abnahmeprotokoll der Pilotstation
```

## Minimalbeispiel

```csharp
var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }
    .Var("Kennung", VarType.String, "SN-10482")
    .Var("Spannung", VarType.Double)
    .Add(Steps.Measure("u", "Messung Spannung", "Spannung_Pruefling", "Spannung", 220, 240, "V"));

var loop = new GroupDefinition("loop", "Test Loop / Testing") { Repetition = Repetition.Endless }.Add(dut);
var def  = new TestDefinition("AC-Prüfung", "1.5",
    new GroupDefinition("init", "Test Start / Init"), loop, new GroupDefinition("fin", "Test End / Finalization"));

var sim = new SimulationAdapter();
sim.SetInput("ai0", 230.1);
var run = new TestRun(def, new TestRunOptions { Hardware = new HardwareRegistry().Bind("Spannung_Pruefling", sim, "ai0") });
run.EventRaised += e => Console.WriteLine(e);
var task = Task.Run(run.RunAsync);
// … später: Werkerabbruch
run.RequestAbort();
RunStatus status = await task;   // z. B. "PASS + WERROR"
```

## Abgedeckte Abnahmetests

| ID | Test |
|---|---|
| E01 | `ScopeAndExpressionTests.E01_*`, `ExecutionTests.E01_*` |
| E02 | `ExecutionTests.E02_*` |
| E03–E06 | `ExecutionTests.E03_*` … `E06_*` |
| E07–E09 | `AbortAndExitTests.*` |
| D01–D05 | `DebugTests.D01_*` … `D05_*` |
| D06 | `DebugTests.D06_*` (Variablen), `RunPatchTests.*` (Parameter-/Code-Patch eines nicht aktiven Knotens) |
| L01–L02 | `ResourceTests.L01_*`, `L02_*` (zwei Prüfplätze, virtuelle Uhr), `ParallelTests.Timeout_*` |
| P01–P05 | `ResultStorageTests.P01_*` … `P05_*`, mit echter Datenbank: `DatabaseAdapterTests.P02_P04_P05_*`, `SettingsTests.Ende_zu_Ende_*` |
| P06 | `MqttNotifierTests.P06_*` |
| A01 | `IdentityAndReleaseTests.A01_*` |
| V01 | `IdentityAndReleaseTests.V01_*` |
| W01 | `CurveTests.W01_*` (drei Wege, drei Phasen: Zeitbasis, Kanalzuordnung und Version reproduzierbar) |

Zusätzlich: Wiederholungsarten (Solange, Bis, Zeitspanne, endlos), Bedingungsdiagnosen, Pflicht-Kennung,
Abbruch beim Warten auf Ressourcen, Referenzzählung, faire Ressourcenvergabe, Abbruch während Haltepunkt.

Etappe 3 zusätzlich: `ProjectAndOperatorTests` (JSON-Rundreise, Katalogbausteine, Werkerinteraktion inkl.
Validierung und Anzeigedauer) und `PresentationTests` (Editor: Einfügen/Verschieben/Ein-/Ausrücken/Löschen,
Eigenschaften mit Validierung, Speichern/Öffnen; Prüfplatz: kompletter Prüflingsdurchlauf mit Eingaben,
Werkerabbruch mit Exit-Folge, Debugger mit Variablenänderung, gezielte Ausführung, geteilte AC-Quelle).

Etappe 4: `CodeBlockTests` (beide Sprachen: Compilerdiagnosen mit Zeile, Ordnervariablen lesen/schreiben,
Hardware über die zentrale Vermittlung inkl. Lock-Prüfung, Ausnahmen → ERROR mit Zeile, Werkerabbruch kooperativ
und per Prozessabbruch, Bibliotheksbaustein mit Bindungen und Bausteingrenze, Werkereingabe aus dem Code,
Umwandeln mit gleichem Verhalten, Speicherung) und `CodeEditorTests` (Build im Editor, Umwandeln und Ausführen am
Prüfplatz, Bibliothek mit Build/Test, Löschschutz).

Etappe 4 (Nachtrag): `CppAndLabViewTests` (C++: Diagnosen mit Zeile, Variablen, Hardware, Ausnahmen, Abbruch;
LabVIEW: Connector mit Test-DLL, fehlende DLL → ERROR mit Diagnose; werden übersprungen, wenn kein C++-Compiler
gefunden wird), `GroupBlockTests` (Port-Erkennung, gleiches Verhalten nach Umwandlung, Speicherung, Editor) und
`RunPatchTests` (nur bei Pause, aktive Knoten gesperrt, Wirkung nur im laufenden Durchlauf).

Etappe 5: `ResultStorageTests` – P01–P05 (siehe Tabelle) sowie verlustfreie Rundreise inkl. Kurven ohne Testversion,
dreiphasige Kurven mit gemeinsamer Zeitbasis, blockweise große Kurven mit expliziten Zeitpunkten, beschädigte und
abgeschnittene Dateien, atomares und kollisionsfreies Speichern mit Wiederherstellung, Anhang-Deduplizierung,
Gruppendefaults/Overrides, Filter im Editor, Outbox enthält genau die DB-Auswahl, defekte Outbox-Einträge, Ergebnisseite.

Etappe 6: `DatabaseAdapterTests` (SQLite über die native Bibliothek: Vertragsprüfung, Migration, echter Prüfling mit
Kurven und typisierten Messwerten, Outbox mit Ausfall/Neustart/Antwortverlust; alle vier Dialekte: vollständige Skripte
und Parameter; Referenzskripte aktuell; SQL Server/PostgreSQL/MySQL mit Verbindungsstring), `MqttNotifierTests`
(Nachrichteninhalt, QoS 0, P06, abgelehnte Anmeldung, voller Puffer, TLS mit Firmen-CA und Hostnamenprüfung) und
`SettingsTests` (geschützte Geheimnisse, Ende zu Ende Prüfplatz → Datenbank → MQTT mit Ausfall und Nachreichen,
Verbindungstest, Vertragsprüfung, Testmeldung).

Etappe 7: `IdentityAndReleaseTests` (Paket mit Bereichshashes und Manipulationserkennung, Hash-Kette des Audit-Logs,
Rechte getrennt/kombiniert und Freigaberegeln in Ordner- und SQLite-Ablage, paralleles Audit-Schreiben, A01 Anmeldung
während der Messung mit Kontowechsel und Abmeldung, Ablehnung unbestätigter/fremder/rechteloser Konten, Zugangsdatei
und Inaktivitätssperre, Werkerbetrieb nur mit Freigabe, V01 Versionswechsel am nächsten Prüfling bzw. Neustart).

Etappe 8: `CurveTests` (Generator dreiphasig mit gemeinsamer Zeitbasis, Formeln, Bearbeitung mit Historie ohne
automatische Konvertierung, unveränderliche Versionen in Projekt und Release-Paket inkl. Manipulationserkennung,
Import CSV mit Einheiten/expliziter Zeit/Qualitätscodes, COMTRADE ASCII/BINARY/FLOAT32, Excel mit gemeinsamen
Zeichenfolgen, Messkurve aus Ergebnisdatei mit unverändertem Original, Geräteeignung, Reihenfolge Laden → Übertragen →
Starten → Stoppen, W01; Oberfläche: Kurvenanzeige mit Zoom/Cursor/Effektivwert, Kurvenseite mit allen drei Wegen,
Versionierung und Testausgabe) sowie Kurvenanzeige der Werkeransicht in `PresentationTests`.

Etappe 9: `ParallelTests` (Abnahme: zwei Prüfplätze mit je sechs Prüflingen und gemeinsamer AC-Quelle unter
verschiedenen Namen ohne widersprüchlichen Zugriff, Gegenprobe ohne Sperre, Zeitlimit mit Besitzer in der Meldung,
Zyklusrisiko statisch und Startverweigerung, Laufzeit-Verklemmung → ERROR, Konfigurationsprüfung, Datei beschädigt,
Konfigurationsseite mit Live-Status, Prüfplatz anlegen/speichern, Werkerabbruch eines Prüfplatzes).

Etappe 10: `InstrumentTests` (Ressourcen → Socket/VISA, Multimeter/Netzgerät/AC-Quelle über SCPI mit
Fehlerwarteschlange und Bereichsprüfung, Generator-Kurvenausgabe und synchrone Oszilloskop-Aufnahme mit Phasenlage,
Abbruch während einer Messung und Zeitlimit mit wiederverwendbarer Sitzung, NI-DAQmx Einzelwerte/Aufnahme/Ausgabe/
Trigger/Abbruch/sicherer Zustand, Ablauf mit realen Treibern inklusive Werkerabbruch und sicherem Zustand,
Konfigurationsseite mit Laborsimulatoren und Gerät testen).

## Bausteine (Palette)

Messung, Ausgabe setzen, Digitaleingang lesen, Digitalausgang setzen, Wert setzen, Bewertung (Bedingung),
Bewertung setzen, Warten, Fehler erzeugen, Werkerinteraktion (Anweisung, Bild mit Bestätigung, Eingabe, Auswahl;
Pflicht, Bereich, erlaubte Werte, warten/fortsetzen, „bis Gruppenende sichtbar“). Grenzwerte und Werte sind
typisierte Ausdrücke, z. B. `GrenzwertMin` oder `Nennspannung * 1.05`.

CodeBlocks: CodeBlock (C#/Python/C++ im Ablauf), Baustein (aus der Bibliothek, zusätzlich LabVIEW) und
Grafischer Baustein (aus einer Gruppe).

Kurve aufnehmen: mehrere Kanäle eines Geräts mit gemeinsamer Zeitbasis (z. B. L1/L2/L3), optional Bewertung des
Effektivwerts je Kanal; die Kurven landen als binäre Chunks in Ergebnisdatei und Datenbank.

Kurvenausgabe: Kurve laden (Ressource, Kurve, Version – Geräteeignung wird geprüft), Kurve übertragen, Kurve starten,
Kurve stoppen. Die Reihenfolge wird vom Gerät erzwungen; das Beispielprojekt gibt `AC_Profil` v0.4 in Init aus und
stoppt in der Finalization.

Kommunikation: Senden, Empfangen, Abfrage, Modbus lesen/schreiben, CAN senden/empfangen, CAN-Signal lesen (DBC),
CAN-Botschaft senden (DBC), UDS-Diagnose (CAN/DoIP/LIN), J1939 empfangen/senden/Adresse beanspruchen, LIN lesen/senden, LIN-Fahrplan, UDS-Sicherheitszugang, DoIP Fahrzeugsuche.

Auswahl & Auftrag: Test auswählen (Ausgabevertrag des eigenen Auswahlablaufs: Testkennung, Version, Auftrag, Prüfparameter).

Kommunikation: `CommunicationTests` (Text/Hex, Modbus-Datentypen/CRC, CAN-Signale, Bausteine gegen die Simulation,
Reservierung, TCP-Textprotokoll, Modbus TCP gegen Simulatorserver, Modbus RTU mit CRC-Fehler und Wiederholung, virtueller
CAN-Bus mit FD und Filter, Prüfplatzkonfiguration).

Plan-Lücken und Pilot: `SelectionTests` (Kennungsmuster, Priorität, Mehrdeutigkeit, manuelle Auswahl, eigener Ablauf über den
Baustein und über einen einzigen CodeBlock mit `SelectTest`/`select_test`, Vorbelegung und Prüflingsanzahl in der Engine,
Werkerpanel mit genau einem Prüfling und Werkerabbruch, Konfigurationsseite),
`OperatorLayoutTests` (Layout im Projektformat, Fortschrittsposition, ausgeblendete Übersicht/Protokoll/Schritte, nur
auffällige Messwerte), `RecoveryTests` (Journal bei Absturz, Wiederherstellung als ERROR mit Filtern, Löschen nach
Speicherung, abgeschnittene Zeile, Verwerfen), `PilotTests` (Systemprüfung, Sicherung/Wiederherstellung mit Prüfsummen).

Auswertung, Editor, Abschnitt 15: `ReportTests` (Kennzahlen, Pareto, Cpk, CSV, PDF-Struktur und -Inhalt, mehrseitig),
`EditorComfortTests` (Rückgängig/Wiederholen, Kopieren/Einfügen mit neuen IDs, Suche, Versionsvergleich),
`OpenPointsTests` (Zeitspanne innerhalb der Iteration, Einzel-/Prozedurschritt/bis Gruppenende, App-Rollen).

Busdatenbanken, Diagnose, LIN: `BusTests` (DBC mit Multiplex/Wertetabellen/Gleitkomma/J1939, LDF, LIN-Prüfsumme, Signal-
und Botschaftsbausteine, UDS mit Segmentierung/NRC/„Antwort folgt“, J1939 Anforderung/BAM/RTS-CTS, LIN, Editor-Diagnose,
Import, ISO-TP/J1939 zwischen zwei Treiberinstanzen am virtuellen Bus, CodeBlocks in Python/C#/C++).

Weitere Meldewege, DB-Auswertung, Diagnoseausbau: `NotifierTests` (HTTP-Webhook gegen einen Test-Server, mehrere Meldewege,
OPC-UA-Optionen), `DatabaseAdapterTests` (Lesen aus SQLite mit Filtern), `ReportTests` (Auswertung aus der Datenbank),
`RecoveryTests` (Zurückkopieren aus dem Wiederherstellungsordner), `BusTests` (DoIP, erweiterte ISO-TP-Adressierung,
J1939-Adressanspruch, LIN-Fahrplan und UDS über LIN). Sicherheitszugang, Dreiphasenquelle, DoIP-Suche/TLS, DB-Kurven:
`BusTests` (Seed/Key gegen die Simulation), `InstrumentTests` (dreiphasige AC-Quelle), `DoIpAndTlsTests` (DoIP-Suche über UDP,
TCP mit TLS gegen ein Selbstzertifikat), `DatabaseAdapterTests` (Lauf mit Kurven aus SQLite). Run-Patch Teil 2 und OPC UA:
`RunPatchTests` (Bibliotheksbaustein-Quelltext und Struktur nicht aktiver Gruppen), `OpcUaTests` (Baustein und Python-CodeBlock
Lesen/Schreiben/Methode/Abo gegen den Simulationsserver, Fehlerfälle). Quellcode-Debugger: `CodeDebugTests` (Python-, C#- und
C++-Zeilen-Stepping mit Variablen – C++ bei vorhandener Toolchain, „bei Eintritt halten“, Stopp-Befehl, haltepunktfreier Lauf).

Installierbares MSIX-Bundle: `Release-MSIX.cmd` starten und eine neue Version eingeben. Anleitung: [MSIX-Release](docs/msix-release.md).

Eigene C#-Bibliotheken: [Handbuch zur DLL-Schnittstelle, Auftragsauswertung und Prüffunktionen](docs/handbuch-csharp-dll.md).

Ausführliche Bedienung mit Bildern: [Benutzerhandbuch](docs/handbuch/benutzerhandbuch.md).

Dynamische COM-Ports und Netzwerkverbindungen: [Anleitung mit Variablen, SSH, SFTP, HTTP und Telnet](docs/dynamische-verbindungen.md). Geräteadressen können während des Prüfprogramms aus aktuellen Variablen gesetzt werden. Die Protokolle sind direkt eingebunden; externe ssh-/sftp-/curl-Programme sind dafür nicht erforderlich.

Noch offen: Abnahme an der Pilotstation mit realer Hardware (`docs/pilot/abnahmeprotokoll.md`).


## Lizenz

Der eigene Code steht unter [MIT](LICENSE). Fremdkomponenten behalten ihre eigenen
Lizenzen. Siehe [Maßnahmen zur Lizenzprüfung](docs/licensing/massnahmen.md),
[Inventar](docs/licensing/inventar.md) und [Weitergabebedingungen](THIRD-PARTY-TERMS.md).
Für neue Veröffentlichungen werden NuGet-Lockdateien verwendet; CI bricht bei
ungeplanten Abhängigkeitsänderungen ab. Historische MSIX-Versionen bis 1.0.12.x
sind nicht durch die Nachprüfung der Version 1.0.13.3 freigegeben.
