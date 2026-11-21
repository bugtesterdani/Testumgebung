# Inhaltsfelder und Offline-Vorschau – AP 3

Issue #259, Release R9, Meilenstein 18. Grundlage sind das verlustfreie Dokument
aus AP 1, die Befehlsbindung aus AP 2 und die 107 geprüften Einträge des
Kapitels 5. Abschnitt 5.83 ist im Handbuch nicht vorhanden.

Die Implementierung wird in PR #260 geprüft, bevor AP 8 begonnen wird.

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

`CabContentParser` erkennt alle 107 Definitionen case-sensitiv. Der versionierte
Korpus `tests/Fixtures/CabJScript/content-fields.json` bindet jeden der 241
Katalogparameter, einschließlich gepackter Formate, Optionslisten, Bitlisten,
OPC-UA-Kennungen und des abweichend benannten SER-Startparameters. Bytebereiche
werden aus tatsächlich dekodierten Zeichen ermittelt: Ein Shift-JIS-Folgebyte
mit Wert `0x5B` ist keine öffnende Inhaltsfeldklammer. ESC-Zwischenräume gehören
nicht zu den Hervorhebungsbereichen.

Im cab-Editor öffnet „Inhaltsfeld-Vorschau …“ die hervorgehobenen Quelldatenfelder,
ihre Offlinewerte und Hinweise mit Handbuchverweis. Beispielzeit, Setup-Kultur,
Monats-Offsets, Wochenregel und Sichtbarkeitsregel werden als Projektmetadaten
gespeichert. `l` berücksichtigt die DOS-Ländercodes (insbesondere GR=Deutschland,
GK=Griechenland, SA=Südafrika). LA wird für die Vorschau als es-MX und SR als
sr-Latn-RS dargestellt; diese regionalen Varianten sind Vorschaukonventionen.
Jalali-Monatsnamen werden persisch dargestellt. JWDAY zählt in der Vorschau
Samstag=0. Quellen und Druckdateien werden dadurch nicht geändert.

Die Vorschau meldet nicht eindeutig festgelegte Regeln: Monate wahlweise als
30 Tage oder Kalendermonate; Offsets in Reihenfolge Tag, Monat, Jahr; Wochen
nach gewählter Regel; logisches AND gemäß Überschrift; ordinale Zeichenfolge-
vergleiche; Unicode-Skalarwerte für Länge/Teilstrings; Gebietskultur für Groß- /
Kleinschreibung. R:u/d bedeutet gegen ±∞, R:m halbe Werte von null weg;
R:n schneidet für die Anzeige Nachkommastellen ohne Rundung ab. D:m zählt
Gesamtziffern; Überbreite bleibt erhalten. Unspezifizierte Binärüberläufe,
negative Binärwerte, ungültige Unicode-Skalare und Teilbereiche außerhalb der
Daten liefern Diagnosen statt stiller Abschneidung. J bewahrt Ausrichtung und
physische Länge als Vorschau-Metadaten; es erzeugt keine erfundenen Leerzeichen.
SELECT zeigt den ausdrücklich bereitgestellten Beispielindex oder die
dokumentierte Vorauswahl und kennzeichnet dies als Offline-Beispielauswahl.
S ändert die Zifferndarstellung, nicht die intern referenzierten Datenwerte.
Binärwerte behalten ihre Bytefolge unabhängig von der lesbaren Hexdarstellung.
Die lokalen Darstellungsgrenzen sind keine behaupteten Druckergrenzen.

Der Korpus muss alle 107 Einträge und deren Parameter erfassen. Für jedes
offline berechenbare Feld sind ein erwarteter Vorschauwert und mindestens ein
Grenzfall erforderlich. Zusätzliche Prüfungen betreffen Verkettung, unzulässige
Verschachtelung, literale Klammern, Feldverweise, Teilstrings, Codepages und
unverändertes Speichern. UI-Prüfungen weisen Beispielzeit und Hervorhebung nach.

Vor dem Merge sind vollständiger Build, vollständige Tests und GitHub-CI
einschließlich unveränderter Qualitätsgrenzen nachzuweisen. Review-Kommentare
werden bearbeitet und die Threads resolved. Der Merge erfolgt ohne Squash und
ohne Rebase mit einem normalen Merge-Commit.
