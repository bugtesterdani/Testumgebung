# cab-JScript-Abdeckung

Generiert durch `build/update-cab-coverage.ps1` nach Ausführung der Nachweise.
Der normale Testlauf prüft denselben Bericht auf Aktualität und führt die Nachweise erneut aus.

237 Einträge, 1.416 Parameter und 1.801 Tabellenwerte. Der geprüfte Katalog enthält
zwei nachgetragene Abschnitte gegenüber der ursprünglich genannten Zahl 235.

| Art | Gesamt | Erkannt | Validiert | Quelltext | Eigenschaften-Editor | Grafisch vollständig | Vorschau | Registrierte Fälle |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| allgemein | 6 | 6 | 6 | 6 | 6 | — | — | 21 |
| barcode | 43 | 43 | 43 | 43 | 43 | 43 / 43 | — | 1182 |
| erweiterung | 7 | 7 | 7 | 7 | 7 | — | — | 22 |
| esc | 25 | 25 | 25 | 25 | 25 | — | — | 235 |
| etikettbefehl | 18 | 18 | 18 | 18 | 18 | 4 / 4 | — | 307 |
| grafik | 3 | 3 | 3 | 3 | 3 | 3 / 3 | — | 76 |
| inhaltsfeld | 107 | 107 | 107 | 107 | 107 | — | 107 / 107 | 298 |
| sofortbefehl | 17 | 17 | 17 | 17 | 17 | — | — | 130 |
| sonstiges | 4 | 4 | 4 | 4 | 4 | — | — | 8 |
| speicherkarte | 7 | 7 | 7 | 7 | 7 | — | — | 39 |

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

## Eigenschaften und Aufträge (AP 5)

Alle 237 Definitionen und ihre 1.416 Parameterschemata sind erreichbar, mit Typ,
Einheit, Generation, Beschreibung und Wahlwerten. Alle Protokollvorlagen prüfen
die tatsächlichen Parameterbindungen über vier Generationen und unverändertes
Schreiben vorhandener gültiger Eingaben. Alle 107 Inhaltsfeldformulare besitzen
echte Argumentbindungen und Offline-Vorschau. Änderungen, optionale Ergänzungen,
Auftragsreihenfolge, Dateiaustausch und echte WPF-Bedienung haben zusätzliche Tests.
Antwortfelder bleiben lesbare Druckerausgaben; allgemeine Kontextregeln erzeugen
keine zusätzlichen Befehle. Variantenparameter setzen die passende Schrift,
Barcodeart oder übergeordnete Option voraus. Ressourcen werden als Dateien ersetzt.
Die Spalte bedeutet vollständige Erschließung der Definitionen, keine Schreibbarkeit
von Druckerantworten oder beliebiger widersprüchlicher Optionskombinationen.
Die 237 Properties-Ausnahmen wurden entfernt.

## Grafik (AP 6)

Alle 50 grafischen Definitionen einschließlich der 43 Barcodeprofile besitzen
ausführbare Nachweise für Einfügen, Verschieben, Drehen, Entfernen und bytegenaue
Erhaltung der Nachbarbereiche. Separate Erwartungswerte prüfen Grundlinie,
Kreiszentrum, Einheiten und Drehung. WPF-Tests rendern alle Vorlagen und prüfen
Bedienung, Bildformate, Schrift-/HTML-Effekte und Füllmuster. Echte unterstützte
Barcodes stammen aus ZXing.Net 0.16.11; nicht erzeugte Symbologien/Optionen bleiben
beschriftete Flächen berechneter Näherungsgröße. Code128 und QR werden rückgelesen.
Schriften und Klarschriftmetriken sind erklärte Windows-Näherungen. Die Spalte
behauptet keine druckergleiche Pixelwiedergabe oder Hardwareauswertung.
Die 50 Grafik-Ausnahmen sind entfernt.

## Verbleibende Integration

Nur die abschließende Projekt-/Release-/ZPL-Integration (1, AP 7) ist noch
zurückgestellt. Der Build erlaubt ausschließlich diese konkrete Ausnahme in
`tests/Fixtures/cab/coverage-deferrals.json`; keine Wildcards oder Duplikate.
AP 7 muss seinen tatsächlichen Nachweis ergänzen und die letzte Ausnahme entfernen.

## Korpus und lokales Handbuch

`tests/Fixtures/cab/` enthält eigene Dateien je Eintrag, eine ungültige Datei
je Barcodeprofil sowie Protokollgrenzen und die beiden bestehenden verlustfreien
Fixtures. Es werden keine zusätzlichen Herstellerexporte erfunden. Hersteller-PDF
und daraus lokal extrahierte Beispiele werden nicht eingecheckt. Der optionale
Handbuchtest und sein lokales Ergebnis werden getrennt vom synthetischen Nachweis
dokumentiert. Ein übersprungener Handbuchtest ist kein erfolgreicher Handbuchlauf.
