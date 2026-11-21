# Definitionen und Validierung – AP 2

Issue #257, Release R9. Basis: geprüfter Katalog aus AP 0 und verlustfreies
Dokument aus AP 1. Dieser erste Commit legt den Vertrag fest; die
Implementierung und ihre Prüfungen folgen im Entwurfs-PR.

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
