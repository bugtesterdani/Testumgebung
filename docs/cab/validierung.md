# Definitionen und Validierung – AP 2

Issue #257, Release R9. Basis: geprüfter Katalog aus AP 0 und verlustfreies
Dokument aus AP 1. Die Implementierung entsteht im Entwurfs-PR #258;
dieses Dokument unterscheidet vorhandene Funktionen von offenen Nachweisen.

Der versionierte Katalog wird unverändert als Ressource in die Domain-
Bibliothek eingebettet. Jede Definition behält ihre Katalog-ID, Seiten,
Generationsangaben, Parameter und vollständigen Wertelisten. Antwortfelder
werden nicht als erforderliche Eingabeparameter behandelt. Verweise und
übergeordnete Befehle bleiben von eigenen ausführbaren Befehlen unterscheidbar.

Typisierte Regeln ergänzen Datentypen, Einheiten, Bereiche, Standards,
Pflichtangaben und Kombinationen. Eine unbekannte oder widersprüchliche
Handbuchangabe begründet keine erfundene harte Beschränkung. Diagnosen
ändern niemals den Quelltext. Unbekannte oder fehlerhafte Originalbytes
bleiben über Parser und Schreiber erhalten.

Jede Diagnose trägt Schwere, Quellbereich, Zeile/Spalte, Katalog-ID und
Handbuchseite. Nicht verfügbare Generationen und Positionen außerhalb des
Etiketts sind Warnungen. Die Generation ist je Auftrag auswählbar und wird
im Projekt gespeichert; Vorgabe ist X4.

Vollständigkeitstests müssen alle Definitionen, Parameter und Tabellenwerte
erfassen. Gültige Werte sowie unzulässige Bereichs-/Auswahlwerte, Generationen,
Feldnamen, Barcodedaten, Schriftkombinationen, Pfade und Objektgrenzen werden
geprüft. Eine vorhandene Definition allein gilt nicht als Nachweis einer
vollständigen Validierungsregel.

## Vorhandene Schnittstellen

`CabCatalog.Default` liefert den eingebetteten Katalog einschließlich unveränderter
Metadaten. `CabDefinitions.Default` ergänzt typisierte Regeln für alle Einträge
außer den Inhaltsfeldern aus AP 3. `CabValidator.Validate` bindet Befehle an ihre
ursprünglichen Bytebereiche und liefert Diagnosen ohne Änderung der Quelle.
ESC-Steuerbefehle unterbrechen dabei weder logische Parameter noch mehrbyteige
Zeichen derselben Codepage. `CabParameterEditor.Replace` erstellt Änderungen
für die zugehörigen Textbereiche und erhält dazwischenliegende Steuerbefehle;
Opaque Binärblöcke sind über diese Textschnittstelle nicht bearbeitbar.

Im cab-Designer öffnet „Generation und Prüfung …“ die Generationsauswahl für
den Vorspann und jeden Etikettenauftrag sowie die statischen Diagnosen.
`LabelLayout.CabSettings` speichert die Auswahl unabhängig von den Druckbytes.
Fehlende Einstellungen bestehender Projekte bedeuten X4. Der ZPL-Designer
erhält diese Auswahl nicht.

## Noch offene Nachweise im Entwurfs-PR

Der versionierte Parameterkorpus umfasst 1.175 gültige Beispiele und zusätzliche
ungültige Auswahl-, Bereichs- und Formatwerte. Jeder Tabellenwert besitzt einen
separaten Erkennungstest. Semantische Tests prüfen unter anderem alle numerisch
dokumentierten Objektfamilien, vorgelagerte Downloads mit der Generation des
jeweiligen Auftrags, Barcode-Zeichenvorräte und globale Kapazitätsgrenzen.
Für DataMatrix und rMQR werden zusätzlich die dokumentierten Größen- und
Kapazitätszeilen geprüft. Micro QR VERSION1 wächst automatisch; bei festen
Versionen darf die Fehlerkorrektur reduziert werden. Widersprüchliche Zeilen
ergeben keine erfundene harte kleinere Grenze. Gemischte Kodiermodi erhalten
eine konservative obere Grenze; nicht-ASCII-QR-Inhalte werden nicht allein
anhand der Byte-Tabelle abgewiesen, weil Kanji-/Mischkodierung möglich ist.
Die abschließende Qualitätsprüfung ist noch nicht abgeschlossen. AP 2 ist
deshalb noch nicht zur Überprüfung freigegeben. Die statische Prüfung ersetzt
keinen Test an der konkreten Druckerhardware; dynamische Inhaltsfelder werden
in AP 3 ausgewertet.
