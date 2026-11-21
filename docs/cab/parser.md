# Verlustfreies cab-JScript-Dokument – AP 1

Issue #255, Release R9. Aufbauend auf dem geprüften Katalog aus AP 0.

Das Dokumentmodell arbeitet mit Originalbytes und absoluten Bytebereichen.
Unbekannte Befehle, ungültige Kodierungen, Leerraum und Zeilenenden müssen
auch dann erhalten bleiben, wenn keine grafische Darstellung möglich ist.
Der Schreiber ersetzt nur ausdrücklich geänderte Bereiche; überlappende
Änderungen werden zurückgewiesen. Neue Zeilen verwenden das vorherrschende
Zeilenende, bei Gleichstand das zuerst vorkommende, bei leerer Datei CRLF.

Binärdownloads und verschlüsselte Bereiche werden nicht als JScript-Text
interpretiert. Nicht eindeutig begrenzte verschlüsselte Inhalte bleiben als
undurchsichtiger Rest erhalten. Ein fehlender Abschluss liefert eine Diagnose
und darf keine nachfolgenden Bytes verwerfen.

Codepage und Maßeinheit gehören zur Position im Dokument. Die Standard-
Codepage der Editoroptionen ist UTF-8; eine ungültige Dekodierung ändert die
Bytes nicht. Inhaltsfelder erhalten eigene Quellbereiche; ihre Auswertung
folgt in AP 3. Die vollständige Befehlsvalidierung folgt in AP 2.

`CabParser` und `CabDocument` liegen in der UI-unabhängigen Domain-Bibliothek.
`CabDocument.Write()` liefert eine Kopie der Originalbytes; `Write(changes)`
wendet absolute Byteänderungen an. `Replace(node, text)` kodiert strikt mit der
Codepage des Knotens und ersetzt nicht darstellbare Zeichen nicht still.
Parameter enthalten Quellbereiche für Klammerfelder, noch ohne Auswertung.
Aufträge sind Gruppen im Dokument; Sofortbefehle und ESC-Befehle außerhalb
bleiben eigenständige Knoten. Übertragungsrahmen von Downloads werden getrennt
von den Nutzbytes behandelt. In ESC-Punkt-Blöcken sind verdoppelte ESC Nutzdaten,
während einzelne ESC-Anfragen eigene Unterknoten erhalten. ESC-Doppelpunkt-
Blöcke schalten die innere ESC-Auswertung aus.

Der bisherige `CabLabelImport.Read` verwendet diesen Baum. Seine alte
Zeilen-/Binärleseschleife und die Beschränkung auf einen vollständigen Auftrag
entfallen. Die bestehende Zeichenfläche zeigt zunächst nur die bisher
darstellbaren Objekte des ersten Auftrags in Millimetern. Die übrigen Bytes
bleiben im gespeicherten `CabImportedSource` erhalten. Eine Änderung eines
projizierten Wertes ersetzt nur dessen Parameterbereich. Nicht darstellbare
Inhalte werden weder gelöscht noch aus einer Teilmenge neu erzeugt. Neue
grafische Objekte benötigen vorerst einen abgeschlossenen Millimeterauftrag;
die umfassende grafische Darstellung folgt in AP 6. Typisierte Eigenschaften
und Quelltextansicht folgen in AP 5 beziehungsweise AP 4.

Die bisherigen Druck-Platzhalter verwenden einen zusammenhängenden UTF-8-
Textabschnitt mit CR-Zeilenenden. Importierte Dokumente mit anderen Kodierungen,
Zeilenenden, Inhaltsfeldern oder späteren Binär-/Blockdaten werden für diesen
alten Mechanismus nicht freigegeben und bytegenau gedruckt. Die allgemeine
quellbereichsbasierte Druckintegration folgt in AP 7. Eigene vorhandene Layouts
behalten ihren bisherigen Platzhaltermechanismus.

Die Tests enthalten 2.000 zufällige Bytefolgen, 100 zufällige Katalogfolgen,
gezielte Rahmen-/Kodierungs-/Änderungsprüfungen und eigene synthetische
LBL-Dateien unter `tests/Fixtures/CabJScript`. Keine Handbuchbeispiele sind
in diesen Korpus übernommen. Die lokalen Handbuchseiten bleiben außerhalb
des Repositories. Vollständiger Build, Tests und CI sind für diesen AP
gesondert vor dem Merge zu prüfen.
