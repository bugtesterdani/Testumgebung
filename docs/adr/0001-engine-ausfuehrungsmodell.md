# ADR 0001 – Ausführungsmodell der Engine

Stand: 02.10.2026 · Status: angenommen für Prototyp (Etappen 0–2)

Dieses ADR hält fest, wie die „Vorschlag“-Punkte und offenen Fragen aus dem Umsetzungsplan in der Engine
umgesetzt sind. Festgelegte Regeln des Plans werden dadurch nicht verändert.

## Hauptablauf

`TestDefinition.Root` ist eine Gruppe „Hauptablauf“ mit den Kindern Init und Loop; Finalization ist ihre
Exit-Gruppe. So gelten für Finalization dieselben Exit-Regeln wie für jede andere Gruppe.

## Reihenfolge beim Gruppeneintritt

1. Scope initialisieren (Startwerte; bei Ressourcen zusätzlich `RessourcenErhalten = false`)
2. Eintrittsbedingung prüfen (in der Abbruchfolge für Exit-Gruppen übersprungen)
3. Ressourcen atomar erwerben – Timeout ⇒ ERROR, reguläre Schritte entfallen, Exit läuft
4. Reguläre Wiederholungen
5. Exit-Gruppe (sieht die Variablen der Gruppe)
6. Ressourcen freigeben, Prüflingstest abschließen, Scope verwerfen, Bewertung zurückgeben

## Wiederholungen

`Zeitspanne` wird zwischen Iterationen geprüft (die erste Iteration läuft immer). `Solange` prüft vor jeder
Iteration, `Bis` nach jeder Iteration. `Endlos` ist für den Test Loop.

## Werkerabbruch als nummerierte Ereignisse

* Abbruch 1: reguläre Phase abbrechen. Der laufende Schritt erhält Cancellation; es starten keine weiteren
  regulären Schritte. Eine bereits laufende Exit-Gruppe darf ihre Wiederholungen ausführen.
* Jeder weitere Abbruch: nur die innerste laufende Exit-Gruppe beenden. Jede Exit-Gruppe startet mit einem
  eigenen, neuen Abbruchsignal, sodass äußere Exit-Gruppen normal laufen.
* Verschachtelte Exit-Gruppen (offene Frage): Wird eine Exit-Gruppe abgebrochen, laufen die Exits ihrer aktiven
  Untergruppen trotzdem von innen nach außen – das ist dieselbe Regel wie im regulären Ablauf.
* Abbruch während eines Haltepunkts: Die Pause endet, die ausstehende Bewertung wird übergeben, danach greift der Abbruch.
* Ein abgebrochener Schritt liefert NRESULT mit `Cancelled = true`. WERROR ist ein eigenes Kennzeichen (`RunStatus.OperatorAbort`).
* Das Abbruchsignal wird asynchron ausgelöst, damit der UI-Thread nicht die Engine-Fortsetzung ausführt.

## Haltepunkte

`ExecuteNode` → Ausführung → `PendingResult.Replace` → bei Haltepunkt pausieren → „Erneut ausführen“
wiederholt die Ausführung (bei Gruppen als neuer Eintritt) → `Commit` genau einmal. Bei Gruppen enthält die
Pause einen Schnappschuss der verlassenen lokalen Variablen, der kein aktiver Scope ist.

## Ressourcen

Zentraler `ResourceManager` mit atomarem Erwerb, FIFO-Fairness (keine Anforderung überholt eine frühere mit
überlappenden Ressourcen), Besitz an der Run-ID und Referenzzählung bei verschachtelten Gruppen desselben Runs.
Gemeinsam genutzte Hardware (`RequiresLock`) ist nur mit eigener Reservierung zugänglich, sonst ERROR.
Noch offen: statische Zyklusprüfung beim Erweitern um weitere Sperren.

## Ausdruckssprache

Eigene typisierte Sprache (kein C#/Python-eval): Literale, Variablen inkl. `../` und Punktnamen, `! - + * / %`,
Vergleiche, `&& ||` mit Kurzschluss. Typfehler, fehlende Namen und Division durch 0 erzeugen eine Diagnose;
eine fehlerhafte Eintrittsbedingung bedeutet: Gruppe nicht betreten, Bewertung ERROR.

## Uhr

`IClock` mit `VirtualClock` (automatisch oder manuell vorgerückt) macht Wiederholungen und Timeouts in Tests deterministisch.
