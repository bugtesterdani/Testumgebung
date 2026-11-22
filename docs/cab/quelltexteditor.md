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

## Bedienung und Grenzen

„Quelltext …“ öffnet die Ansicht im cab-Designer. Die Suche findet Befehle,
Parameter in Katalogreihenfolge und Tabellenwerte. Auswahl und Doppelklick
fügen an der markierten Textstelle ein. Strg+Leertaste führt zur Suche.
Syntaxvorlagen und schematische Werte bleiben erkennbar; sie behaupten noch
keinen vollständig gültigen Befehl. Antwortfelder sind beschrieben, werden
aber nicht als Eingabe eingefügt. Die Verfügbarkeit wird für die gespeicherte
Standardgeneration angezeigt. Handbuchseiten stehen in Hilfetext und Tooltips.

ESC wird eindeutig als `␛e` angezeigt, das wörtliche Zeichen `␛` als `␛u`.
Auch benachbarte ESC-Bytes und das tatsächlich gedruckte Symbol bleiben dadurch
unterscheidbar. Beim Bearbeiten wird die Darstellung in die Originalzeichen
zurückübersetzt. Unicode-Zeilen-/Absatztrenner in Druckdaten bleiben wörtlich.
Unveränderte Zeilen behalten ihre ursprünglichen CR-/LF-Enden. Neue Zeilen
verwenden das dokumentierte Zeilenende der Datei. Strenge Codepage-Kodierung
verhindert Ersatzzeichen. Nicht dekodierbare Fragmente bleiben ebenso wie
Binär- und verschlüsselte Blöcke geschützt; ihre Zusammenfassung nennt den
Grund beziehungsweise Typ, Namen und Bytezahl. BASIC-Blöcke bleiben Text.

Diagnosen stehen an der betreffenden Zeile und mit Seitenverweis in der Liste;
Doppelklick markiert den betroffenen Bereich. Eine Textänderung aktualisiert
das bestehende Layout und die Eigenschaften. Unveränderte Modellobjekte und
ihre Canvas-Darstellungen werden wiederverwendet, auch bei verschobenen
Bytepositionen oder gelöschten Nachbarbefehlen. Darstellungsumfang und die
ausführliche Eigenschaftenansicht werden in AP 5 und AP 6 erweitert.

Alle 237 Korpusdateien werden zusätzlich im Quelltextmodell geöffnet und
bytegleich geschrieben. Vervollständigung wird für alle 237 Einträge, 1.416
Parameter und 1.801 Werte unter allen vier Generationen nachgewiesen. Die
237 Source-Rückstellungen aus AP 8 entfallen; 288 konkrete Rückstellungen
für AP 5–7 bleiben bestehen.
