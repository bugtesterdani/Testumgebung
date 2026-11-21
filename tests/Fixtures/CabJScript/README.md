# Synthetische cab-Testdaten

Die LBL-Dateien wurden für die Tests erstellt; sie sind keine kopierten
Handbuchbeispiele. Die Binärbereiche gehören zur Verlustfreiheitsprüfung.

`parameter-values.json` enthält einen expliziten gültigen Wert für jeden der
1.175 Parameter außerhalb der Inhaltsfelder. Gemeinsame Regeln werden für
jeden betroffenen Katalogeintrag geprüft. Bei Auswahl- und Bereichsregeln
stehen zusätzliche ungültige Werte im Korpus. Die HTML-Tags im Parameter
`JS-4.17/text` sind Beispiele innerhalb frei formulierbaren HTML-Inhalts,
keine geschlossene Auswahl für den gesamten Text. Hier prüft der ungültige
Fall den fehlenden Pflichtwert statt ein beliebiges nicht aufgelistetes Wort.

Der Korpustest gleicht seine Schlüssel vollständig mit den Definitionen ab.
Ein fehlender, zusätzlicher oder doppelter Parameter lässt ihn scheitern.
Zusätzliche Tests prüfen die Erkennung jedes Tabellenwerts, konkrete
Befehlsbindungen, Generationen, Schriftkombinationen und Barcodegrenzen.
Ein gültiger isolierter Parameter beweist nicht die Gültigkeit einer beliebigen
Befehlskombination; dafür dienen die separaten semantischen Tests.
# Inhaltsfeldkorpus (AP 3)

`content-fields.json` enthält eigene synthetische Daten für alle 107 Einträge
des Kapitels 5. Der Test weist zusätzlich jeden der 241 Parameter über die
tatsächliche Argumentbindung nach. Erwartete Offlinewerte, Grenzen, regionale
Kalender, Hardwareplatzhalter und Quellbereiche stehen in den separaten
`CabContent*Tests`. Das Herstellerhandbuch und seine Beispiele werden nicht
als Testdateien übernommen.
