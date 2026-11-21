# Definitionen und Validierung – AP 2

Issue #257, Release R9. Basis: geprüfter Katalog aus AP 0 und verlustfreies
Dokument aus AP 1. Implementierung und Prüfnachweise gehören zu PR #258.

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

Die bisherige Zeichenfläche projiziert nur physische Befehle, deren vollständiger
Quellbereich und Millimetereinheit durch die logische Bindung bestätigt sind.
Durch ESC unterbrochene Felder bleiben vollständig in der Quelle; sie erscheinen
bis zur erweiterten Grafikbearbeitung nicht als abgeschnittene Einzelobjekte.
Ein `J` innerhalb eines Kommentars beginnt für die Projektion keinen Auftrag.
Für `C` wird die dokumentierte Grenze des ersten Schnittversatzes gegen die
Etikettenhöhe aus `S` geprüft, auch bei unterschiedlichen aktiven Maßeinheiten.
Pflichtfelder berücksichtigen die ausgewählte Syntax: Vektortext benötigt seine
Schriftgröße; eine individuelle Barcodehöhe mit Elementbreite benötigt keine
zusätzliche SC-Standardgröße. Bereits zuvor im selben Auftrag deklarierte
Feldnamen im Barcodeinhalt werden bis zur Inhaltsfeldauswertung als dynamisch
gekennzeichnet. Unbekannte Klammertexte bleiben wörtliche Daten.
Modell-/Versionskombinationen und Matrixgrößen werden auch bei dynamischem
Barcodeinhalt geprüft. Nur die von den ausgewerteten Daten abhängigen Zeichen-,
Längen-, Prüfziffern- und Kapazitätsprüfungen werden aufgeschoben.

## Prüfnachweise und Grenzen

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
Die verbindlichen Qualitätsgrenzen werden mit `build/coverage.ps1 -EnforceTargets`
geprüft; die konkreten Laufnachweise stehen im Pull Request. Die statische
Prüfung ersetzt keinen Test an der konkreten Druckerhardware. Dynamische
Inhaltsfelder werden in AP 3 ausgewertet.
