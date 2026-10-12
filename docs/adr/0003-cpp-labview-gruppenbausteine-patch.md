# ADR 0003 – C++, LabVIEW (Prototyp), grafische Bausteine und Run-Patch

Stand: 02.10.2026 · Status: angenommen (Nachtrag Etappe 4)

## C++-Worker

* Eigener Worker-Prozess `Testumgebung.Workers.Native` mit demselben JSON-Zeilenprotokoll wie C#/Python.
* **Toolchain:** `TESTUMGEBUNG_CXX`, sonst MSVC (`vswhere` → `vcvars64.bat`), sonst `clang++`, sonst `g++`.
  Die gefundene Toolchain erscheint in der `hello`-Meldung. Diagnosen werden aus der Compilerausgabe (MSVC- bzw.
  GCC/Clang-Format, englisch erzwungen) gelesen und per `#line 1 "CodeBlock"` auf Zeilen im Nutzercode abgebildet.
* **Build-Cache:** Quelltext-Hash → `%TEMP%\Testumgebung\cpp\<hash>` (`TESTUMGEBUNG_CPP_CACHE`); unveränderter Code
  wird nicht neu übersetzt.
* **Stabile C-Schnittstelle (ABI 1):**

  ```c
  int tu_abi_version(void);               // muss 1 liefern
  int tu_invoke(const tu_host* host);     // 0 = ok, 1 = Fehler, 2 = abgebrochen
  ```

  `tu_host` ist eine Tabelle von C-Funktionszeigern (Werte holen/setzen, Ergebnis, Hardware lesen/schreiben, Log,
  Abbruch abfragen, Werkereingabe/-bestätigung, Pfadvariablen). Damit ist die DLL unabhängig von Compiler und
  C++-Laufzeit des Hosts. Ausnahmen werden in der DLL gefangen und als ERROR „C++-Ausnahme: …“ gemeldet.
* **Abbruch:** kooperativ über `IsCancelled()/ThrowIfCancelled()` (auch in `Sleep`, `Read`, `Write`); reagiert der
  Code nicht, wird der Worker-Prozess nach der Frist beendet – wie bei C#/Python.

## LabVIEW (Prototyp)

LabVIEW ist auf dem Entwicklungsrechner noch nicht vorhanden. Umsetzung daher als Brücke ohne LabVIEW-Abhängigkeit:

* Ein LabVIEW-Baustein speichert Einstellungen (`vi`, `dll`, `funktion`, `aufruf` = C|stdcall) statt Quelltext.
  Bearbeitet wird das VI extern („In LabVIEW öffnen“); ausgeführt wird die mit dem Application Builder gebaute DLL.
* Aus den Ports wird ein C++-Glue erzeugt, den der C++-Worker übersetzt. **Connector-Konvention** (Reihenfolge =
  Portliste): Eingang Double → `double`, Int → `int32_t`, Bool → `uint8_t` (LVBoolean), String → `const char*`;
  Ausgänge/Ein-Ausgänge als Zeiger, String-Ausgang als `(char* puffer, int32_t länge)`.
  Optionaler Int-Ausgang „Bewertung“ (0–3) setzt die Bewertung, ein nicht leerer String-Ausgang „Fehler“ ergibt ERROR.
* Fehlende DLL/Runtime oder fehlendes Symbol → Diagnose bei „Build prüfen“ und ERROR bei Ausführung
  („LabVIEW-DLL nicht ladbar: …“). DateTime-Ports werden abgelehnt.
* Offen bis LabVIEW verfügbar ist: Prüfung mit echter LabVIEW-Runtime, Arrays/Cluster am Connector.

## Grafische Bausteine aus Gruppen

* `GroupBlockDefinition` (ID, Name, Version, Beschreibung, Ports, Körper = Gruppe) in `groupLibrary` der Projektdatei.
* Ein Schritt `gruppenbaustein` bindet wie ein Code-Baustein über `in.<Port>`/`out.<Port>`.
* **Grenze:** Nur der Port-Scope ist Bausteingrenze; der Körper selbst nicht. Variablen im Körper sehen daher die
  Ports, aber nichts vom Aufrufer.
* „Aus Gruppe erstellen“ analysiert Ausdrücke, Variablen-Parameter, Code und Bindungen im Körper (lokale Variablen
  ausgenommen): nur gelesen → Eingang, geschrieben → Ausgang, beides → Ein-/Ausgang. Die Gruppe wird durch einen
  Baustein-Schritt mit derselben Knoten-ID ersetzt; Ressourcen der Gruppe gehen auf den Körper über.
* Der Körper erscheint im Editor als eigener Abschnitt und ist dort wie jede Gruppe bearbeitbar.

## Run-Patch (D06)

* Nur bei Pause (Haltepunkt/Schritt). Änderbar: Parameter (`param:<Name>`, inkl. Quelltext), Name, Bedingung.
* Gesperrt: Knoten im aktuellen Ausführungsstack (aktiv).
* Jede Änderung wird an einer Kopie über den Bausteinkatalog neu aufgebaut und validiert; erst dann übernommen.
* Patches werden nummeriert und als Entwicklungsereignis protokolliert („Run-Patch N angewendet“). Sie gelten nur
  für den laufenden Durchlauf – der Projektentwurf bleibt unverändert und muss bei Bedarf separat gespeichert werden.
