# Generische Eigenschaften und Aufträge – AP 5

Issue #265, Release R9, Meilenstein 18. AP 4 (#264) ist normal gemergt.
Dieser erste Commit beschreibt den Vertrag; Implementierung und Tests folgen
erst nach Erstellung des Draft-PR.

Alle 237 Definitionen und ihre 1.416 Parameter müssen in generischen Formularen
erreichbar sein. Typ, Einheit, Syntax, Beschreibung, Wahlwerte, Generation und
Handbuchseite kommen aus demselben geprüften Katalog. Optionale Parameter werden
auf Wunsch eingeblendet. Nicht verfügbare Werte bleiben erklärt und deaktiviert.
Als Antwortfelder katalogisierte Ausgaben werden nicht zu Eingabebefehlen
umgedeutet. Tatsächliche Eingaben werden über ihre Bytebereiche bearbeitet.

Die Auftragsübersicht enthält auch nichtgrafische, unmittelbare und ESC-Befehle,
Downloads, Kommentare und Blöcke. Hinzufügen, Entfernen und Umsortieren müssen
die Befehlsreihenfolge prüfen. Binärdaten werden über Ressourcenoperationen
ersetzt; sie gelangen nicht in ein normales Textfeld. Alle katalogisierten
Downloadtypen sind aufgelistet. Bildformate, die WPF unterstützt, erhalten eine
Vorschau; andere Ressourcen eine eindeutige Typ-/Größenanzeige.

Ein Inhaltsfelddialog erschließt alle 107 Felder in Gruppen mit Parameterformular
und der Offline-Vorschau aus AP 3. Er ergänzt die vorhandenen Platzhalter.

Tests weisen die vollständigen Formularschemata, die tatsächliche Bearbeitung
von Eingaben, unveränderte Nachbarbytes, Generationen, Reihenfolge, Ressourcen
und Inhaltsfelder nach. Erst dann werden die 237 Eigenschaften-Ausnahmen aus
AP 8 entfernt; die 51 Rückstellungen für Grafik und Integration bleiben sichtbar.

Vor Merge: vollständiger Build, vollständige Tests, unveränderte Qualitätsgrenzen,
erfolgreiche finale GitHub-CI, Ready for Review und erledigte Review-Threads.
Normaler Merge ohne Squash/Rebase; erst danach AP 6.
