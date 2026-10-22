# Kommandozeile: Prüfprogramme ohne Oberfläche prüfen, bauen und simulieren

`testumgebung-cli` führt dieselben Prüfungen wie die Anwendung aus, aber ohne Oberfläche: für CI, für Skripte und für
KI-Agenten, die Prüfprogramme schreiben und in einer abgeschotteten VM testen. Es nutzt den echten `ProjectSerializer`,
den echten Bausteinkatalog, die echte Engine und dieselben Code-Worker wie die Anwendung.

Es ersetzt **keine Abnahme auf realer Hardware**: Simuliert wird mit den Simulationsdaten des Projekts (`simulation`,
`simulationOutputs`, `simulationComm`), nicht mit Geräten. CodeBlocks, importierte DLLs und Prozessbausteine können trotzdem externe Programme starten oder Dateien und Netzwerk verändern. Unbekannte Prüfprogramme nur in einer dafür vorgesehenen isolierten Umgebung ausführen.

## Befehle

| Befehl | Wirkung |
| --- | --- |
| `check <Pfad>` | Projekt laden, Ablaufstruktur, Bausteinkonfiguration (mit Katalogauszug bei Fehlern), Speichern/Laden-Rundreise |
| `build <Pfad> [--junit <Datei>]` | wie `check`, zusätzlich alle CodeBlocks und Bibliotheksbausteine übersetzen (wie „Build prüfen“) |
| `test <Pfad> [--junit <Datei>]` | wie `build`, zusätzlich jedes Szenario (`*.tsim.json`) simulieren und mit den Erwartungen vergleichen |
| `simulate <Szenario.tsim.json>` | ein Szenario simulieren und das Ablaufprotokoll ausgeben (Schritte, Werte, Grenzen, Werkereingaben) |
| `katalog [Suchtext\|Bausteintyp]` | Bausteinübersicht (Typ, Name, Pflichtparameter) bzw. alle Parameter passender Bausteine |
| `anleitung [format\|szenario\|beispiel]` | KI-Anleitung zum Projektformat, diese Datei bzw. das geprüfte Beispielprojekt |

`<Pfad>` ist eine Projektdatei (`*.tproj.json`, mit allen Szenarien im selben Ordnerbaum, die auf sie verweisen), eine
Szenario-Datei oder ein Ordner (rekursiv; `bin`, `obj` und versteckte Ordner werden übersprungen). Exitcode: `0` alles
bestanden, `1` Prüfpunkte fehlgeschlagen, `2` Aufruf- oder Dateifehler. `--junit` schreibt einen JUnit-Bericht (eine
Suite je Projekt bzw. Szenario); ist der Wert ein Ordner, heißt die Datei `testumgebung.xml`.

Python-CodeBlocks laufen mit der mitgelieferten Python-Laufzeit, ein anderes Python über `TESTUMGEBUNG_PYTHON`.

## Szenario-Datei (`*.tsim.json`)

Ein Szenario beschreibt **einen** simulierten Lauf: welche Werkereingaben gemacht werden, welche Werte die Simulation
liefert und was herauskommen muss. Für einen Ablauf schreibt man mehrere Szenarien: Normalfall, Grenzwerte genau auf
und knapp über der Grenze, FAIL, fehlerhafte Eingabe, Werkerabbruch.

```json
{
  "format": "testumgebung.szenario",
  "formatVersion": 1,
  "name": "Zwei gute Prüflinge",
  "project": "netzteil.tproj.json",
  "duts": 2,
  "simulation": { "Spannung_Pruefling": 230.0, "Strom_Pruefling": [2.0, 2.5] },
  "answers": [ { "step": "sn", "values": ["SN-001", "SN-002"] } ],
  "expect": {
    "status": "PASS",
    "duts": [ { "dutId": "SN-001", "rating": "PASS" }, { "dutId": "SN-002", "rating": "PASS" } ],
    "steps": [ { "step": "p", "occurrence": 1, "rating": "PASS", "value": 460.0, "tolerance": 0.001 } ]
  }
}
```

