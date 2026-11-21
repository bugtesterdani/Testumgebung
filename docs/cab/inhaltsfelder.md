# Inhaltsfelder und Offline-Vorschau – AP 3

Issue #259, Release R9, Meilenstein 18. Grundlage sind das verlustfreie Dokument
aus AP 1, die Befehlsbindung aus AP 2 und die 107 geprüften Einträge des
Kapitels 5. Abschnitt 5.83 ist im Handbuch nicht vorhanden.

Dieser erste Commit legt den Vertrag fest. Die Implementierung und ihre
Prüfnachweise folgen im Entwurfs-PR, bevor AP 8 begonnen wird.

## Quelle und Grammatik

Inhaltsfelder erhalten ihre Katalog-ID, Parameter, Werte und ursprünglichen
Bytebereiche. Die Erkennung berücksichtigt die aktive Codepage und die
vorhandenen disjunkten Textbereiche um ESC-Steuerbefehle. Die Auswertung
ändert keine Quelldaten. Verkettete Felder sind zulässig; ineinander gesetzte
Optionen werden nach Handbuch diagnostiziert. Unbekannte Klammertexte bleiben
wörtlicher Text und erhalten einen Hinweis. Benannte Feldverweise und ihre
Teilstrings werden von unbekannten Klammertexten unterschieden.

Alle Diagnosen nennen Quellbereich, Katalog-ID und Handbuchseite. Ungültige
Parameter oder nicht auswertbare Werte liefern eine Diagnose und einen
gekennzeichneten Vorschauzustand; sie dürfen keine Quellbytes verwerfen.

## Vorschau

Die Vorschau verwendet eine einstellbare Beispielzeit und ausdrücklich
bereitgestellte Beispielwerte. Sie sendet keine Druckerbefehle. Zeit-/Datums-
Offsets, Jalali und Suriyakati richten sich nach den geprüften Handbuchregeln.
Rechen-, Vergleichs-, Prüfziffern-, Formatierungs- und Zeichenkettenfunktionen
sowie Binär-/Basisumwandlungen werden in der Domain-Bibliothek ausgewertet.
Benannte Felder verwenden die zuvor im jeweiligen Auftrag definierten Werte.
Für SER wird der erste Wert dargestellt; es wird kein Druckzähler verändert.

Druckerabhängige RFID-, Datenbank-, OPC-UA-, Speicher-, Protokoll-, ABC-,
Display- und Job-ID-Felder erscheinen als entsprechend gekennzeichnete
Platzhalter. Ein solcher Platzhalter behauptet keinen gelesenen Hardwarewert.
Hervorhebungsbereiche stehen der Vorschau und dem späteren Quelltexteditor
aus AP 4 zur Verfügung. Die vollständige Grafikunterstützung bleibt AP 6.

Prüfvariablen in geschweiften Klammern sowie die bisherigen Escapeformen
`{{` und `}}` bleiben erhalten. Die bestehende Sperre gegen eckige Klammern
in eingesetzten Variablenwerten bleibt im Druckbaustein bestehen. Vorschauwerte
werden nicht als neue JScript-Quelle in die Druckdatei zurückgeschrieben.

## Nachweise

Der Korpus muss alle 107 Einträge und deren Parameter erfassen. Für jedes
offline berechenbare Feld sind ein erwarteter Vorschauwert und mindestens ein
Grenzfall erforderlich. Zusätzliche Prüfungen betreffen Verkettung, unzulässige
Verschachtelung, literale Klammern, Feldverweise, Teilstrings, Codepages und
unverändertes Speichern. UI-Prüfungen weisen Beispielzeit und Hervorhebung nach.

Vor dem Merge sind vollständiger Build, vollständige Tests und GitHub-CI
einschließlich unveränderter Qualitätsgrenzen nachzuweisen. Review-Kommentare
werden bearbeitet und die Threads resolved. Der Merge erfolgt ohne Squash und
ohne Rebase mit einem normalen Merge-Commit.
