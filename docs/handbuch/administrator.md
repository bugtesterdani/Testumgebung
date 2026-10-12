# Handbuch für Administratoren

Installation, Ablageorte und Rechte: `docs/installation.md`.

## Erstinbetriebnahme eines Prüfrechners

1. Installer als Administrator ausführen.
2. Anwendung als Administrator starten, **Anmelden** (ohne `zugang.json`: Entwicklungsmodus mit allen Rechten).
3. **Zugang** einrichten (`docs/entra-registrierung.md`): Entra-App, Mandant, Konten mit „Entwickeln“/„Freigeben“,
   optional Inaktivitätssperre; Rechtequelle lokal, über Entra-App-Rollen oder beides. Ohne gültige Zugangsdatei hat niemand erweiterte Rechte (beschädigt) bzw. alle
   (Datei fehlt = Entwicklungsmodus) – für den Pilot immer eine Zugangsdatei anlegen.
4. **Prüfplatzkonfiguration**: Anzahl Prüfplätze, Geräte (Adapter, Modell, Ressource, Zeitlimit, Optionen), Zuordnung
   abstrakter Namen zu Gerät/Kanal, gemeinsame Geräte nur mit Reservierung. **Gerät testen** und **Geräte suchen**
   (VISA/DAQmx). Geräteliste: `docs/geraete.md`.
5. **Auswahl & Auftrag** je Prüfplatz, falls Prüflinge über Kennung/Auftrag zugeordnet werden.
6. **Einstellungen** (je Windows-Benutzer): Ergebnisordner, Datenbank (Verbindungstest, Vertragsprüfung), Outbox, Meldewege MQTT, HTTP-Webhook und OPC UA.
7. **System & Sicherung → Systemprüfung** (mit „Reale Geräte ansprechen“) – keine Fehler mehr.
8. **Sicherung erstellen**, ZIP extern ablegen.
9. Abnahme nach `docs/pilot/abnahmeprotokoll.md`.

## Systemprüfung

Prüft: Ordner beschreibbar (Ergebnisse, Wiederherstellung, Outbox, Zwischenaufzeichnung), freien Speicher, Rechte auf
ProgramData (Werker: nur lesen ist erwartet), Prüfplatzkonfiguration und Geräte (optional mit `*IDN?`), Zugang,
Datenbankverbindung, offene Outbox, unvollständige Prüfläufe, Python, C++-Toolchain, VISA und NI-DAQmx (Fehler nur, wenn
zugeordnete Geräte sie brauchen).

## Betrieb

* **Neue Version der Anwendung:** Installer erneut ausführen (Konfiguration bleibt). Vorher Sicherung, nachher Systemprüfung.
* **Neue Prüfablaufversion:** über Releases freigeben; Prüfplätze übernehmen sie ab dem nächsten Prüfling.
* **Outbox wächst:** Datenbank nicht erreichbar – Einstellungen → Verbindungstest. Einträge bleiben erhalten und werden
  automatisch übertragen („Jetzt wiederholen“ unter Ergebnisse).
* **Unvollständige Prüfläufe:** Ergebnisse → als abgebrochen speichern (ERROR mit Vermerk) oder verwerfen (wird
  aufbewahrt). Betroffene Prüflinge neu prüfen.
* **Ergebnisordner war zeitweise nicht erreichbar:** Dateien liegen im Wiederherstellungsordner; sobald der Ordner wieder
  da ist, erscheint unter Ergebnisse ein Hinweis mit „In den Ergebnisordner zurückkopieren“ (beschädigte Dateien bleiben liegen).
* **Auswertung über die Datenbank:** Auswertung → Datenquelle „Datenbank“ wählt die konfigurierte SQL- bzw. simulierte
  Datenbank; Zeitraum, Test und Prüfplatz filtert der Server. Ohne Kurven – diese nur je Prüfling über die Ergebnisdatei.
* **Weitere Meldewege:** HTTP-Webhook (POST mit JSON wie MQTT, Erfolg = Status 2xx) und OPC UA (schreibt RunId, DutId,
  Event, Zeit, Fehler, Station in Knoten unter einem Präfix). Beide best effort wie MQTT; Kennwörter/Kopfwerte per DPAPI.
* **Prüfplatzkonfiguration beschädigt:** Kein Prüfplatz startet; Datei korrigieren oder aus Sicherung wiederherstellen.
* **Rechner ersetzen:** Anwendung installieren, Sicherung wiederherstellen, Datenbank-/MQTT-/HTTP-/OPC-UA-Kennwörter neu eingeben
  (DPAPI), Systemprüfung.

## Umgebungsvariablen

| Variable | Wirkung |
|---|---|
| `TESTUMGEBUNG_PYTHON` | Python-Interpreter für Python-CodeBlocks (z. B. venv) |
| `TESTUMGEBUNG_CXX` | fester C++-Compiler (cl.exe, clang++, g++) |
| `TESTUMGEBUNG_ERGEBNISSE` | Ergebnisordner, solange keine Einstellungsdatei existiert |
| `TESTUMGEBUNG_DB=aus` | ohne Datenbank, solange keine Einstellungsdatei existiert |
