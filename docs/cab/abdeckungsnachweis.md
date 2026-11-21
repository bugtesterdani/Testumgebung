# Abdeckungsnachweis und Testkorpus – AP 8

Issue #261, Release R9, Meilenstein 18. Dieser erste Commit legt den Vertrag
für den früh eingeführten Abdeckungstest fest; Implementierung und Nachweise
folgen im Draft-PR. AP 3 ist mit PR #260 abgeschlossen.

## Verbindlicher Vergleich

Der Test liest den geprüften Repository-Katalog und vergleicht alle 237 IDs,
1.416 Parameter und 1.801 Werte mit den tatsächlich verfügbaren Definitionen.
Fehlende, zusätzliche oder ungeprüfte Definitionen sind Fehler. Eine explizite
Testzuordnung weist Parsen, Validieren und unverändertes Schreiben nach;
bloße Erkennung eines Tabellenworts ersetzt keinen ausführbaren Protokollfall.

Eigene `.lbl`-Dateien unter `tests/Fixtures/cab/` decken jeden Eintrag ab,
außerdem gültige/ungültige Barcodes und die Protokollgrenzen aus AP 8.
Hersteller-PDF und Herstellerbeispiele werden nicht eingecheckt.

## Frühe Einführung

Die ausdrücklich empfohlene Reihenfolge führt AP 8 vor AP 4–7 ein. Noch nicht
implementierte Eigenschaften-, Grafik- oder Integrationsfunktionen erhalten
deshalb konkrete, AP-bezogene Ausnahmen. Diese sind kein Abdeckungsnachweis.
Der Bericht zeigt tatsächlichen Stand und Ausnahmen getrennt. Neue Ausnahmen
oder wachsende Ausnahmemengen dürfen nicht unbemerkt grüne Tests ergeben;
die späteren APs entfernen ihre Ausnahmen. Am Ende muss die Liste leer sein.

`docs/cab/abdeckung.md` wird reproduzierbar aus denselben Daten erzeugt und
gegen veraltete Angaben geprüft. Pflichtspalten ohne begründete Ausnahme
müssen vollständig sein. Qualitätsgrenzen bleiben unverändert.

## Lokaler Handbuchtest

Nur `TESTUMGEBUNG_CAB_MANUAL` aktiviert die lokale Extraktion. Ohne PDF wird
der Test übersprungen. Die Daten bleiben lokal. Extraktionsfehler, gedruckte
Widersprüche und tatsächliche Parserfehler werden unterschieden, dokumentiert
und nicht als erfolgreich getestete gültige Beispiele ausgegeben.

Vor Merge: vollständiger Build, vollständige Tests, erfolgreiche GitHub-CI,
Ready for Review, Hinweise bearbeiten, Threads resolved und normaler
Merge-Commit ohne Squash/Rebase. Erst danach beginnt AP 4.
