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
6. **Verwaltung → Datenbank** (je Windows-Benutzer): Ergebnisordner, Datenbank (Verbindungstest, Vertragsprüfung) und Outbox. Unter **Verwaltung → Meldewege** MQTT, HTTP-Webhook und OPC UA konfigurieren.
7. **Verwaltung → Sicherung → Systemprüfung** (mit „Reale Geräte ansprechen“) – keine Fehler mehr.
8. **Sicherung erstellen**, ZIP extern ablegen.
9. Abnahme nach `docs/pilot/abnahmeprotokoll.md`.

## Systemprüfung

Prüft: Ordner beschreibbar (Ergebnisse, Wiederherstellung, Outbox, Zwischenaufzeichnung), freien Speicher, Rechte auf
ProgramData (Werker: nur lesen ist erwartet), Prüfplatzkonfiguration und Geräte (optional mit `*IDN?`), Zugang,
Datenbankverbindung, offene Outbox, unvollständige Prüfläufe, Python, C++-Toolchain, VISA und NI-DAQmx (Fehler nur, wenn
zugeordnete Geräte sie brauchen).

## Verschleißzähler

Zählerstände und das Rücksetzprotokoll liegen unter `%ProgramData%\Testumgebung\Verschleiss\verschleisszaehler.json`. Der klassische Installer gewährt der lokalen Gruppe **Benutzer** Änderungsrechte für den Unterordner `Verschleiss`. Bei einer MSIX- oder manuellen Installation den Ordner bei Bedarf als Administrator anlegen und der Gruppe **Benutzer** Änderungsrechte für diesen Ordner und seine Inhalte geben. Die Rechnerkonfiguration im darüberliegenden Ordner bleibt für Werker schreibgeschützt.

Bei einer Rechnerwiederherstellung `pruefplaetze.json` und `verschleisszaehler.json` aus derselben Sicherung auswählen. Die Sicherung enthält die Verschleißdatei, sobald Zählerstände oder Rücksetzungen gespeichert wurden.

## Betrieb

* **Neue Version der Anwendung:** Installer erneut ausführen (Konfiguration bleibt). Vorher Sicherung, nachher Systemprüfung.
* **Neue Prüfablaufversion:** über Releases freigeben; Prüfplätze übernehmen sie ab dem nächsten Prüfling.
* **Outbox wächst:** Datenbank nicht erreichbar – Verwaltung → Datenbank → Verbindungstest. Einträge bleiben erhalten und werden
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

## Alarmierung

Unter **Verwaltung → Meldewege → Alarmierung** die gewünschten Auslöser und mindestens einen Versandweg aktivieren. Beide Versandwege können gleichzeitig aktiv sein.

| Auslöser | Wann ein Alarm gesendet wird |
|---|---|
| ERROR-Prüfläufe in Folge | Beim Erreichen der eingestellten Anzahl je Prüfplatz. Ein regulärer Produktionslauf mit anderem Ergebnis oder ein Werkerabbruch setzt den Zähler zurück. Entwurfs- und Referenzläufe zählen nicht mit. |
| Offene Outbox-Einträge | Sobald die Anzahl der offenen Einträge den Grenzwert erreicht. Fällt sie darunter, ist der Alarm wieder bereit. |
| Ergebnisordner nicht erreichbar | Beim ersten fehlgeschlagenen Schreiben in den Ergebnisordner, auch wenn die lokale Wiederherstellungsdatei gespeichert wurde. Ein erfolgreicher Schreibvorgang gibt den Alarm wieder frei. |
| Kalibrierung | Beim Eintritt in die konfigurierte Vorwarnzeit und erneut bei Überschreitung des Kalibriertermins. Die Anwendung prüft die Gerätekonfiguration alle 30 Sekunden. |

Für beide Zahlenfelder deaktiviert `0` den Auslöser. Die Anwendung meldet eine fortbestehende Bedingung je Sitzung nur einmal. Sie prüft die Auslöser nach einem Neustart oder nach Übernahme der Einstellungen neu.

- **SMTP** braucht Server, Port, Absender und Empfänger. Standard ist Port 587 mit STARTTLS. Benutzername und Passwort sind für Server mit Anmeldung vorgesehen.
- **Teams** braucht die HTTPS-URL eines Workflows mit dem Auslöser „Wenn eine Teams-Webhookanforderung empfangen wird“. Der Workflow muss den Aufruf über die URL erlauben und die Adaptive Card im gewünschten Kanal posten. [Microsoft-Dokumentation zum Teams-Webhook](https://learn.microsoft.com/en-us/microsoftteams/platform/webhooks-and-connectors/how-to/add-incoming-webhook?tabs=newteams%2Cdotnet)

Die Versandversuche stehen unter **Gesendete Meldungen**. Ein fehlgeschlagener Versand beeinflusst den Prüfablauf nicht. SMTP-Passwort und Teams-URL sind wie die übrigen Zugangsdaten an den Windows-Benutzer gebunden und müssen nach einem Benutzer- oder Rechnerwechsel neu eingegeben werden.

## Umgebungsvariablen

| Variable | Wirkung |
|---|---|
| `TESTUMGEBUNG_PYTHON` | Python-Interpreter für Python-CodeBlocks (z. B. venv) |
| `TESTUMGEBUNG_CXX` | fester C++-Compiler (cl.exe, clang++, g++) |
| `TESTUMGEBUNG_ERGEBNISSE` | Ergebnisordner, solange keine Einstellungsdatei existiert |
| `TESTUMGEBUNG_DB=aus` | ohne Datenbank, solange keine Einstellungsdatei existiert |
