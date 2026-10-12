# Bildnachweis

`14-zwei-pruefplaetze.png`: Ergänzung vom 12.10.2026 für Version 1.0.10.0, WPF-Doppelansicht mit zwei Beispielaufforderungen bei 1280 × 900. Der Oberflächentest erzeugt zusätzlich eine 1920 × 1080-Version unter `artifacts/verification/paired-stations-1920.png`. Die Antworten beider Seiten wurden im Test getrennt bestätigt; die Messwerte im Bild sind Beispieldaten.

13 PNGs aus tatsächlichen WPF-Ansichten der Quellversion 1.0.3.0, erzeugt am 10.10.2026 mit Beispielkonfiguration, jeweils 1920 × 1080 Pixel. Seitenansichten ohne äußeren Fensterrahmen; kein Nachweis realer Hardware- oder Produktionsläufe. Kein Bild wurde aus einer erfundenen Oberflächenbeschreibung generiert.

Die Reihenfolge der Dateinamen entspricht den technischen Seiten; die Bildnummern im Benutzerhandbuch folgen dessen Kapitelreihenfolge.

Reproduktion im Projektordner mit vorhandenem .NET SDK und Windows/WPF:

```powershell
$env:TESTUMGEBUNG_HANDBUCH_BILDER = Join-Path (Get-Location) 'docs/handbuch/bilder'
dotnet test tests/Desktop.UiTests/Desktop.UiTests.csproj --filter FullyQualifiedName~DevelopmentWorkspaceTests
```

Der optionale Bildgenerator liegt im vorhandenen WPF-Prüfharness. Für das DLL-Bild wird die nach dem C#-DLL-Handbuch gebaute Beispiel-DLL unter `artifacts/verification/dll-handbuch/bin/Release/net10.0/MeinePruefbibliothek.dll` verwendet. Er verändert keine Produktionskonfiguration und speichert keine echten Programmfreigaben. Die Beispielbilder enthalten lokale Pfade des Entwicklungsrechners, keine produktiven Zugangsdaten.
