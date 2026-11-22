# Eigener cab-Testkorpus

`python build/generate-cab-test-corpus.py` erzeugt die synthetischen `.lbl`-Fälle,
das Fallinventar und die expliziten `CatalogCase`-Attribute. Erneutes Erzeugen
muss dieselben Dateien ergeben. Die Dateien enthalten keine Herstellerbeispiele.
Der Test prüft alle 237 Einträge, 1.416 Parameter und 1.801 Tabellenwerte.
Wertproben prüfen einzelne Regeln; vollständige Befehlsfälle prüfen zusätzlich
den Kontext. Die zwei `retained-*`-Dateien bewahren die bisherigen Grenzfälle.

`coverage-deferrals.json` ist keine Generatorausgabe. Die frühen Ausnahmen sind
einzeln den nachfolgenden APs zugeordnet und müssen dort abgebaut werden.
AP 4 hat seine 237 Source-Ausnahmen durch vollständige Katalogvervollständigung,
Quelltext-Korpus- und Bearbeitungsnachweise ersetzt; 288 Ausnahmen für AP 5–7 bleiben.
`build/update-cab-coverage.ps1` führt die Nachweise aus und aktualisiert den Bericht.

## Optionales Herstellerhandbuch

Eine eigene Python-Umgebung mit `build/requirements-cab-manual.txt` bereitstellen.
`TESTUMGEBUNG_CAB_MANUAL` benennt das lokale PDF (07/2026, Firmware 5.48),
`TESTUMGEBUNG_CAB_MANUAL_PYTHON` dessen Python-Interpreter. Ohne PDF überspringt
der normale Testlauf diesen Test. `build/test-cab-manual-extractor.py` prüft
synthetische Umbrüche unabhängig vom PDF. Die Extraktion verwendet Codekästen,
Schriftmerkmale und explizite CR/LF/ESC-Markierungen. Typografische Fortsetzungen
werden verbunden; Protokollbefehle bleiben getrennt. Ergebnisse liegen nur unter
`artifacts/cab/manual/` und werden nicht eingecheckt.

Der Auftrag, alle gedruckten Beispiele fehlerfrei zu validieren, widerspricht bei
25 Beispielen den katalogisierten Regeln. `manual-expected-diagnostics.json`
benennt diese konkreten Negativ-/Widerspruchsfälle mit Begründung, Quellhash und
genau erwarteten Diagnosen. Neue, veränderte und verschwundene Diagnosen sind
Testfehler. Die Datei enthält keine Beispieltexte. Diese Fälle zählen ausdrücklich
nicht als gültige Beispiele und nicht als Funktionsausnahmen von AP 4–7.
Die strengen Namens-, Pflichtparameter-, Längen- und Prüfziffernregeln bleiben
wirksam. Änderungen an dieser Liste erfordern eine erneute fachliche Prüfung.