| Feld | Bedeutung |
| --- | --- |
| `format`, `formatVersion` | exakt `testumgebung.szenario` und `1` |
| `name`, `description` | Anzeigename, optionale Beschreibung |
| `project` | Projektdatei, relativ zur Szenario-Datei |
| `duts` | Anzahl Durchläufe der Loop-Gruppe (Standard 1). Ersetzt nur für diesen Lauf die Wiederholung des Loops (`Endless`), die Projektdatei bleibt unverändert |
| `timeoutSeconds` | Zeitgrenze des Laufs in echten Sekunden (Standard 120). Danach Werkerabbruch und Fehler |
| `seed` | Startwert für das Rauschen (`noise`) der Projektsimulation (Standard 1, reproduzierbar) |
| `realTime` | `false` (Standard): virtuelle Uhr, `warten` und Zeitgrenzen vergehen sofort; `true`: echte Zeit |
| `simulation` | Eingangswerte je Ressourcenname aus `simulation` des Projekts: Zahl (fester Wert) oder Liste (je Lesezugriff der nächste Wert, danach bleibt der letzte) |
| `answers` | Werkereingaben je Schritt-ID (oder Schrittname): `value` (bei jeder Abfrage) oder `values` (der Reihe nach). Text, Zahl oder Bool |
| `abort` | `{ "atStep": "<Schritt-ID>", "occurrence": 1 }`: Werkerabbruch, sobald dieser Schritt zum n-ten Mal startet |
| `expect.status` | Gesamtergebnis: `PASS`, `FAIL`, `ERROR`, `NRESULT`, mit Werkerabbruch z. B. `PASS + WERROR` |
| `expect.duts` | abgeschlossene Prüflinge der Reihe nach: `dutId` und/oder `rating`. Die Anzahl muss stimmen |
| `expect.steps` | je Schritt-ID: `rating`, `value` (mit `tolerance`), `count` (Anzahl Ausführungen, auch `0`), optional `occurrence` (nur die n-te Ausführung) |

Regeln:

- Anweisungen und Bildbestätigungen werden automatisch bestätigt. Eingaben und Auswahlen brauchen eine Antwort in
  `answers`; fehlt sie oder sind die `values` aufgebraucht, endet der Lauf mit Werkerabbruch und einer Meldung, welche
  Antwort fehlt.
- Antworten werden wie in der Oberfläche geprüft (Pflicht, Bereich, erlaubte Werte). Eine ungültige Antwort ergibt
  ERROR am Schritt – so lassen sich Fehleingaben testen.
- Unbekannte Felder sind ein Fehler (Tippfehler fallen sofort auf).
- Erwartungen müssen aus der Prüfvorschrift bzw. dem Altprogramm stammen. Ein Szenario, das nur den aktuellen Stand
  abschreibt, prüft nichts.

## Ausgabe

Jeder Prüfpunkt erscheint als Zeile `[OK  ]`, `[FEHL]` oder `[--  ]` (übersprungen). Bei Fehlern folgen der passende
Katalogauszug bzw. das Ende des Ablaufprotokolls, z. B.:

```text
[FEHL] kaputt.tproj.json › Bausteinkonfiguration: Schritt u (analog.messen): „Ressource“ ist erforderlich.
       analog.messen – Messung [Analog messen / ausgeben]
         - Ressource (Resource, Pflicht, Standard „Spannung_Pruefling“)
         …
[FEHL] kaputt.tproj.json › Build CodeBlock p (Python): Fehler Zeile 1, Spalte 23: SyntaxError: invalid syntax
```

Projekte ohne Szenario werden bei `test` geprüft und gebaut, aber als „übersprungen“ gemeldet.

## Bauen und weitergeben

```powershell
pwsh -File build\publish-cli.ps1 -Version 1.0.0
```

Der Aufruf benötigt PowerShell 7 sowie die mit `build/acquire-toolchain.ps1` bereitgestellte Toolchain und erzeugt `artifacts\Testumgebung-Cli-1.0.0-win-x64.zip` (eigenständig: .NET-Laufzeit, C#-/C++-Worker und portable
Python-Laufzeit enthalten, keine Installation nötig) samt `.sha256`. Vor dem Packen läuft `test` mit den Beispielen
unter `docs/beispiele/kommandozeile/`. Im Quellcode-Checkout geht auch:

```powershell
dotnet run --project src/Testumgebung.Cli -- test docs/beispiele/kommandozeile
```

Nach dem Entpacken kann in PowerShell direkt gestartet werden:

```powershell
.\testumgebung-cli.cmd --version
.\testumgebung-cli.cmd test .\docs\beispiele\kommandozeile --junit .\ergebnisse.xml
```

`Testumgebung.Cli.exe` ist der eigentliche Starter; `testumgebung-cli.cmd` leitet
Argumente und Exitcode weiter. Das ZIP enthält auch die Compiler-Toolchain samt
zugehörigen Quellen und Lizenztexten. Es wird in GitHub Actions mit derselben
Buildversion wie das MSIX erzeugt und bei einem Release zusammen mit seiner
Prüfsumme angeboten. Die vorhandene MSIX-Installation wird dafür nicht benötigt.

## Einsatz mit KI-Agenten

KI-Agenten können `check`, `build` und `test` samt JUnit-Ausgabe als Rückmeldung nutzen.
Die Formatbeschreibung ist über `anleitung format`, die Szenariobeschreibung über
`anleitung szenario` verfügbar. Für CI sollte ein Projekt mindestens ein Szenario mit
fachlich begründeten Erwartungen enthalten.
