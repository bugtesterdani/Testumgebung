# ADR 0013 – Busdatenbanken, Diagnose (UDS), J1939, LIN, Kommunikation aus CodeBlocks

Status: angenommen (Oktober 2026). Bezug: ADR 0011 „Nicht enthalten“, Umsetzungsplan Abschnitte 1, 3, 7.

## Entscheidung

* **Busdatenbanken im Projekt:** DBC- und LDF-Dateien werden im Originaltext im Projekt gespeichert
  (`TestDefinition.BusDatabases`, Projektformat `busDatabases`) und sind damit Teil des Release-Pakets und des
  Versionsvergleichs. Ausgewertet wird beim ersten Zugriff (`BusDatabase` in Contracts: Botschaften, Signale mit
  `CanSignal`-Kodierung, Multiplex, Wertetabellen, Gleitkomma, Start- und Zykluswerte). Bausteine verweisen über den
  Datenbanknamen; der Editor prüft Datenbank, Botschaft und Signal statisch.
* **Protokolle zentral in Contracts:** `IsoTp` (Einzel-, Erst-, Folgerahmen, Flusssteuerung mit Blockgröße/STmin,
  CAN FD bis 64 Bytes, Längen über 4095), `Uds` (P2/P2*, NRC 0x78, unterdrückte positive Antwort, negative Antwort als
  `UdsNegativeResponseException` mit Klartext), `J1939` (ID aus Priorität/PGN/Adressen, Anforderung, BAM, RTS/CTS in beide
  Richtungen), `Lin` (geschützte ID, klassische/erweiterte Prüfsumme). Sie arbeiten auf den Verträgen `ICanAdapter`
  (neu: `ReceiveAnyAsync` mit mehreren Filtern) und `ILinAdapter` (neu) und laufen damit unverändert gegen Simulation,
  virtuellen Bus und echte Treiber.
* **Bausteine** `can.signal.lesen`, `can.botschaft.senden`, `uds.anfrage`, `j1939.empfangen`, `j1939.senden`,
  `lin.lesen`, `lin.senden` in der Palette „Kommunikation“, Zugriff über `IStepContext.UseDeviceAsync` (Reservierung,
  Zugriffsprotokoll). Bewertung wie bisher: Zeitüberschreitung ERROR (wählbar FAIL), falsche Antwort/Wert FAIL,
  Konfigurations- und Datenbankfehler ERROR.
* **CodeBlocks:** `ICodeHost.DeviceAsync(operation, argumente)` mit den Operationen `comm.*`, `modbus.*`, `can.*`, `uds`,
  `j1939.*`, `lin.*`. Der Host führt sie über `CodeDevices` und den Schrittkontext aus – dieselbe Vermittlung wie die
  Bausteine, damit Sperren sprachübergreifend gelten. APIs in Python (`comm_query`, `uds`, …), C# (`CommQuery`, `Uds`, …)
  und C++ (neuer, am Ende angefügter Eintrag `device` der Host-Tabelle; ABI-Version bleibt 1).
* **Hardware:** Vector XL Driver Library (vxlapi64.dll, klassisches CAN, Anwendungskanal aus „Vector Hardware Config“),
  PEAK PLIN-API (PLinApi.dll, Master), virtueller LIN-Bus mit festen Antworten. Wie bei PCAN/Kvaser per P/Invoke ohne
  NuGet-Paket; ohne Treiber meldet „Gerät testen“ „Treiber nicht installiert“.
* **Simulation:** CAN-Regeln mit mehreren Antwortrahmen (`;`), abwechselnden zyklischen Rahmen gleicher ID (Multiplex),
  UDS-Steuergerät mit ISO-TP (`uds 0x7E0 0x7E8`, `uds: ANFRAGE => ANTWORT`), LIN-Slave-Regeln (`ID => DATEN`, `@ID`).

## Nicht enthalten

* CAN FD über Vector XL (Ereignisstruktur `XLcanTxEvent`); FD bleibt über PCAN und Kvaser möglich.
* LIN-Slave-Betrieb; Ereignis-/sporadische Frames werden aus der LDF nicht übernommen.
* Seed/Key-Algorithmen (herstellerspezifisch) – als CodeBlock mit `uds(…)`.
* Erprobung der Treiber an echter Hardware.

Nachtrag (ADR 0014): LIN-Fahrplantabellen und LIN-Diagnose über das Transportprotokoll, ISO-TP mit erweiterter/gemischter
Adressierung, J1939-Adressvergabe (Address Claim) und DoIP sind umgesetzt.

## Folgen

* Projektdateien mit Busdatenbanken werden größer (Originaltext); ältere Anwendungsstände ignorieren das Feld nicht,
  sondern lesen es nicht – das Projekt bleibt ladbar, die Bausteine melden dann „Busdatenbank nicht im Projekt“.
* `ICanAdapter.ReceiveAnyAsync` hat eine Standardimplementierung für einen Filter; eigene CAN-Adapter mit mehreren
  Filtern müssen sie für J1939-Transport implementieren.
