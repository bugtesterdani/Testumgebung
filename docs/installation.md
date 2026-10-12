# Installation und Pilotbetrieb

## Voraussetzungen je Prüfrechner

| Komponente | Pflicht | Hinweis |
|---|---|---|
| Windows 10/11 x64 | ja | Anmeldung mit Firmenkonto über den Windows-Kontomanager (WAM) |
| .NET 10 Desktop Runtime x64 | ja (außer bei self-contained Installer) | Installer prüft und warnt |
| Python 3.14.8 | mitgeliefert | keine separate Installation; eigener Interpreter/venv über `TESTUMGEBUNG_PYTHON` |
| MSVC Build Tools (oder clang++/g++) | nur für C++-/LabVIEW-Bausteine | fester Compiler über `TESTUMGEBUNG_CXX` |
| Keysight IO Libraries Suite oder NI-VISA | nur für USB/GPIB-Geräte | LAN-Socket (`TCPIP::host::5025::SOCKET`) geht ohne VISA |
| NI-DAQmx | nur für NI-DAQ-Module | ohne Hardware: simuliertes Gerät in NI MAX |
| SQL-Datenbank | empfohlen | SQL Server, PostgreSQL, MySQL/MariaDB oder SQLite; Schema legt die Anwendung an (`docs/sql/`) |

## Paket bauen (Entwicklungsrechner)

```powershell
powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Version 1.0.0 -Installer
```

* führt alle Tests aus (Abbruch bei Fehlern), veröffentlicht nach `artifacts\publish\Testumgebung` (mit `workers\csharp`,
  `workers\python`, `workers\native` und `docs\`), schreibt `artifacts\publish\SHA256SUMS.txt`;
* `-Installer` erzeugt mit Inno Setup 6 `artifacts\installer\Testumgebung-Setup-1.0.0.exe`;
* `-SelfContained` bündelt die .NET-Laufzeit (größer, keine Runtime-Installation nötig).

## Installation (Administrator)

1. `Testumgebung-Setup-<Version>.exe` als Administrator ausführen (Ziel: `C:\Program Files\Testumgebung`).
2. Der Installer legt `%ProgramData%\Testumgebung` an und setzt die Rechte: **Administratoren und SYSTEM Vollzugriff,
   Benutzer nur lesen.** Damit können nur Administratoren Prüfplätze, Geräte und Zugang ändern.
3. Anwendung als Administrator starten → **System & Sicherung → Systemprüfung**. Fehler beheben, Warnungen bewusst
   akzeptieren oder beheben.
4. Einrichten (siehe Administratorhandbuch): Zugang (`zugang.json`, `docs/entra-registrierung.md`), Prüfplätze und Geräte
   (`pruefplaetze.json`, `docs/geraete.md`), Auswahl-/Auftragsablauf, Datenbank und Ergebnisordner (Einstellungen, je Benutzer).
5. **System & Sicherung → Sicherung erstellen** und die ZIP-Datei außerhalb des Rechners ablegen.

Deinstallation entfernt keine Konfiguration, Ergebnisdateien, Outbox oder Zwischenaufzeichnung.

## Ablageorte

| Ort | Inhalt | Wer schreibt |
|---|---|---|
| `%ProgramData%\Testumgebung\pruefplaetze.json` | Geräte, Prüfplätze, Zuordnung, Auswahl-/Auftragsablauf | Administratoren |
| `%ProgramData%\Testumgebung\zugang.json` | Entra-App, Rechte je Konto, Inaktivitätssperre | Administratoren |
| `%LOCALAPPDATA%\Testumgebung\einstellungen.json` | Ergebnisordner, Datenbank, Outbox, MQTT (Geheimnisse per DPAPI) | Benutzer |
| `%LOCALAPPDATA%\Testumgebung\Outbox` | noch nicht übertragene Runs | Anwendung |
| `%LOCALAPPDATA%\Testumgebung\Zwischenaufzeichnung` | Journal je laufendem Prüfling (Absturzsicherung) | Anwendung |
| `%LOCALAPPDATA%\Testumgebung\Wiederherstellung` | Ergebnisdateien, wenn der Ergebnisordner nicht erreichbar war | Anwendung |
| `%LOCALAPPDATA%\Testumgebung\Releases` | Release-Pakete ohne SQL-Datenbank | Anwendung |
| `Dokumente\Testumgebung\Ergebnisse` (Standard) | Ergebnisdateien `.tres` | Anwendung |

Werker arbeiten unter ihrem eigenen Windows-Konto. Für mehrere Werker an einem Rechner einen gemeinsamen Ergebnisordner
(Netzlaufwerk) und eine gemeinsame Datenbank einstellen – sonst liegen Ergebnisse und Releases je Benutzerprofil.

## Unvollständige Prüfläufe (Absturz, Stromausfall)

Jeder laufende Prüflingstest wird nach jedem Ereignis in ein Journal geschrieben. Nach erfolgreicher Speicherung wird es
gelöscht. Bleibt eines zurück, zeigt die Werkeransicht einen Hinweis; unter **Ergebnisse → Unvollständige Prüfläufe**
entscheidet ein Entwickler/Administrator:

* **Als abgebrochen speichern:** Ergebnisdatei und Datenbank mit Bewertung ERROR und Vermerk; Filter des Ablaufs gelten;
  Kurven werden nicht zwischengespeichert.
* **Verwerfen:** Journal wird nach `…\Zwischenaufzeichnung\verworfen` verschoben (nachvollziehbar, nicht gelöscht).

Es wird nicht automatisch weitergeprüft; der Prüfling ist neu zu prüfen.

## Sicherung und Wiederherstellung

**System & Sicherung**: ZIP mit `einstellungen.json`, `zugang.json`, `pruefplaetze.json` und `sicherung.json`
(Prüfsummen, Rechner, Benutzer). Beim Wiederherstellen werden Prüfsummen und JSON geprüft, die aktuellen Dateien als
`*.vor-wiederherstellung-<Zeit>` aufbewahrt und die Dateien atomar ersetzt. Prüfplätze gelten sofort, Einstellungen und
Zugang nach Neustart. DPAPI-geschützte Kennwörter sind nur für dasselbe Konto auf demselben Rechner lesbar – auf einem
anderen Rechner unter **Einstellungen** neu eingeben. Projekte und Release-Pakete liegen in der Datenbank bzw. im
Release-Ordner und werden dort gesichert.

