# ADR 0010 – Auswahl-/Auftragsablauf, konfigurierbare Werkeransicht, Pilotvorbereitung

Status: angenommen (Oktober 2026). Bezug: Umsetzungsplan Abschnitte 4, 5, 12 (Etappe 11), 15.

## Auswahl- und Auftragsablauf (Abschnitt 5)

* **Ort der Konfiguration:** je Prüfplatz in `pruefplaetze.json` (`stations[].selection`), nicht im Prüfprojekt – die Zuordnung
  Kennung → Test ist Sache der Fertigung und gilt über Projekte hinweg.
* **Methoden** unabhängig aktivierbar, feste Reihenfolge: eigener Auswahlablauf → Kennungsregeln → manuelle Auswahl.
  Mehrere Regeltreffer gleicher (kleinster) Priorität mit verschiedenen Zielen = mehrdeutig. Keine eindeutige Zuordnung →
  kein Start (konfigurierbare Meldung, manuelle Alternative nur wenn aktiv und nur unter freigegebenen Tests).
* **Nur freigegebene Versionen** (aktuell oder früher freigegeben, Paket geprüft). Im Entwicklungsmodus/„Entwurf testen“ der Editorstand.
* **Eigener Ablauf** = normales Projekt mit dem Baustein „Test auswählen“ (Ausgabevertrag: Testkennung, Version, Auftrag,
  Prüfparameter). Läuft mit Werkerinteraktion, Hardware und CodeBlocks des Prüfplatzes, Prüflingsanzahl 1.
* **Übergabe** an den zugeordneten Test über `TestRunOptions.Presets`: setzt sichtbare Variablen gleichen Namens beim
  Eintritt in den Prüflingslauf (typisiert, protokolliert). Die Werkereingabe mit Ziel = vorbelegte Variable übernimmt den
  Wert ohne Rückfrage. `TestRunOptions.DutLimit = 1` beendet einen Endlos-Loop nach genau einem Prüfling.
* **Schleife:** Start öffnet die Erfassung, nach jedem Prüfling zurück zur Erfassung (Auftrag bleibt), Werkerabbruch beendet sie.

## Werkeransicht (Abschnitt 4)

* `TestDefinition.Layout` (`OperatorLayout`): Fortschritt oben/links/unten, Ablaufübersicht, Protokoll, alle Messwerte.
  Teil des Projekts (`operatorLayout` nur bei Abweichung vom Standard → bestehende Pakete/Hashes unverändert).
* Ausblenden einzelner Schritte/Messwerte bleibt der bestehende Filter „Anzeigen (am Prüfplatz)“ (Abschnitt 9.1) –
  zusätzlich jetzt auch in der Ablaufübersicht. Kein zweiter Mechanismus.
* „Links“ ohne Ablaufübersicht fällt auf „oben“ zurück: der Fortschritt ist nie unsichtbar.

## Pilot (Etappe 11, Abschnitt 15)

* **Unvollständige Runs:** Journal je Prüflingstest (JSON-Zeilen, `FileOptions.WriteThrough` + Flush je Ereignis) in
  `%LOCALAPPDATA%\Testumgebung\Zwischenaufzeichnung`; gelöscht, sobald Datei, Wiederherstellungsordner oder Outbox das
  Ergebnis hat. Nach Neustart entscheidet ein Mensch: als abgebrochen speichern (ERROR, Vermerk, Filter aus dem Journal)
  oder verwerfen (aufbewahrt). **Keine automatische Weiterprüfung** (Abschnitt 15). Kurven werden nicht zwischengespeichert
  (Datenmenge); abgeschnittene letzte Zeile wird erkannt.
* **Sicherung:** ZIP mit Manifest und SHA-256; Wiederherstellen nur intakt, Vorstand daneben, atomar. DPAPI-Geheimnisse
  werden nicht entschlüsselt exportiert (Warnung beim Rechner-/Kontowechsel).
* **Systemprüfung:** nur lesend (außer Schreibtest), Fehler nur wenn zugeordnete Geräte eine fehlende Komponente brauchen.
* **Installation:** Inno Setup, framework-abhängig (optional self-contained), ProgramData-ACL über SIDs: Administratoren/SYSTEM
  voll, Benutzer lesen. Deinstallation lässt Konfiguration und Nachweise liegen.

## Offene Punkte

* Prüfung auf echter Hardware (Etappe 10) und Abnahme an der Pilotstation stehen aus (`docs/pilot/abnahmeprotokoll.md`).
* Gemeinsame Ergebnis-/Release-Ablage für mehrere Werkerkonten an einem Rechner: über Netzlaufwerk und SQL-Datenbank.
