# Dateien, Druckbaustein und bestehende Projekte – AP 7

Issue #269, Release R9, Meilenstein 18. AP 6 (#268) wurde mit normalem
Merge-Commit d3b67a8f0c36f624bcd9ee55fe84a7e1615ffaa1 abgeschlossen.
Dieser erste Dokumentationscommit legt den Integrationsvertrag fest.
Implementierung und Tests folgen nach Eröffnung des Entwurfs-PR.

## Quelldatei und Druck

„LBL öffnen …“, „LBL speichern …“ und das Ablegen einer .lbl-Datei müssen
denselben verlustfreien Dokumentpfad verwenden. Unverändertes Speichern ist
byteidentisch, einschließlich Codepagewechseln, Zeilenenden und Binärblöcken.
Der Druckbaustein speichert die Datei weiterhin in CabLblDaten.

Platzhalter werden ausschließlich in tatsächlichen Text-/Datenparametern der
Syntaxknoten ersetzt. Eine geschweifte Klammer in einer Grafikdatei oder einem
anderen opaken Block löst keine Ersetzung aus. Neue Vorlagen benötigen keinen
CabLblTextOffset. Alte Projekte bleiben lesbar; ihre unveränderten eingebetteten
Bytes werden abgesehen von gezielten Platzhalterersetzungen unverändert gesendet.
Text, Barcode und Rich Text erhalten dieselbe sichere Parameterauswahl.

## Migration und Übernahme

Bestehendes CabLblLayout wird beim Öffnen einmalig durch den bisherigen Renderer
in JScript überführt und danach als Quelle bearbeitet. Dies geschieht an der
Arbeitskopie. Erst „Vorlage übernehmen“ speichert Quelle und eingebettete Datei
im Projekt. Abbrechen verändert weder Projekt noch bestehende Release-Daten.
ZPL-Verhalten und ZPL-Tests bleiben erhalten.

## Projektprüfung und Nachweise

JScript-Diagnosen erscheinen im Projekt-Validierungsbericht. Fehler verhindern
die Freigabe; Warnungen bleiben sichtbar und verhindern sie nicht. Die Prüfung
führt keine Drucker-, Datei-, RFID- oder sonstigen Gerätebefehle aus.

Tests müssen bestehende CabLblDaten-/CabLblLayout-Projekte, Druckbytes,
Platzhalter in Text/Barcode/Rich Text, binäre Klammern, Codepages sowie
Öffnen/Speichern/Ablegen/Abbrechen/Übernehmen tatsächlich nachweisen.
Der letzte Integrationsaufschub wird erst nach diesen ausführbaren Nachweisen
entfernt. docs/cab/abdeckung.md wird aus den Tests erzeugt; der KI-Katalog wird
ausschließlich durch den echten Projektformat-Exporter aktualisiert.

Das Entwicklerhandbuch beschreibt den vollständigen Katalogstand nach Ausgabe
07/2026, die Generationsauswahl, alle drei Ansichten und Inhaltsfelder neben
Platzhaltern. Hardwareabhängige Werte, Schrift-/SC-Näherungen und nicht erzeugte
Barcodevarianten bleiben ausdrücklich als Grenzen der Offline-Vorschau benannt.

Vor Merge gelten vollständiger Build und Tests, unveränderte Qualitätsgrenzen,
erfolgreiche exakte GitHub-CI, Ready for Review und erledigte Review-Threads.
Abschluss erfolgt per normalem Merge ohne Squash oder Rebase. Dieser Feature-PR
schließt nicht das Release-Planungs-Issue und veröffentlicht kein Release.
