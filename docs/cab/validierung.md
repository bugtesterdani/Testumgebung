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
Binärparameter sind über diese Textschnittstelle nicht bearbeitbar.

Im cab-Designer öffnet „Generation und Prüfung …“ die Generationsauswahl für
den Vorspann und jeden Etikettenauftrag sowie die statischen Diagnosen.
`LabelLayout.CabSettings` speichert die Auswahl unabhängig von den Druckbytes.
Fehlende Einstellungen bestehender Projekte bedeuten X4. Der ZPL-Designer
erhält diese Auswahl nicht.

## Noch offene Nachweise im Entwurfs-PR

Der vollständige positive und negative Testkorpus je Parameter und Tabellenwert,
weitere Barcode-Kapazitätskombinationen sowie die abschließende Qualitätsprüfung
sind noch nicht abgeschlossen. Die vorhandenen Definitionen allein belegen
diese Abdeckung nicht. AP 2 ist deshalb noch nicht zur Überprüfung freigegeben.
