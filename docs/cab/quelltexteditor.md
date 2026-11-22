# Quelltextansicht – AP 4

Issue #263, Release R9, Meilenstein 18. Ausgangspunkt ist der normale Merge
von AP 8 (#262). Dieser erste Commit beschreibt den Vertrag; der Draft-PR
wird vor Implementierung und Tests geöffnet.

Die Quelltextansicht bearbeitet das verlustfreie Dokument aus AP 1. Sie zeigt
Befehlsarten, Parameter und Inhaltsfelder unterscheidbar, bietet alle geprüften
Katalogeinträge und Werte zur Vervollständigung und erläutert Syntax,
Parameter, Generation und Handbuchseite. Binär- und undurchsichtige Blöcke
werden als geschützte Zusammenfassung dargestellt. Ihre Nutzbytes gelangen
nicht in eine editierbare Textdarstellung.

Diagnosen bleiben mit Originalposition und Handbuchseite verknüpft. Eine Liste
und Randmarkierungen führen zur betroffenen Stelle. Änderungen werden als
gezielte Byteersetzungen geschrieben; unveränderte Bereiche bleiben identisch.
Die vorhandene grafische Projektion wird abgeglichen, sodass unveränderte
Objekte ihre Identität behalten. Der Quelltext bleibt die maßgebliche Datei.

Tests müssen sämtliche Einträge und Tabellenwerte in der Vervollständigung,
ungültige Werte mit Seitenverweis, geschützte Blöcke, mehrere Codepages und
Änderungspositionen nachweisen. Ein Oberflächentest öffnet eine Datei, ändert
Quelltext und speichert sie; nur die betroffene Zeile darf sich unterscheiden.
Die 237 Source-Ausnahmen aus AP 8 werden erst nach diesen echten Nachweisen
entfernt. Die Rückstellungen für AP 5–7 bleiben sichtbar.

Vor Merge: vollständiger Build, vollständige Tests, unveränderte Qualitätsziele,
erfolgreiche finale GitHub-CI, Ready for Review und erledigte Review-Threads.
Normaler Merge-Commit ohne Squash/Rebase; erst danach AP 5.
