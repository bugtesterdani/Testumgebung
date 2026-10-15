# Betriebsmodus, Diagnose und externe Laufzeitdateien

Die Anwendung startet standardmäßig im Produktivmodus. Eine fehlende oder beschädigte `%ProgramData%/Testumgebung/zugang.json` gewährt keine automatischen Entwicklungs- oder Freigaberechte. Produktive erweiterte Rechte erfordern bestätigte Entra-Anmeldung. Ohne Zugangsdatei kann die lokale Entwicklung ausdrücklich im Anmeldepanel über **Lokal anmelden (Entwicklungsmodus)** aktiviert werden; alternativ mit `Testumgebung.Desktop.exe --development` starten. Bei vorhandener oder beschädigter Zugangskonfiguration steht dieser Einstieg nicht zur Verfügung. Fehlgeschlagene Anmeldungen lassen das Panel geöffnet, damit die Ursache sichtbar bleibt.

Für den lokalen Auftragsablauf `Start-Programme.cmd` im Projektordner öffnen (`Start-A123.cmd` funktioniert weiterhin). Der gesonderte Start `--development --example-order` lädt die Beispielprogramme A, B, C und D sowie den C#-Auswahlablauf ohne zusätzliche Einrichtung, verwendet ausschließlich simulierte Geräte und zeigt A123 im Auftragsfeld. A123, B123, C123 und D123 wählen das entsprechende Programm (entscheidend ist der erste Buchstabe). **Start** oder **Enter** führt die Auswahl aus; pro Loop wird eine neue Seriennummer erfasst. Nach Abbruch erneut einen FA scannen; damit kann auch das Programm gewechselt werden. C und D verwenden zunächst dieselbe simulierte Stromprüfung wie B. Die gespeicherte Produktionskonfiguration und bestehende Freigabepakete werden dabei nicht ersetzt. Ältere eigene Freigaben müssen nach Anpassung an den Auftragsvertrag erneut geprüft und freigegeben werden.

Unerwartete Dispatcher-Fehler werden unter `%LOCALAPPDATA%/Testumgebung/diagnose/fehler-*.json` mit Zeitpunkt, Anwendungsversion und vollständiger Ausnahme protokolliert. Maximal 50 Protokolle bleiben erhalten. Die Anwendung fordert Abbruch an und wartet auf Exit-/Speicherfolge und sicheren Gerätezustand. Scheitert dieser Abschluss, bleibt die Oberfläche gesperrt und zeigt den Fehler. Fehlerprotokolle können Dateipfade und technische Fehlermeldungen enthalten.

Die Freigabeaufnahme erfasst zusätzlich vorhandene NI-DAQmx-/VISA-/CAN-Treiberdateien in System32 und LabVIEW-Runtime-DLLs unter den üblichen NI-Verzeichnissen. Weitere erforderliche Herstellerdateien werden administrativ unter `%ProgramData%/Testumgebung/laufzeitdateien.json` eingetragen:

```json
[
  { "name": "LabVIEW-Prüfmodul", "path": "C:\\Pruefung\\Spannung.dll" },
  { "name": "Herstellertreiber", "path": "C:\\Hersteller\\Treiber.dll" }
]
```

Jede Datei erhält eine feste Version und SHA-256-Bindung im Freigabepaket. Fehlende konfigurierte Dateien blockieren die Aufnahme/Prüfung; geänderte Inhalte erfordern neue Entwicklungsprüfung und Freigabe. Gleiche logische Namen können auf anderen Stationen auf andere absolute Pfade zeigen, sofern Inhalt und Version identisch sind. Die Liste muss alle für den konkreten Prüfablauf benötigten externen Binärdateien enthalten. Die Inventarisierung ersetzt keine Funktionsprüfung am Gerät oder eine tatsächliche LabVIEW-VI-Abnahme.

Integrationstests für externe Datenbankserver werden bei fehlendem Verbindungsstring ausdrücklich als übersprungen gemeldet. Ein grüner Testlauf mit übersprungenen Tests ist kein Server-Abnahmenachweis.


## Release-Meldungen nach einem Softwareupdate

Eine Prüfprogramm-Freigabe hält die Versionen und Prüfsummen ihrer Laufzeitumgebung
fest. Ein Update der Testumgebung kann diese Komponenten ändern. Die Release-Ansicht
meldet dann, dass Freigaben nicht vollständig geladen werden konnten. Unter
**Details anzeigen** stehen die betroffenen Komponenten in einem begrenzten,
scrollbaren Textfeld. Der vollständige Text lässt sich markieren und kopieren;
Programmwahl und Release-Liste bleiben erreichbar.

Bei einer bestätigten Laufzeitabweichung den bisherigen Prüfablauf unter der neuen
Software überprüfen, einen Entwurf mit neuer Prüfprogramm-Version anlegen, die
Entwicklungsprüfung durchführen und ausdrücklich freigeben. Bestehende Pakete bleiben
unverändert. Die kompakte Anzeige hebt die Laufzeit- und Integritätsprüfung nicht auf.
Andere Ursachen wie eine unerreichbare Ablage anhand der Details beheben.
