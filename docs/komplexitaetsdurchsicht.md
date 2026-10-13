# Komplexitätsdurchsicht vom 13.10.2026

Die vollständige Messliste aus `artifacts/coverage/2026-10-13_221136/report/Methoden-Wartbarkeit.csv` enthält 6.000 Funktionen aus 14 Produktionsmodulen. Alle Einträge wurden anhand ihrer Komplexität priorisiert. 5.174 besitzen Komplexität höchstens 6; ein weiterer Umbau allein wegen dieser Kennzahl ist dort nicht begründet. Die Quellcodeprüfung konzentriert sich auf die komplexeren Abläufe. Eine metrische Sichtung ist ausdrücklich keine individuelle Lesbarkeitsprüfung jeder einzelnen Funktion.

Die zusätzliche lokale Auswertung `Komplexitaetspruefung.html` und `Komplexitaetspruefung.csv` im neuen Coverage-Bericht führt jede gemessene Funktion mit Modul, Kennzahlen, Quelldatei, Zeile und Bewertungsstatus auf. Sie unterscheidet automatische Priorisierung, konkrete Quellcodeentscheidungen und weitere Kandidaten. Compilererzeugte Zustandsmaschinen und lokale Funktionen bleiben sichtbar. Die Auswertung erhebt keinen Anspruch, dass jeder automatisch priorisierte Eintrag bereits im Quellcode abschließend beurteilt ist.

## Umgesetzte Vereinfachungen

| Ablauf | Zweck der Änderung |
| --- | --- |
| Release-Ansicht | Programmliste und Wiederherstellung der Release-Auswahl sind eigene Phasen. |
| Bibliotheksvergleich | Codebibliothek und grafische Bausteine werden separat verglichen; Ausgabereihenfolge bleibt erhalten. |
| Sperrreihenfolge | Ressourcen und zusätzliche Sperrkanten werden separat ermittelt; Rekursion und Zyklenschutz bleiben zusammen. |
| Prüfplatz-Ereignisse | Livekurve, Identifikatoren und Ergebnisanzeige sind getrennte Aufgaben. |
| Prüfstart | Vorbereitung der Anzeige und ihr Abschluss sind benannte Phasen; Dispatcher und Sicherheitsabschluss bleiben erhalten. |
| Geräteausgabe | Pro Kanal werden Kanalname, Einheit und Wertebereich zusammen geprüft; Zeitbasis und Speicher folgen separat. |
| Serielle Einstellungen | Paritätsbezeichnungen werden an einer Stelle übersetzt und geprüft. |
| SCPI-Blöcke | Die wiederholte Prüfung auf ausreichend übertragene Bytes nutzt dieselbe Regel mit unveränderten Fehlermeldungen. |

## Bewusst beibehalten

Kurze Filterregeln (`StatisticsFilter.Matches`, `RunQueries.Matches`), konstante PCAN/Kvaser-Bitratentabellen, Modbus-Adressbereiche und Typumwandlungen (`FromVariant`, `StorableValue`, `ReadValue`, `TypedValue.Parse`) bleiben zusammen. Ihre vielen Fälle erhöhen die Messzahl, sind aber unmittelbar lesbar. Zusätzliche Handler oder Tabellen würden hier vor allem Indirektion hinzufügen. Auch der kompakte Telnet-Zustandsautomat und der rückwärts laufende C++-Bezeichnerscanner bleiben erhalten.

## Weitere plausible Kandidaten

| Funktion | Mögliche fachliche Trennung | Besonders zu erhalten |
| --- | --- | --- |
| `VectorXlBackend.Read` | Classic-CAN und CAN-FD getrennt dekodieren | Speicherlayouts, Zeitlimit und Ereignisbehandlung |
| `DaqmxDevice.CaptureAsync` | Konfiguration, Blocklesen und Deinterleaving | Abbruch, Task-Cleanup und Sample-Reihenfolge |
| `SessionService.SignInAsync` | Rechteprüfung und Übernahme der Identität | Generation beim Abmelden und finally-Benachrichtigung |
| `WaveformPlotViewModel.Render` | Achsenbereich und Min/Max-Dezimierung | Sichtfenster, Spitzenwerte und Punktreihenfolge |
| `CurveGenerator.Generate` | Einstellungen prüfen und Kanal erzeugen | Fehlertexte und endliche Samplewerte |
| `ResultAnalysis.MeasurementsOf` | Messwerte sammeln und Statistik bilden | Grenzwerte, Einheiten und bisherige Reihenfolge |
| `CodeBlockConverter.Analyze` | Zugriffsrichtung einzelner Variablen bestimmen | lokale Deklarationen und zusammengesetzte Zuweisungen |
| `ResultFile.DecodeChunks` | einzelne Chunk-Typen dekodieren | Fehlerisolation, Strukturreferenzen und Reihenfolge |

Diese Kandidaten sind keine Forderung nach einem beliebigen niedrigeren Zielwert. Vor dem jeweiligen Umbau muss der Lesbarkeitsgewinn gegen zusätzlichen Kontext und Zustandsübergaben abgewogen werden. Die verbindlichen Ziele bleiben Zeilenabdeckung mindestens 90 %, Zweigabdeckung mindestens 85 %, Komplexität höchstens 50 und ungerundeter CRAP strikt unter 30. Keine zusätzlichen Messausnahmen.

## Messnachweis nach den Änderungen

Vollständiger Lauf mit `build/coverage.ps1 -EnforceTargets`, Bericht `artifacts/coverage/2026-10-13_223004/report/`: 6.012 gemessene Funktionen in 14 Modulen, höchste Komplexität 28, höchster ungerundeter CRAP 28,867932, Zeilenabdeckung 94,024916 %, Zweigabdeckung 85,655009 %. 6.001 Tests bestanden; fünf Tests für nicht konfigurierte externe Gegenstellen übersprungen; keine Fehler.

| Ausgangsfunktion | Komplexität vorher | Komplexität nachher |
| --- | ---: | ---: |
| `ReleasesViewModel.Rebuild` | 28 | 1 |
| `ProjectDiff.CompareLibrary` | 28 | 1 |
| `StationViewModel.OnStepCompleted` | 28 | 10 |
| `StationViewModel.RunProjectCoreAsync` | 26 | 18 |
| `LockOrderAnalysis.Visit` | 26 | 16 |
| `OutputSuitability.Check` | 28 | 16 |
| `SerialSettings.From` | 28 | 16 |
| `ScpiSession.CompleteBlock` | 28 | 22 |

Die Werte gelten für die jeweilige Ausgangsfunktion. Die neuen Teilfunktionen bleiben vollständig in der Messung und im Bericht; die Tabelle behauptet keine Senkung der Summe aller Kontrollflusswege. Der höchste CRAP-Wert insgesamt bleibt gegenüber der vorherigen Messung unverändert, obwohl mehrere einzelne Funktionen verständlicher und weniger komplex geworden sind.
