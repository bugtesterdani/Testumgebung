# Grafische JScript-Bearbeitung und Vorschau – AP 6

Issue #267, Release R9, Meilenstein 18. AP 5 (#266) ist mit normalem Merge
abgeschlossen. Dieser erste Dokumentationscommit legt den Vertrag für den
Entwurfs-PR fest; Implementierung und Tests folgen erst danach.

Die grafische Ansicht muss alle 50 grafischen Definitionen einschließlich aller
43 Barcodeprofile erschließen. Grundlage bleiben die verlustfreien Quelldaten
und die tatsächlichen Parameterbindungen. Einfügen, Verschieben, Drehen,
Größenänderung und Löschen dürfen keine fremden Befehlsbytes normalisieren.
Mehrere Aufträge werden einzeln ausgewählt und angezeigt.

Etikettgröße, Abstand, Sensor und Spalten kommen aus S; D und darstellungsbezogene
O-Optionen bestimmen den globalen Versatz, Spiegelung und Drehung. Positionen,
Einheiten, Druckauflösung und Drehpunkte müssen nachvollziehbar in die
Begrenzungsrahmen der Vorschau eingehen.

T umfasst alle katalogisierten Schriften als ausdrücklich erklärte Bitmap-/
Vektorschrift-Näherung, darstellbare Effekte, Rahmen und inverse Darstellung.
W umfasst Breite, Höhe, Zeilenabstand und die katalogisierten HTML-Auszeichnungen.
G umfasst Kreis/Ellipse, Linien mit Endformen und abgerundete Rechtecke sowie
Strich-, Füllungs-, Umriss- und Schattenoptionen. I verwendet die tatsächlichen
Downloadressourcen und die Faktoren mx/my.

Barcodevorschauen müssen maßstäblich sein. Eine lizenzrechtlich geklärte
Bibliothek soll unterstützte Symbologien wirklich erzeugen. Wo die konkrete
Symbologie oder Optionsvariante nicht erzeugt werden kann, steht eine klar
beschriftete Fläche mit berechneter Größe. Sie wird nicht als scannbarer Code
ausgegeben. Neue Abhängigkeiten benötigen eine genaue Versionsbindung,
Originallizenzen und einen aktualisierten Lizenzbericht.

Die Vorschau nutzt bestehende Beispielwerte für geschweifte Platzhalter und die
Offline-Inhaltsfelder aus AP 3. Hardwareabhängige Werte bleiben gekennzeichnete
Beispiele. Bestehende ZPL-Abläufe dürfen dadurch nicht verändert werden.

Tests müssen für jede grafische Definition tatsächliches Einfügen, Verschieben
und Entfernen in gültigen Dateien und die Erhaltung fremder Bytes nachweisen.
Begrenzungsrahmen sichern Positionen und Drehungen ab. Erst dann werden die
50 Grafik-Rückstellungen entfernt; die AP-7-Rückstellung bleibt bestehen.

Vor Merge: vollständiger Build und vollständige Tests, unveränderte
Qualitätsgrenzen, erfolgreiche exakte GitHub-CI, Ready for Review und erledigte
Review-Threads. Normaler Merge ohne Squash/Rebase; erst danach AP 7.
