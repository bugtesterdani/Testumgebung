# Testabdeckung und Wartbarkeit messen

`Report-Coverage.cmd` im Projekt starten. Voraussetzungen: Windows, PowerShell 7, .NET 10 SDK, bei der ersten Ausführung NuGet-Zugriff. ReportGenerator 5.5.11 wird projektlokal unter `artifacts/tools/reportgenerator` installiert. Coverlet 10.1.0 ist eine private Testabhängigkeit, Microsoft.NET.Test.Sdk 18.5.1 wird für den aktuellen Collector verwendet. Keine zusätzlichen Bibliotheken im Produktivprogramm.

Jeder Lauf erzeugt `artifacts/coverage/<Datum_Uhrzeit>/report/`:

- `index.html`: echter ReportGenerator-Bericht mit Modulen, Klassen, Zeilen, Zweigen, Quellcode und Risikostellen.
- `Wartbarkeit.html`: Modulübersicht mit Wartbarkeitsbewertung.
- `Funktionen.html`: durchsuchbare Übersicht jeder gemessenen Funktion mit Komplexität, Sequenzpunktabdeckung und CRAP; Filter für Komplexität über 50 und CRAP ab 30.
- `Qualitaetsziele.html`, `Qualitaetsziele.json`: tatsächliche Werte und Prüfung gegen 90 % Zeilen, 85 % Zweige sowie maximal 50 Komplexität und strikt weniger als 30 CRAP pro Funktion.
- `Wartbarkeit.md`: Erläuterung, Grenzen und wichtigste Methoden.
- `Module-Wartbarkeit.csv`, `Methoden-Wartbarkeit.csv`: Kennzahlen zum Weiterverarbeiten.
- `Summary.json`, `Summary.csv`, `Summary.md`: ReportGenerator-Ausgaben.
- Im Nachbarordner `raw`: OpenCover/Cobertura und TRX als Messnachweise.

Zeilenabdeckung bedeutet ausgeführten Code, nicht bestandene Tests. Wartbarkeit wird über zyklomatische Komplexität und Abdeckung komplexer Methoden eingeordnet; kein frei erfundener Prozentwert und kein Microsoft Maintainability Index. Die Richtwerte ≥15 Kontrollflusswege und <80 % Sequenzpunktabdeckung sind dokumentierte interne Priorisierung, keine Produktfreigabe.

Beide Testprojekte werden nacheinander gemessen und zusammengeführt. Testcode und generierter Code unter `obj` sind ausgeschlossen; Auto-Properties bleiben enthalten. Das Skript instrumentiert zusätzlich `Workers.CSharp` und `Workers.Native` in den Worker-Unterordnern des Engine-Testprojekts. Damit umfasst der Standardreport alle 14 .NET-Module. Ein direkter Solution-Testaufruf ohne diese Zusatzkonfiguration erfasst weiterhin nur 12 Module. Tatsächliche Hardware, externe Entra-Dienste und Datenbankserver werden durch die lokale Abdeckung nicht bestätigt.

Ohne konfigurierte externe Datenbanken werden vier Prüfungen übersprungen. SSH/SFTP wird nur mit einer lokalen Python/Paramiko-Gegenstelle aktiviert:

```powershell
$env:PYTHONPATH = 'C:\Pfad\zu\Paramiko-Paketen'
pwsh -File build/coverage.ps1 -PythonForSsh 'C:\Pfad\zu\python.exe'
```

Fehlschlagende Tests führen zu einem Fehlerstatus; vorhandene Messdaten werden trotzdem als Bericht ausgegeben. Bisherige Berichte bleiben erhalten.

Die Zielwerte können zusätzlich als automatische Prüfschranke verwendet werden:

```powershell
pwsh -File build/coverage.ps1 -EnforceTargets
```

Der Bericht wird auch bei verfehlten Zielen geschrieben. Danach liefert das Skript einen Fehlerstatus. Die Prozentgrenzen und die CRAP-Grenze werden mit ungerundeten Werten geprüft. CRAP wird je Funktion aus C² × (1−Sequenzpunktabdeckung)³ + C berechnet. Eine geringe Komplexität allein garantiert die CRAP-Grenze nicht; die Funktion muss ausreichend getestet sein. Die maximale Modulkomplexität bezeichnet die komplexeste gemessene Funktion des Moduls, nicht die Summe aller Funktionen. Compilererzeugte Zustandsmaschinen von asynchronen Funktionen bleiben in der Messung enthalten. Eine bestandene Komplexitätsprüfung gilt für die gemessenen Module; separat laufende Worker werden ausdrücklich ausgewiesen.

Erste Messung: 10.10.2026, 341 bestanden, 5 übersprungen, 0 fehlgeschlagen; 82,7 % Zeilen und 63,9 % Zweige. Besonders niedrige Abdeckung: Desktop 26,8 %, Instruments 69,7 %. Das Ergebnis ist eine Ausgangsbasis für gezielte Tests und Vereinfachung komplexer Methoden.

Werkzeugdokumentation: [Coverlet](https://github.com/coverlet-coverage/coverlet) und [ReportGenerator](https://github.com/danielpalme/ReportGenerator).


Historische Messung am 11.10.2026 (`artifacts/coverage/2026-10-11_145443/report/`): 93,659833 % Zeilen, 85,064974 % Zweige, höchste Funktionskomplexität 50 und höchster CRAP 51,380917. Alle 14 Produktionsmodule sind erfasst; 5.829 Tests bestanden, vier externe Datenbankprüfungen übersprungen, keine Fehler. Der damalige Lauf bestand die damals geltende CRAP-Grenze von maximal 55; seit Issue #3 gilt die strengere Grenze CRAP < 30.


Die aktuelle CRAP-Grenze ist exklusiv: 30,0 gilt als verfehlt. Auch Werte unmittelbar unter oder über 30 werden vor dem Runden geprüft. `Methoden-Wartbarkeit.csv` enthält dazu `CRAPExact`; der Filter in `Funktionen.html` verwendet den daraus berechneten Status. Grenzfälle werden mit `build/tests/verify-coverage-goals.ps1` geprüft, einschließlich Werten, die in der Anzeige beide auf 30,0 gerundet werden.

Der GitHub-Build prüft die Ziele für Pull Requests, main und Releases vor der Paketierung. Der vollständige Bericht einschließlich Rohdaten und Testprotokollen liegt als Actions-Artefakt `Qualitaetsbericht-<Version>` bereit. Verfehlte Ziele verhindern den erfolgreichen Build und damit den Merge über die bestehende main-Schutzregel. Die bereits geprüften TRX-Protokolle werden auch dem MSIX-Artefakt beigefügt.

Abschlussmessung zu Issue #3 am 13.10.2026 (artifacts/coverage/2026-10-13_214037/report/): 94,020993 % Zeilenabdeckung, 85,656709 % Zweigabdeckung und höchster CRAP-Wert 29. Alle 14 Produktionsmodule sind gemessen; keine Funktion erreicht CRAP 30 und keine überschreitet Komplexität 50. 6.001 Tests bestanden, fünf Prüfungen mit nicht konfigurierten externen Gegenstellen wurden übersprungen, keine Fehler. Der vollständige Lauf mit -EnforceTargets hat alle Ziele bestanden. Es wurden keine zusätzlichen Ausschlüsse zur Erfüllung der Ziele eingeführt.
