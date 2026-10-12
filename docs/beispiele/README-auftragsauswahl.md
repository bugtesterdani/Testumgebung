# Fertigungsauftrag → C#-Auswahl → Prüfsitzung

1. Der Werker scannt den **Fertigungsauftrag** und bestätigt mit Enter. Das Auftragsfeld erscheint direkt, wenn die Auswahl für den Prüfplatz aktiviert ist.
2. Der eigene Auswahlablauf liest `Auftrag`, fragt seine Datenquelle ab und ruft `SelectTest(testprogramm, version, Auftrag, parameter)` auf. Er läuft **einmal je Auftragsscan**. Scriptfehler, unbekannte Aufträge und nicht freigegebene Programme verhindern den Start.
3. Das gewählte Programm führt **Start/Init einmal** aus. Auftrag und Prüfparameter sind dort bereits verfügbar.
4. **Loop läuft bis zum Werkerabbruch.** Jeder Prüfling erfasst eine neue Seriennummer. Ergebnisse werden unter dieser Seriennummer und dem Fertigungsauftrag gespeichert.
5. Bei Abbruch folgt **End/Finalization**. Nach Laufende, Geräteabschaltung und Speicherabschluss scannt der Werker denselben oder einen anderen Auftrag erneut; das Script entscheidet erneut.

## Einrichten

Auf der Seite **Auswahl und Auftragsablauf** für den gewünschten Prüfplatz:

- Auswahl-/Auftragsablauf und **Fertigungsauftrag startet eine endlose Prüfsitzung** aktivieren.
- **Eigener Auswahlablauf** aktivieren und die `.tproj.json` auswählen. Für reine Scriptentscheidung Kennungsregeln und manuelle Auswahl deaktivieren.
- Speichern. Testprogramme im Werkerbetrieb vorher prüfen und freigeben.
- In der Simulation den Auftrag in **Scan** eintragen und **Prüfen** ausführen; das startet noch kein Testprogramm.

`OrderSession` ist auch bei vorhandenen Auswahlkonfigurationen ohne dieses neue Feld standardmäßig aktiv. Für den bisherigen Ablauf „Kennung zuordnen → ein Prüfling“ den Auftragsmodus ausschalten. Prüfplätze ohne aktivierte Auswahl behalten ihren direkten Ablaufstart.

## Beispiele

| Datei | Zweck |
|---|---|
| `auswahl-auftrag.tproj.json` | C#-Demo: `A…`, `B…`, `C…`, `D…` wählen Programm A, B, C bzw. D; andere Aufträge werden abgelehnt. |
| `auswahl-auftragsdatei.tproj.json` | C# liest Auftragsdaten aus einer lokalen JSON-Datei und wählt das dort hinterlegte Programm. |
| `fertigungsauftraege.beispiel.json` | Beispieldaten: `FA-100` → Programm A, `FA-200` → Programm B. |
| `programm-a.tproj.json` | Start, endloser Loop mit Seriennummer und Spannungsprüfung, End. |
| `programm-b.tproj.json` | Start, endloser Loop mit Seriennummer und Stromprüfung, End. |

Für die Dateivariante die Beispieldaten als `%ProgramData%\Testumgebung\fertigungsauftraege.json` bereitstellen oder den Pfad im CodeBlock anpassen. Die Anwendung legt dort keine erfundenen Produktionsdaten an. Dateifehler und unbekannte Aufträge verhindern die Auswahl. Eine ERP-, MES-, Datenbank- oder API-Abfrage ersetzt diesen Leseteil, sobald Schnittstelle und Authentifizierung bekannt sind.

## Vertrag des Testprogramms

- String-Variable `Auftrag` im Hauptablauf deklarieren; Prüfparameter wie `Artikel` ebenfalls dort, wenn Start oder End sie benötigen.
- Im Loop einen Prüflingstest (`IsDutRun`) mit **eigener lokaler String-Variable** `Kennung` anlegen (alternativer Name über `DutIdVariable`).
- Zu Beginn dieses Prüflingstests eine **Pflicht-Eingabe der Seriennummer** mit dieser Zielvariable einfügen. Danach folgen die Prüfschritte.
- Seriennummern nicht im Start vorbelegen. Seriennummern aus Auswahlparametern entfernt die Anwendung im Auftragsmodus. Ein frischer Prüflingsscope verhindert die Übernahme einer alten Seriennummer.
- Start liest den übergebenen Auftrag und überschreibt ihn nicht mit einem festen Beispielauftrag.

Eine Laufoption erzwingt im Auftragsmodus die endlose Haupt-Loop, ohne das gespeicherte oder freigegebene Projekt zu verändern. Start, Untergruppen und End behalten ihre Wiederholungsdefinition. Abbruch während der nächsten Seriennummernabfrage kann einen abgebrochenen Datensatz ohne Kennung hinterlassen; dieser wird keinem vorher geprüften Gerät zugeordnet.

Ergebnisdateien, Outbox und Wiederherstellungsjournal enthalten den Auftrag. Die Datenbank migriert automatisch auf Schema v3 (`tu_runs.order_id`, indexiert); vorhandene Ergebnisse ohne Auftrag bleiben lesbar. Der Ergebnisviewer zeigt den Auftrag im Kopf.

Die Auftragsauswertung kann auch öffentliche Methoden aus eigenen C#/.NET-DLLs aufrufen. Import und Beispiele stehen in [Eigene .NET-DLLs](../dotnet-dlls.md).
