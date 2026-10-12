# Handbuch für Werker

## Überblick

Die Werkeransicht zeigt je Prüfplatz: links die **Ablaufübersicht** (Schritte des aktuellen Prüflings mit Zustand),
in der Mitte den **aktiven Inhalt** (Anweisungen, Bilder, Eingaben), rechts **Bewertung, Kurven und Messwerte**, unten
das **Protokoll**. Die Anordnung kann je Prüfablauf abweichen (z. B. Fortschritt oben, ohne Protokoll).

Oben wählen Sie den **Prüfplatz** (Kachel mit Zustand: Bereit, Läuft, Wartet, Abbruch). Jeder Prüfplatz hat eigenen
Start, eigenen Werkerabbruch und eine eigene Anzeige. Geprüft wird nur mit **freigegebenen** Prüfabläufen
(Kopfzeile „Freigegebene Prüfvorschrift“).

## Prüfen

1. Prüfplatz wählen, **Start** (unten rechts).
2. Ist der **Auswahl-/Auftragsablauf** aktiv, erscheint „Prüfling erfassen“:
   * **Kennung** scannen oder eingeben, **Enter** (oder „Zuordnen“).
   * **Auftrag** eintragen, falls nicht vorbelegt – er bleibt für die folgenden Prüflinge stehen.
   * Grün „Zuordnung eindeutig“: **Start** prüft genau diesen Prüfling.
   * Rot „Keine eindeutige Testzuordnung“: falls angeboten, über die Liste (z. B. „Manuell auswählen“) den passenden Test
     wählen; sonst den Vorgesetzten informieren. Ohne eindeutige Zuordnung startet keine Prüfung.
   * Nach dem Prüfling erscheint die Erfassung wieder für den nächsten Prüfling.
3. Anweisungen in der Mitte befolgen: bestätigen, Auswahl treffen (z. B. „i. O.“/„n. i. O.“) oder Wert eingeben.
   Pflichtfelder sind markiert; Werte außerhalb des erlaubten Bereichs werden abgelehnt.
4. Ergebnis: **PASS** (grün), **FAIL** (rot), **ERROR** (Fehler im Ablauf/Gerät). **WERROR** bedeutet: Werkerabbruch
   während des Prüflings.

Bei Endlosschleifen zeigt der Fortschritt den aktuellen Prüfling und die Durchlaufzahl, keinen Gesamtprozentsatz.

## Werkerabbruch

**Werkerabbruch** ist immer sichtbar. Er stoppt die regulären Schritte; danach laufen die Abschlussschritte
(Exit-Gruppen, z. B. Quelle abschalten) von innen nach außen – ihre Liste erscheint in der Mitte. Warten Sie, bis
„Bereit“ angezeigt wird. Bei aktivem Auswahl-/Auftragsablauf beendet der Werkerabbruch auch die Erfassung.

> Der Werkerabbruch ist ein **Software-Abbruch, kein Not-Halt**. Bei Gefahr den Not-Halt der Anlage betätigen.

Nach jedem Lauf werden die Geräte des Prüfplatzes zusätzlich in den sicheren Zustand gebracht (Ausgänge aus).

## Meldungen

| Meldung | Bedeutung / Vorgehen |
|---|---|
| „Wartet auf Ressource …“ | Ein gemeinsames Gerät wird gerade von einem anderen Prüfplatz benutzt – die Prüfung läuft automatisch weiter. |
| „Ergebnis lokal gesichert, Datenbank nicht erreichbar“ | Ergebnis liegt in der Outbox und wird automatisch nachgesendet. Weiterprüfen möglich. |
| „Ergebnisdatei im Wiederherstellungsordner“ | Ergebnisordner (z. B. Netzlaufwerk) nicht erreichbar – Vorgesetzten informieren. |
| „Unvollständige Prüfläufe aus einer früheren Sitzung“ | Nach Absturz/Stromausfall. Betroffene Prüflinge neu prüfen; Entwickler/Administrator entscheidet über die Aufzeichnung. |
| „Start verweigert: Zyklusrisiko …“ | Konfiguration der Prüfplätze passt nicht zusammen – Administrator informieren. |

## Anmelden

Werker müssen sich nicht anmelden. **Anmelden** (oben rechts) ist für Entwickler und Freigeber; laufende Prüfungen
laufen beim Abmelden weiter.
