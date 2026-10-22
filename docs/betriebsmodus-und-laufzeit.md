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


## Automatische Anpassung an Monitor und Fenster

Das Hauptfenster passt seine Ansicht automatisch an die verfügbare Fensterfläche
und die Arbeitsfläche seines aktuellen Monitors an. Beim Verschieben zwischen
Monitoren wird der Monitor verwendet, auf dem der größte Teil des Fensters liegt.
Ein fest ausgewähltes „Display 1“ ist dafür nicht maßgeblich. Taskleiste und die
Windows-Skalierung des aktuellen Fenstermonitors werden berücksichtigt.

Bei wenig Platz werden Text, Abstände und Bedienelemente gemeinsam kompakter.
Auf großen Flächen bleibt die normale Schriftgröße erhalten und die Arbeitsbereiche
bekommen mehr Platz. Auch ein manuell kleiner gezogenes Fenster wird berücksichtigt.
Die zusätzliche Layoutskalierung liegt zwischen 65 und 100 Prozent; die persönliche
Windows-DPI-Skalierung kommt unabhängig davon hinzu. Bei noch kleineren Flächen
sind die Inhalte scrollbar, damit Steuerelemente erreichbar bleiben. Beim Start
und beim Wechsel auf eine kleinere Arbeitsfläche wird ein normales Fenster auf
die verfügbare Größe begrenzt. Eine zusätzliche Konfiguration ist nicht erforderlich.


## Lokale Benutzer einrichten und Aussperrung vermeiden

Die lokale Anmeldung ist eine ausdrücklich aktivierte **Entwicklungssimulation ohne
Passwortprüfung**. Für bestätigte Firmenkonten im Produktivbetrieb Microsoft Entra
verwenden. „M. Weber“ ist ein vorausgefüllter Simulationsname, kein festes
Administratorkonto.

Bei der ersten Anmeldung wird das aktuelle Entwicklerkonto für die erste
Zugangskonfiguration vorgeschlagen. Es muss beim Speichern sein Recht **Entwickeln**
behalten. Ein Speicherversuch, der ihm dieses Recht innerhalb derselben Anmeldeart
entzieht, wird vor dem Schreiben abgelehnt; Datei und Sitzung bleiben unverändert.
Zum Entfernen des ursprünglichen Kontos zuerst ein weiteres Entwicklerkonto
speichern, sich damit anmelden und danach das alte Konto entfernen.

Lokale Namen wie `Tester` werden als `tester@simuliert.local` gespeichert.
Ungültige und nach der Normalisierung doppelte Anmeldenamen werden abgelehnt.
Beim Speichern im lokalen Entwicklungsmodus wird die ausdrücklich lokale
Konfiguration festgehalten. Nach einem Neustart im Anmeldefenster den zugeordneten
Namen eingeben und **Lokal anmelden (Entwicklungsmodus)** wählen. Es gelten dann
nur dessen gespeicherte Rechte. Unbekannte Konten erhalten keinen Zugang.

### Bereits ausgesperrte ältere lokale Testinstallation wiederherstellen

Diese Schritte gelten ab der korrigierten Softwareversion und nur für eine lokale
Testinstallation. Die maschinenweite Zugangsdatei liegt unter
`%ProgramData%\Testumgebung\zugang.json`; ein Zurücksetzen der paketbezogenen
App-Daten über Windows setzt diese Datei nicht zuverlässig zurück.

1. Die Software schließen. Ein berechtigter Windows-Administrator erstellt zuerst
   eine separate Sicherung von `zugang.json`; vorhandene Sicherungen beibehalten.
2. Bei einer bewusst lokalen Konfiguration ohne Client-ID und Mandanten-ID,
   mit Rechtequelle `Lokal` und einem vorgesehenen Benutzer mit `develop: true`,
   auf oberster JSON-Ebene `"allowLocalDevelopment": true` ergänzen. Die
   Benutzerliste und die vorgesehenen Rechte beibehalten; den gespeicherten
   Anmeldenamen prüfen. Für lokale Namenseingabe muss er dem normalisierten
   Namen entsprechen, beispielsweise `tester@simuliert.local`.
3. Mit der korrigierten Version starten, genau dieses Konto eingeben und die
   lokale Entwicklung im Anmeldefenster ausdrücklich aktivieren.

Bei einer Entra-Produktivkonfiguration stattdessen die Firmenanmeldung und deren
Rechtezuordnung administrativ korrigieren. Der lokale Schalter wird bei Entra-
Einstellungen, beschädigter Konfiguration oder fehlendem Entwicklerkonto nicht
als Wiederherstellungszugang angeboten. Es gibt keinen automatischen
Administrator-Fallback auf „M. Weber“.
