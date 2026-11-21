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

Diese Beschreibung legt den Vertrag für die Implementierung fest. Parser,
Schreiber, Importanbindung und Akzeptanztests sind in diesem ersten Commit
noch nicht umgesetzt. Der PR bleibt bis zu deren Prüfung ein Entwurf.
