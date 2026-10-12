# ADR 0002 – CodeBlocks und isolierte Worker

Stand: 02.10.2026 · Status: angenommen für Etappe 4 (C# und Python; C++, LabVIEW, Gruppenbausteine und Run-Patch: ADR 0003)

## Zwei Verwendungsarten

| | CodeBlock im Ablauf (`code`) | Bibliotheksbaustein (`baustein`) |
|---|---|---|
| Variablen | alle im Ordner sichtbaren Variablen als Felder (C#) bzw. lokale Namen (Python); geänderte Werte werden zurückgeschrieben | nur die Ports; Bausteingrenze – `Var`/`SetVar` sind gesperrt |
| Eingänge | – | Ausdruck je Eingang (Konstante, Variable, Formel) |
| Ausgänge | – | Zielvariable je Ausgang; Ein-/Ausgang schreibt in die gebundene Variable zurück |
| Speicherung | im Schritt (`parameters.Quelltext`) | in `library` der Projektdatei, Schritt bindet über `in.<Port>`/`out.<Port>` |

„In Baustein umwandeln“ analysiert die verwendeten Ordnervariablen (nur gelesen → Eingang, nur geschrieben → Ausgang,
beides → Ein-/Ausgang), legt den Baustein an und ersetzt den Schritt durch einen gleichwertig gebundenen Baustein-Schritt
mit derselben Knoten-ID. Dynamische Zugriffe (`Var("…")`) werden als Warnung gemeldet.

## Gemeinsamer Vertrag

Ein JSON-Objekt pro Zeile über stdin/stdout, für alle Sprachen gleich:

* Host → Worker: `compile`, `invoke` (Aufruf-ID, Quelltext, Variablen mit Typ, Werte), `cancel`, `reply`, `exit`
* Worker → Host: `hello` (Laufzeitinfo), `compiled` (Diagnosen mit Zeile/Spalte im Nutzercode), `call`
  (Rückruf: get, set, read, write, ask, log), `done` (Bewertung, Wert, Einheit, Grenzen, Meldung, Variablenwerte), `error`

Hardwarezugriffe laufen als Rückruf über die Engine (`IStepContext`) – damit gelten Ressourcenlocks sprachübergreifend.
Werkerinteraktionen aus dem Code nutzen dieselbe Werkeroberfläche wie der Baustein „Werkerinteraktion“.

## Isolation und Abbruch

* Jeder Aufruf läuft in einem eigenen Worker-Prozess aus einem Pool (max. 2 untätige je Sprache).
* C#: Roslyn übersetzt den Nutzercode als Methodenrumpf in eine generierte Klasse (`#line`-Zuordnung, eingebettetes PDB
  für Zeilennummern in Ausnahmen); Laden in einen sammelbaren `AssemblyLoadContext`; Cache je Quelltext und Signatur.
* Python: Nutzercode als Funktionsrumpf; `compile()` für Diagnosen; Hintergrund-Thread liest stdin, damit `cancelled()`
  und `sleep()` den Abbruch sofort sehen. Interpreter über `TESTUMGEBUNG_PYTHON` wählbar (versioniertes Environment).
* Werkerabbruch: zuerst kooperativ (`CancellationToken`/`IsCancelled`, `cancelled()`), nach dem Abbruch-Timeout des
  Schritts (Standard 5 s) wird der Worker-Prozess beendet. Der Schritt gilt dann als abgebrochen; Hardwareaktionen werden
  nicht automatisch zurückgenommen.
* `print`/`Console.WriteLine` im Nutzercode gehen auf stderr und stören das Protokoll nicht.

## Offen

* C++ (Toolchain, Bibliothek mit C-Schnittstelle) und LabVIEW-VI-Prototyp.
* Grafische Bausteine aus Gruppen („Aus Gruppe erstellen“).
* Quellcode-Debugger für Fremdsprachen; Ablaufdebugging (Haltepunkte) gilt bereits für alle Typen.
* Code-Patch während einer Pause (D06, zweiter Teil).
