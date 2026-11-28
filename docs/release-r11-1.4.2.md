# Release R11: 1.4.2

Release-Planung: [Issue #280](https://github.com/bugtesterdani/Testumgebung/issues/280),
Meilenstein **Release R11**. Zielversion: **1.4.2**.
Die vierte Paketversionsstelle vergibt der MSIX-Workflow fortlaufend.

## Lieferumfang

- #281 / #282: Rechnerunabhängige Python-Umgebungs-IDs, mehrere Umgebungen je
  Prüfplatz und programmspezifische Auswahl einschließlich Import und Export.
  Freigaben prüfen die tatsächlich ausgewählte Umgebung ohne absolute Pfade
  als Identität. Die mitgelieferte Runtime bleibt ausdrücklich auswählbar;
  eine fehlende explizite ID führt zu einem Fehler statt einem stillen Fallback.
- #283 / #284: Optionale externe MP3-/WAV-Datei während einer Werkerinteraktion.
  Gespeichert wird nur der Verweis; die Audiodatei kommt nicht in die Datenbank.
- #285 / #286: Die Werkeranzeige setzt bei Gruppenwiederholung mit NRESULT auch
  frühere FAIL-/ERROR-Bewertungen aus Codebausteinen korrekt zurück. Andere
  Gruppenfehler und die Versuchshistorie bleiben erhalten.

## Prüfung und Veröffentlichung

Alle genannten Änderungen sind in `main` enthalten. Für die letzte Korrektur
bestanden lokal 14.326 Engine-Tests und 98 Desktop-UI-Tests; sieben Tests ohne
externe Voraussetzungen wurden übersprungen.

Der Abschluss-PR schließt #280 und bestätigt die Zielversion 1.4.2. Nach seinen
erfolgreichen Prüfungen und dem Merge erzeugt der offizielle Workflow den Tag
**v1.4.2**, signierte Pakete und einen Release-Entwurf. Vor der Veröffentlichung
werden die Anlagen und technischen Lizenznachweise geprüft. Maßgeblich sind
der [Release-Ablauf](release-ablauf.md) und die
[Veröffentlichungsunterlagen](oeffentliche-veroeffentlichung.md).
