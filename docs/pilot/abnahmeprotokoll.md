# Abnahmeprotokoll Pilotstation (Etappe 11)

Abnahmekriterium laut Umsetzungsplan: *Repräsentativer Test an Pilotstation abgenommen.*
Je Prüfpunkt Ergebnis eintragen: **i. O.** / **n. i. O.** / **entfällt** (mit Begründung). Abweichungen unten erfassen.

| Feld | Eintrag |
|---|---|
| Pilotstation (Rechnername, Standort) | |
| Anwendungsversion (Installer) / SHA256SUMS geprüft | |
| Prüfablauf, freigegebene Version | |
| Datenbank (Typ, Server) | |
| Geräte (Modell, Ressource, Firmware) | |
| Datum, Teilnehmer (Entwicklung, Fertigung, Qualität) | |

## A. Installation und System

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| A1 | Installation als Administrator | Anwendung startet; `%ProgramData%\Testumgebung` vorhanden | | |
| A2 | Rechte ProgramData | Werkerkonto kann `pruefplaetze.json` nicht ändern (Speichern meldet fehlendes Recht) | | |
| A3 | Systemprüfung (mit Geräten) | keine Fehler; Warnungen bewertet und dokumentiert | | |
| A4 | Sicherung erstellen | ZIP enthält 3 Dateien + `sicherung.json` | | |
| A5 | Wiederherstellung | geänderte `pruefplaetze.json` wird zurückgesetzt, Vorstand aufbewahrt | | |

## B. Zugang und Freigabe

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| B1 | Anmeldung Entwickler (Entra) | Rechte laut `zugang.json`; fremder Mandant abgelehnt | | |
| B2 | A01: Anmeldung während Messung | Test läuft weiter | | |
| B3 | Freigabe durch zweite Person | Entwurf → Entwicklungsprüfung → Freigabe; Audit-Log vollständig | | |
| B4 | V01: Freigabe während laufendem Prüfling | laufender Prüfling mit alter Version, nächster mit neuer | | |
| B5 | Werker ohne Anmeldung | nur freigegebene Version startbar | | |

## C. Repräsentativer Prüfablauf (reale Geräte)

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| C1 | Gerät testen je Gerät | `*IDN?`/Selbsttest ok, Kanäle aus dem Treiber | | |
| C2 | Gut-Prüfling | PASS; Messwerte plausibel gegen Referenzmessgerät | | |
| C3 | Schlecht-Prüfling bzw. Grenzwert verletzt | FAIL am richtigen Schritt | | |
| C4 | Kurvenausgabe (falls im Ablauf) | Laden/Übertragen/Starten/Stoppen, Kurve am Oszilloskop/Messung bestätigt | | |
| C5 | Werkerabbruch während Messung (E07) | Exit-Folge innen → außen, Ausgänge aus, WERROR | | |
| C6 | Abbruch während Gerätezugriff | kein Hängen; sicherer Zustand; nächster Start möglich | | |
| C7 | Gerät während Prüfung trennen | ERROR mit verständlicher Meldung; sicherer Zustand der übrigen Geräte | | |
| C8 | Werkeransicht | Anweisungen/Bilder/Eingaben beim aktiven Inhalt; Layout wie konfiguriert; Start/Abbruch immer sichtbar | | |

## D. Auswahl- und Auftragsablauf (falls genutzt)

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| D1 | Eindeutige Kennung (Regel) | Zuordnung grün, Start prüft genau einen Prüfling, Kennung nicht erneut abgefragt | | |
| D2 | Mehrdeutige/unbekannte Kennung | Meldung konfiguriert, kein Start; Alternative nur wenn freigegeben | | |
| D3 | Eigener Auswahlablauf | liefert Test, Auftrag, Prüfparameter; Fehler → kein Start | | |
| D4 | Folgeprüfling | Erfassung erscheint wieder, Auftrag bleibt; Werkerabbruch beendet die Auswahl | | |

## E. Parallelbetrieb (falls mehrere Prüfplätze)

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| E1 | L01: zwei Prüfplätze, gemeinsames Gerät | exklusiver Zugriff, Zugriffsprotokoll ohne Konflikte | | |
| E2 | L02: Sperr-Timeout | ERROR, keine regulären Schritte ohne Reservierung | | |
| E3 | Werkerabbruch auf einem Prüfplatz | anderer Prüfplatz läuft weiter | | |

## F. Daten und Wiederherstellung

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| F1 | Ergebnisdatei | je Prüfling `.tres`, im Reader vollständig, Kurven vorhanden | | |
| F2 | P01: Filter | nur gewählte Ziele enthalten Daten | | |
| F3 | P02: Datenbank während Prüfung trennen | Outbox, 10-s-Warnung, späteres Nachsenden ohne Dubletten (P05) | | |
| F4 | P04: Neustart mit offener Outbox | Übertragung beim nächsten Öffnen fortgesetzt | | |
| F5 | Stromausfall/Prozess beenden während Prüfling (Task-Manager) | nach Neustart Hinweis „unvollständige Prüfläufe“; als abgebrochen gespeichert (ERROR, Vermerk) bzw. verworfen; keine automatische Weiterprüfung | | |
| F6 | Ergebnisordner (Netz) nicht erreichbar | Datei im Wiederherstellungsordner, Meldung | | |

## G. Bedienprüfung und Dokumentation

| Nr. | Prüfpunkt | Erwartung | Ergebnis | Bemerkung |
|---|---|---|---|---|
| G1 | Werker prüft nach `handbuch/werker.md` ohne Hilfe | 5 Prüflinge ohne Rückfrage | | |
| G2 | Entwickler ändert Grenzwert, gibt frei (`handbuch/entwickler.md`) | neue Version am Prüfplatz | | |
| G3 | Administrator richtet Prüfplatz nach `handbuch/administrator.md` ein | Systemprüfung ohne Fehler | | |

## Abweichungen

| Nr. | Beschreibung | Bewertung (kritisch / Nacharbeit / akzeptiert) | Verantwortlich | Termin |
|---|---|---|---|---|
| | | | | |

## Entscheidung

☐ Abgenommen  ☐ Abgenommen mit Auflagen  ☐ Nicht abgenommen

| Rolle | Name | Datum | Unterschrift |
|---|---|---|---|
| Entwicklung | | | |
| Fertigung | | | |
| Qualität | | | |
