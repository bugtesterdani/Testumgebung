# ADR 0011 – Kommunikation mit dem Prüfling

Status: angenommen (Oktober 2026). Bezug: Umsetzungsplan Abschnitte 1, 3, 7.

## Entscheidung

* **Verträge in Contracts:** `IByteStreamAdapter` (Seriell, TCP, UDP), `IModbusAdapter` (RTU/TCP, Kanal = Unit-ID),
  `ICanAdapter` (CAN/CAN FD mit Filter). Ein Gerät kann mehrere Verträge erfüllen; die Simulation erfüllt alle je Kanal.
* **Zugriff aus Bausteinen über `IStepContext.UseDeviceAsync`:** dieselbe Reservierungsprüfung und dasselbe
  Zugriffsprotokoll wie Messen/Ausgeben – gemeinsame Busse (RS-485, CAN) sind gemeinsame Geräte mit Sperre.
* **Treiber im Projekt Instruments** (Adapterfamilie neben den Messgeräten, gleicher Vertrag `IInstrumentDriver`:
  Gerät testen, Protokoll, sicherer Zustand):
  * Seriell über die Win32-API (kein NuGet-Paket nötig, Windows-only); TCP/UDP über Sockets.
  * Modbus selbst implementiert (RTU mit CRC und 3,5-Zeichen-Pause, TCP mit MBAP und Transaktions-ID, Wiederholung bei
    Zeitüberschreitung/CRC-Fehler, Ausnahmeantworten als `ModbusException` ohne Wiederholung).
  * CAN über PEAK PCAN-Basic und Kvaser CANlib per P/Invoke; Hintergrundleser mit Puffer (10 000 Rahmen); Empfangen
    liefert den ältesten passenden Rahmen seit dem letzten Verwerfen. Vector XL ist (noch) nicht angebunden.
* **Simulation zuerst:** Projekt enthält `SimulationComm` (Name, Kanal, Art, Regeln); Antworten sofort und deterministisch.
  Kein Treffer = Schweigen → Zeitüberschreitung, damit Fehlerpfade simulierbar sind.
* **Kodierung zentral** (`CommText`, `ModbusCodec`, `CanSignal`): Text mit Escapes/Hex, Modbus-Datentypen mit Wortfolge,
  CAN-Signale wie in DBC (Intel/Motorola).
* **Zeitüberschreitung = ERROR** (Gerät/Prüfling antwortet nicht), pro Baustein auf FAIL umstellbar; falsche Antwort
  (Muster, Sollwert, erwartete Daten) = FAIL.

## Nicht enthalten (Stand bei Annahme)

* DBC-/LDF-Import, ISO-TP/UDS-Transport, J1939, LIN, Vector-Hardware – über CodeBlocks oder spätere Bausteine.
* CodeBlocks greifen nicht über die zentrale Vermittlung auf Kommunikationsgeräte zu (nur Messen/Ausgeben).
* Erprobung von COM-Port-, PCAN- und Kvaser-Treiber an echter Hardware.

Nachtrag: Die ersten beiden Punkte sind mit ADR 0013 umgesetzt. Die Erprobung an echter Hardware steht weiterhin aus.
