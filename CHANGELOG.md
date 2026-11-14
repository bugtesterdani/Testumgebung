# Entwicklungsetappen

Diese Übersicht dokumentiert die Umsetzung des ursprünglichen Softwareplans. Versionsbezogene Release Notes stehen bei den jeweiligen [GitHub-Releases](https://github.com/bugtesterdani/Testumgebung/releases).


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
