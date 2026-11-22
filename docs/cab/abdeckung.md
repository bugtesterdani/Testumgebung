# cab-JScript-Abdeckung

Generiert durch `build/update-cab-coverage.ps1` nach Ausführung der Nachweise.
Der normale Testlauf prüft denselben Bericht auf Aktualität und führt die Nachweise erneut aus.

237 Einträge, 1.416 Parameter und 1.801 Tabellenwerte. Der geprüfte Katalog enthält
zwei nachgetragene Abschnitte gegenüber der ursprünglich genannten Zahl 235.

| Art | Gesamt | Erkannt | Validiert | Quelltext | Eigenschaften-Editor | Grafisch vollständig | Vorschau | Registrierte Fälle |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| allgemein | 6 | 6 | 6 | 6 | 0 | — | — | 21 |
| barcode | 43 | 43 | 43 | 43 | 0 | 0 / 43 | — | 1182 |
| erweiterung | 7 | 7 | 7 | 7 | 0 | — | — | 22 |
| esc | 25 | 25 | 25 | 25 | 0 | — | — | 235 |
| etikettbefehl | 18 | 18 | 18 | 18 | 0 | 0 / 4 | — | 307 |
| grafik | 3 | 3 | 3 | 3 | 0 | 0 / 3 | — | 76 |
| inhaltsfeld | 107 | 107 | 107 | 107 | 0 | — | 107 / 107 | 298 |
| sofortbefehl | 17 | 17 | 17 | 17 | 0 | — | — | 130 |
| sonstiges | 4 | 4 | 4 | 4 | 0 | — | — | 8 |
| speicherkarte | 7 | 7 | 7 | 7 | 0 | — | — | 39 |

## Bedeutung und Grenzen

Erkannt und validiert: je Eintrag eine ausführbare Protokolldatei mit unverändertem
Schreiben und geprüfter Befehls-/Inhaltsfeldbindung. Allgemeine Regeln und drei
Barcode-Anwendungsprofile werden ausdrücklich über ihren Kontext bzw. Referenzbefehl
nachgewiesen; sie sind keine erfundenen zusätzlichen Druckerbefehle.

Jeder Tabellenwert hat zusätzlich einen expliziten `CatalogCase` mit konkretem
Parameterwert. Dieser wird als isolierter Parameter aus einem Protokollträger
gelesen, gegen seine eigene Definition geprüft und unverändert geschrieben. Das
ersetzt keine beliebige Kombination von Optionen in einem vollständigen Befehl.
Schematische Tabellenwörter werden durch konkrete Werte instanziiert; Antwortfelder
bleiben als solche gekennzeichnet. Alle Parameter besitzen außerdem die gültigen
Korpuswerte aus AP 2 bzw. die tatsächlichen Inhaltsfeldbindungen aus AP 3.

Vorschau: 82 offline auswertbare Definitionen und 25 explizite druckerabhängige
Platzhalter; zusätzlich gelten die Grenz- und Erwartungswerttests aus AP 3. Ein
Platzhalter behauptet keinen gelesenen Hardwarewert.

## Quelltextansicht (AP 4)

Alle 237 Korpusdateien öffnen und speichern bytegleich in der Quelltextansicht.
Alle 237 Einträge, 1.416 Parameter und 1.801 Werte sind für alle vier Generationen
in der Katalogvervollständigung erreichbar. Gezielte Text-, Codepage-, Binärschutz-
und Oberflächentests prüfen Bearbeitung, Navigation und unveränderte Objekte.
Die 237 Source-Ausnahmen wurden entfernt.

## Ausnahmen der frühen Einführung

Die empfohlene Reihenfolge führt AP 8 vor AP 4–7 ein. Deshalb sind Eigenschaften-Editor (237, AP 5), vollständige Grafiknachweise
(50, AP 6) und abschließende Projekt-/Release-/ZPL-Integration (1, AP 7) noch
ausdrücklich zurückgestellt. Bestehende Teilansichten zählen nicht als vollständiger
Nachweis einer Spalte. Die Pflichtspalte Eigenschaften-Editor steht tatsächlich auf
0, nicht auf behaupteten 100 %. Der Build erlaubt ausschließlich die 288 konkret
aufgelisteten Ausnahmen in `tests/Fixtures/cab/coverage-deferrals.json`; keine
Wildcards, Duplikate oder unbekannten IDs. Die späteren APs müssen ihren tatsächlichen
Nachweis ergänzen und ihre Ausnahmen entfernen. Am Ende muss die Liste leer sein.

## Korpus und lokales Handbuch

`tests/Fixtures/cab/` enthält eigene Dateien je Eintrag, eine ungültige Datei
je Barcodeprofil sowie Protokollgrenzen und die beiden bestehenden verlustfreien
Fixtures. Es werden keine zusätzlichen Herstellerexporte erfunden. Hersteller-PDF
und daraus lokal extrahierte Beispiele werden nicht eingecheckt. Der optionale
Handbuchtest und sein lokales Ergebnis werden getrennt vom synthetischen Nachweis
dokumentiert. Ein übersprungener Handbuchtest ist kein erfolgreicher Handbuchlauf.
