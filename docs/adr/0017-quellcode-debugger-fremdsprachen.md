# ADR 0017 – Quellcode-Debugger für Fremdsprachen (Zeilen-Stepping in CodeBlocks)

Status: angenommen (Oktober 2026). Bezug: offener Punkt aus ADR 0015/0016.

## Entscheidung

Einheitliche Debug-Schnittstelle für alle CodeBlock-Sprachen, je Sprache unterschiedlich tief („einheitlich + gestuft“, wie mit
Dani abgestimmt). Zusätzlich zum bestehenden Ablauf-Debugging (Haltepunkte und Einzelschritt über Bausteine) gibt es nun
Zeilen-Debugging *innerhalb* eines CodeBlocks.

### Protokoll (sprachübergreifend)

* Der Aufruf (`invoke`) trägt eine `debug`-Konfiguration: `enabled`, `breakpoints` (Zeilen), `entry` (bei Eintritt halten),
  `step`. Kontrakt: `CodeDebugConfig` an `CodeRequest`.
* Erreicht der Nutzercode eine relevante Stelle, meldet der Worker einen Rückruf `debug.at` mit `line`, `kind`
  (`line`/`entry`/`exit`) und einem Variablen-Schnappschuss (`vars`). Der Host (`DebugController`) entscheidet über das Anhalten,
  meldet die Pause (`CodeLinePaused`) und liefert die Steuerung zurück: `cmd` (`continue`/`step`/`stop`) sowie den
  aktualisierten Haltepunktstand. Kontrakte: `CodeDebugEvent`, `CodeDebugStop`; Host-Rückruf `ICodeHost.DebugAsync`.
* Der Worker schaltet den Tracer nur bei aktiviertem Debugging ein – ohne Haltepunkte kein Mehraufwand.

### Je Sprache (Fähigkeit `CodeDebug.CapabilityOf`)

* **Python – volles Zeilen-Stepping** (`FullLineStepping`): `sys.settrace` meldet jede Nutzercode-Zeile; Variablen aus
  `frame.f_locals` (ohne API- und interne Namen). Haltepunkt pausiert *vor* Ausführung der Zeile.
* **C# – volles Zeilen-Stepping** (`FullLineStepping`): Roslyn-Instrumentierung des Nutzercodes im isolierten Worker – vor jede
  Anweisung ein `__DbgLine(zeile)`, nach Deklarationen/Zuweisungen ein `__DbgVar("name", wert)` zur Variablen-Erfassung; Ports
  (Felder) werden per Reflexion ergänzt. Die Zeilennummer wird aus der ursprünglichen `#line`-Zuordnung berechnet, daher
  verschieben die Einschübe die gemeldete Zeile nicht. Diagnosen weiterhin am nicht instrumentierten Build (korrekte Zeilen).
* **C++ – volles Zeilen-Stepping per Instrumentierung** (`FullLineStepping`): Der native Worker instrumentiert den Nutzercode
  lexikalisch, bevor er zur Bibliothek übersetzt wird – vor jede Anweisung an einer Zeilengrenze ein `__tu_dbg_line(zeile)`
  (ruft den Host über die Tabelle `tu_host.dbg_line`), nach einer einzeiligen einfachen Zuweisung/Deklaration ein
  `__tu_dbg_var("name", wert)`. Die Einschübe bleiben auf derselben physischen Zeile, daher stimmt die `#line`-Zuordnung
  (Diagnosen, Zeilennummern) weiterhin. Einschränkungen (dokumentiert, da lexikalisch statt über einen echten Parser): die
  Variablen-Erfassung deckt einzeilige `T name = …;` bzw. `name = …;` ab (keine mehrzeiligen Anweisungen, keine Compound-Ops,
  keine Ports); bei exotischer Syntax wird im Zweifel kein Einschub gesetzt. ABI der C-Schnittstelle dadurch auf Version 2.
* **LabVIEW – reduzierter Modus** (`EntryExitOnly`): kompilierte VI ohne textuellen Quelltext (Aufruf über generierten
  C++-Glue) → kein Zeilen-Stepping. Halt bei **Eintritt** (vor dem Lauf, mit Eingangswerten) und **Austritt** (nach dem Lauf,
  mit Ausgangswerten); „Stopp“ bei Eintritt bricht ab. Ein Haltepunkt am Baustein wirkt als Halt bei Eintritt.
* **Eintritt/Austritt gelten zusätzlich für alle Sprachen**: Jeder Debug-Lauf meldet beim Eintritt (ermöglicht „bei Eintritt
  halten“); der instrumentierte C++-Code steppt danach über die Zeilen (kein Austritts-Halt), LabVIEW meldet zusätzlich den
  Austritt.

### Oberfläche

`DebugController` hält die Code-Haltepunkte je Knoten und den Zustand; `DebugViewModel` bietet eine einheitliche Oberfläche
(Fortsetzen/Einzelschritt/Stopp, „Bei Eintritt halten“, Haltepunkt je Zeile umschalten, Variablen-Anzeige) mit einem Hinweis
auf die Debug-Tiefe der jeweiligen Sprache.

## Entscheidung Dani (07.10.): „beides kombinieren“

Instrumentierung als verlässliche, getestete Standardlösung für C++-Zeilen-Stepping (oben). Ein zusätzlicher gdb/MI-Weg für
echte lokale C++-Variablen (statt der instrumentierten, auf einzeilige Zuweisungen begrenzten Erfassung) ist als optionale
Erweiterung vorgesehen, aber **nicht Teil dieser Lieferung**: Er ist plattformgebunden und auf dem eigentlichen Zielpfad
Windows/MSVC headless nicht absicherbar (anderes Debug-Engine-API als gdb; ein live per Zeile synchronisiertes gdb/ptrace
gegen den laufenden Worker ist fragil). Da die Instrumentierung Zeilen-Stepping und die üblichen Variablen bereits abdeckt,
bleibt gdb ein dokumentierter Ausbaupunkt, der erst mit einer prüfbaren Windows-Umgebung sinnvoll umgesetzt wird.

## Nicht enthalten / weiterhin offen

* **Lokale C++-Variablen über einen echten Debugger (gdb/MI)** – siehe oben; Ausbaupunkt, headless/Windows nicht absicherbar.
* **Zeilen-Stepping für LabVIEW** – kein textueller Quelltext; bleibt bei Eintritt/Austritt.
* Variablen-Erfassung für mehrzeilige C++-Anweisungen, Compound-Zuweisungen und Ports; bedingte Haltepunkte und
  Ausdrucksauswertung im angehaltenen Zustand.
* **LIN-Slave-Betrieb** am realen Interface und die **Hardware-Erprobung** aller treibergebundenen Teile (siehe ADR 0015/0016).

## Folgen

* Haltepunkte liegen nun auf zwei Ebenen: Ablauf (vor der Bewertungsrückgabe eines Bausteins) und Nutzercode-Zeile; beide über
  denselben `DebugController`.
* Engine, Instruments und die Contracts bleiben stack-/sprachunabhängig; die Tiefe steckt allein in den Worker-Prozessen.
* Getestet über echte Worker: Python-, C#- und C++-Zeilen-Stepping mit Variablen-Inspektion (C++ bei vorhandener Toolchain),
  „bei Eintritt halten“, Stopp-Befehl und der haltepunktfreie Lauf (`CodeDebugTests`).
