# Mitgelieferter C++-Compiler und GDB

Die Windows-x64-Ausgabe enthält w64devkit 2.8.0 mit GCC 16.1.0 und GDB 17.1 unter `toolchains/w64devkit`. Es wird weder eine globale Installation noch eine Änderung des systemweiten PATH benötigt. Der C++-Worker verwendet diesen Compiler automatisch, sofern kein anderer Compiler ausdrücklich konfiguriert wurde. Nur der gestartete Worker erhält den passenden PATH.

## Aufbau und Prüfung

Auf dem Entwicklungsrechner einmal `powershell -ExecutionPolicy Bypass -File build/acquire-toolchain.ps1` ausführen. Der Download verwendet festgelegte SHA-256-Prüfsummen für Binärpaket und korrespondierende Quellen. Anschließend prüft `build/test-toolchain.ps1` Kompilierung, Programmausführung, GDB-Breakpoint und Variablenabfrage. Beide Werkzeuge werden aus dem Projektordner gestartet.

`build/publish.ps1 -SelfContained` erstellt eine Ausgabe einschließlich .NET-Laufzeit, C++-Toolchain, GDB, originaler Lizenzdateien und Quellarchiv. Ohne `-SelfContained` wird weiterhin die passende .NET-Desktop-Laufzeit benötigt. Die portable Python-Laufzeit wird ebenfalls mitgeliefert. Gerätespezifische Treiber sind weiterhin separat erforderlich.

## Stand der Debugger-Anbindung

An einem C++-Zeilenhaltepunkt verbindet sich die Anwendung über GDB/MI mit dem pausierten Worker und liest echte lokale Variablen sowie den nativen Callstack. Diese erscheinen im Debug-Reiter mit dem Präfix `GDB:`. Auch Structs ohne C++-Streamoperator werden unterstützt. Nach der Inspektion wird GDB wieder abgetrennt; Fortsetzen, nächste Codezeile und Abbruch sind in der Oberfläche vorhanden. Die nativen Callbacks verwenden jetzt ABI 3; der Cache wird durch den geänderten generierten Quelltext automatisch erneuert.

Die Zeilenhalte bleiben instrumentiert. GDB steuert keine eigenständigen Funktionshaltepunkte und verändert weder Register noch Variablen. Die Frame-Überbrückung ist für die mitgelieferte GNU-x64-Toolchain umgesetzt; bei anderen Compilern kann nur die vorhandene Instrumentierung verfügbar sein. Ein fehlgeschlagener Inspektionsversuch erscheint als `GDB: Diagnose`. Die GDB/MI-Kommandos folgen der offiziellen Dokumentation: https://sourceware.org/gdb/current/onlinedocs/gdb.html/GDB_002fMI-Stack-Manipulation.html

## Weitergabe

Originale Lizenztexte und `source/source.tar` gehören zur Auslieferung und dürfen nicht aus Platzgründen entfernt werden. Das Quellarchiv enthält die korrespondierenden Quellen der Distribution. Die eigene Anwendung bleibt ein separater Prozess gegenüber den Compiler-/Debugger-Werkzeugen. Lizenzpflichten der eigenen C++-Bibliotheken und erzeugten Programme müssen zusätzlich berücksichtigt werden.

Offizielle Bezugsquelle: https://github.com/skeeto/w64devkit/releases/tag/v2.8.0

