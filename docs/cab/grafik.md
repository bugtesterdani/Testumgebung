# Grafische JScript-Bearbeitung und Vorschau – AP 6

Issue #267, Release R9, Meilenstein 18. AP 5 (#266) ist mit normalem Merge
abgeschlossen. Der erste Dokumentationscommit und Entwurfs-PR wurden vor der
Implementierung angelegt.

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
G umfasst Kreis/Ellipse, Linien mit Endformen und Rechtecke sowie
Strich-, Füllungs-, Umriss- und Schattierungsoptionen. Die im Auftrag als
Eckenradien bezeichneten Werte ht/vt sind laut Hersteller horizontale bzw.
vertikale Strichstärken; die Ansicht erfindet dafür keine Radiusparameter.
S ist ein Dichteverlauf und kein versetzter Schlagschatten. I verwendet die tatsächlichen
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

## Bedienung und Grenzen

Im cab-Designer öffnet „Vollständige cab-Grafik …“ die Ansicht. Die Auftragsauswahl
trennt mehrere J-Aufträge; Einfügen verwendet die eigene Katalogvorlage vor dem
A-Abschluss. Auswahl, Ziehen, Positions-/Drehungsfelder, Größenfelder und Löschen
ändern gebundene Parameter. Rückgängig/Wiederholen gilt für Quelltextänderungen.
Für Bilder wird die Datei als Download eingebettet und über I referenziert.
Die bestehende Eigenschaftenansicht erschließt zusätzliche Optionen und S/D/O.
Die numerischen Eingaben verwenden die Einheit des gewählten Befehls und einen
Dezimalpunkt. Einfügen in Zoll-Aufträge rechnet die Vorlagenlängen um; SC-Werte
bleiben Standardgrößen. Feste Barcodegrößen und Matrix-Proportionen werden nicht
durch erfundene Parameter umgangen; die Ansicht meldet die Beschränkung.
Die druckauflösungsabhängigen SC-Standardmaße werden ausdrücklich angenähert;
eine druckergleiche Rasterung und Klarschriftmetrik wird nicht behauptet.

Beispielwerte ändern nur die private Vorschau, nicht die Quelldatei. Vorhandene
objektbezogene Beispiele haben Vorrang; globale Beispiele und ältere Kennungs-
Vorschauwerte bleiben nutzbar. Inhaltsfelder werden danach durch die Offline-
Auswertung aus AP 3 ausgewertet. Fehlende Hardwarewerte bleiben gekennzeichnet.

Benutzermuster user1–user4 benötigen einen vorherigen 32×32-Download. Fehlende
Muster werden ausdrücklich als graue Ersatzfüllung gemeldet. Ohne expliziten dx
verwendet eine mehrspaltige Vorschau die Etikettenbreite als erklärte Näherung.
Barcodes mit nicht implementierter Optionsvariante erhalten eine beschriftete
Ersatzfläche. Inverse Darstellung und Rahmen beziehen die Klarschrift ein;
die Klarschrift verwendet eine erklärte 3-mm-Windows-Näherung.

Ersetzungen durch R werden dem benannten ursprünglichen Grafikfeld desselben
Auftrags zugeordnet. Unsichtbare Inhalte unterdrücken die vollständige gedruckte
Grafik einschließlich inverser Fläche, Klarschrift und Rahmen; im Editor bleiben
sie zur Bearbeitung auswählbar. Die Größenübernahme einer Linie zieht die
Strichstärke von ihrer Begrenzungsbreite ab und ist bei unveränderten Maßen stabil.

Die gepinnten Encoder besitzen feste Balkenverhältnisse:
[Code39Writer](https://github.com/micjahn/ZXing.Net/blob/v0.16.11.0/Source/lib/oned/Code39Writer.cs)
verwendet 2:1,
[ITFWriter](https://github.com/micjahn/ZXing.Net/blob/v0.16.11.0/Source/lib/oned/ITFWriter.cs)
verwendet 3:1. Andere verlangte lineare Verhältnisse werden nicht als echter Code
behauptet. Ihre beschrifteten Ersatzflächen berücksichtigen das Verhältnis in
der ausdrücklich angenäherten Breite. PDF417 verwendet nur die vom Encoder
unterstützten ganzzahligen Zeilenverhältnisse von 1 bis 4.
