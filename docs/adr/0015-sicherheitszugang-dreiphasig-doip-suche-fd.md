# ADR 0015 – Sicherheitszugang, dreiphasige AC-Quelle, DoIP-Suche/TLS, CAN FD über Vector, LDF-Frames, DB-Kurven

Status: angenommen (Oktober 2026). Bezug: offene Punkte aus ADR 0009, 0013, 0014.

## Entscheidung

* **Sicherheitszugang als Baustein** (Punkt „Seed/Key nur als CodeBlock“): `Uds.SecurityAccessAsync` fordert den Seed an
  (Stufe ungerade), berechnet den Schlüssel und sendet ihn (Stufe+1). Verfahren in `SecurityKey`: XOR-Konstante, Addition,
  Invertieren, Add-Rotate-XOR oder „Ausdruck je Byte“ (Funktion von `s` und `i`); herstellerspezifische Algorithmen weiterhin
  als CodeBlock. Baustein „UDS-Sicherheitszugang“. Die Simulation beantwortet 0x27 (`sicherheit STUFE seed=… <verfahren>`),
  inkl. `invalidKey` 0x35 bei falschem Schlüssel.
* **Dreiphasige AC-Versorgung** (ADR 0009): `ThreePhaseAcSource` fasst drei einphasige Quellen (AC6800B) zu einem Gerät L1/L2/L3
  zusammen; Kanäle `Ln:VOLT/FREQ/CURR/POW/OUT` sowie `ALLE:…` (an alle Phasen). Sicherer Zustand und „Gerät testen“ je Phase.
* **DoIP-Fahrzeugsuche** (ADR 0014): `DoIpDiscovery` (Vehicle Identification Request/Announcement) über `IDatagramChannel`
  (UDP 13400, `UdpDatagramChannel`); Baustein „DoIP Fahrzeugsuche“ mit Prüfung auf VIN/Adresse/Mindestanzahl.
* **TLS für den TCP-Client** (DoIP und Textprotokolle): Option `TLS=ja` am TCP-Gerät; `TLS-Servername`, `Firmen-CA`
  (CustomRootTrust) und `TLS-Prüfung=aus` für Laborzertifikate. DoIP über TLS = TCP-Client-Gerät mit TLS plus DoIP-Transport.
* **CAN FD über Vector XL** (ADR 0013): `VectorXlBackend` sendet/empfängt FD über `xlCanTransmitEx`/`xlCanReceive`
  (XLcanTxEvent/XLcanRxEvent), Bitraten über `xlCanFdSetConfiguration`. Nicht an Hardware erprobt.
* **LDF ereignisgesteuerte und sporadische Frames** (ADR 0013): `BusDatabase.FrameGroups`; der LIN-Fahrplan löst einen
  Gruppennamen in seine Einzelframes auf.
* **Kurven in der Datenbankauswertung** (ADR 0014): `IRunQuerySource.LoadRunAsync` liest einen Lauf vollständig inkl.
  zusammengesetzter Kurven; der Ergebnis-Reader (`ResultDocumentViewModel(ResultRun)`) stellt sie dar.

## Nicht enthalten / weiterhin offen

* **Code-Patch während einer Pause (D06, zweiter Teil)** und **OPC-UA-Methodenaufrufe/-Abonnements** waren hier zunächst
  zurückgestellt; beide sind inzwischen umgesetzt – siehe ADR 0016.
* **LIN-Slave-Betrieb** am realen Interface (PLIN-Slave-Konfiguration) – hardware-/treibergebunden; der virtuelle LIN-Bus
  bildet Slave-Antworten für Tests ab.
* **Quellcode-Debugger für Fremdsprachen** (Einzelschritt im C#-/Python-/C++-Nutzercode): eigener Debugger je Sprache,
  sehr umfangreich und headless kaum prüfbar – zurückgestellt. Ablauf-Debugging (Haltepunkte, Einzelschritt über Bausteine)
  gilt weiter für alle Typen.
* Erprobung aller treibergebundenen Teile (Vector FD, PLIN, DoIP-Gateways, reale Steuergeräte, SCPI-Quellen) an echter Hardware.

## Folgen

* `IDiagnosticTransport` deckt nun CAN (ISO-TP, auch erweiterte Adressierung), DoIP (TCP, optional TLS) und LIN ab.
* TLS liegt im TCP-Transport; jedes TCP-basierte Protokoll (DoIP, Textprotokolle) kann es nutzen.
