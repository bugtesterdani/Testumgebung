# Maßnahmen und Nachprüfung der Lizenzbefunde

Stand: 13.10.2026. Ausgangspunkt: [historischer Prüfbericht](pruefbericht.md).
Der Rechteinhaber hat MIT für seinen eigenen Code ausdrücklich gewählt.

| Befund | Maßnahme | Status |
| --- | --- | --- |
| Eigene Lizenz fehlt | Root-LICENSE mit MIT und Rechteinhaber Daniel (bugtesterdani); Drittanbieter getrennt | umgesetzt |
| OPC Core GPLv2/RCL | Gesamte OPC-Familie auf 1.5.378.182, Original-MIT-Texte aus den tatsächlich verwendeten Paketen; Zertifikats-/Passwort-API angepasst | Build und 47 Oberflächentests bestanden |
| Native SQL-SNI-Bedingungen | Native Assets ausgeschlossen, offiziell unterstütztes Managed Networking aktiviert; echte TCP-Verbindungsprobe ohne SNI-Modul | Test bestanden; neue Distribution wird zusätzlich auf Dateiebene geprüft |
| Ungeklärte libtommath.dll | Optionale DLL aus der unterstützten Python-Embed-Laufzeit ausgeschlossen, ursprüngliches Downloadarchiv unverändert | Standardmodule einschließlich SSL, ctypes, SQLite, Decimal, Expat, Kompression bestanden; keine Tcl/Tk-Zusage |
| xunit.abstractions 2.0.3 | Explizite Apache-2-Angabe im eigenen Repository der eingefrorenen Abstractions gesichert, mit eigenem Original-Lizenztext | Nachweis ergänzt; ausschließlich Tests |
| Fehlende Originalhinweise | Vollständige Sammlung und Root-Lizenz im Publish; Buildprüfung bricht bei fehlenden Mindestnachweisen ab | umgesetzt |
| GNU-/LGPL-Quellen | Passendes vollständiges source.tar, Dockerfile und Anpassungen bleiben enthalten; SHA256 im Build geprüft | umgesetzt, bei jeder Weitergabe beibehalten |
| Microsoft-Anteile in Python | Original-Zusatzbedingungen bleiben erhalten; THIRD-PARTY-TERMS.md wird in App und externe Python-Kopie mitgegeben; Zustimmung vor erster Nutzung, erneut bei Textänderung | technisch umgesetzt; verbindliche Rechtsprüfung nicht ersetzt |
| Sicherheitsbefunde | SQLite auf Microsoft.Data.Sqlite 10.0.12 / SQLitePCLRaw 2.1.12; OPC auf obigen Stand | aktualisiert; erneute NuGet-Abfrage ohne gemeldete verwundbare Pakete |
| Linux-Dependency-Restore | EnableWindowsTargeting für den reinen Restore | umgesetzt; Anwendung weiterhin Windows/WPF |
| Reproduzierbarkeit | SDK 10.0.401 festgelegt, 20 NuGet-Lockdateien; CI-Locked-Mode; Inventar und SHA-Nachprüfung aus dem jeweiligen Publish-Stand | umgesetzt |
| Historische Downloads | Alte Versionen sind nicht rückwirkend bereinigt | Für Weitergabe ausschließlich nachgeprüften neuen Stand verwenden |

## Belege und Funktion

Die bestehende Engine-Suite besteht mit 5.924 erfolgreichen Tests und 5 übersprungenen
Umgebungsfällen (externe Datenbanken/optionaler SSH-Dienst). Die Oberfläche besteht
mit 47 Tests einschließlich der neuen Managed-SQL-Prüfung. Ohne erreichbaren realen
SQL-Server bzw. realen OPC-UA-Server ist dies keine Hardware-/Server-Abnahme.

Die SQL-Verbindungsprobe reserviert einen lokalen, nicht lauschenden TCP-Port;
eine SqlException ist das erwartete Ergebnis. DllNotFoundException und das Laden
eines SqlClient.SNI-Moduls würden den Test fehlschlagen lassen.
Fünf zusätzliche Fälle prüfen den Zustimmungsnachweis (aktuell, veraltet, leer,
fehlend und mit Zeilenende). Die UI-Test-App ersetzt die interaktive Zustimmung
durch eine Testfunktion und verändert keine echte Benutzer-Zustimmung.
Die Managed-SNI-Auswahl ist [von Microsoft dokumentiert](https://learn.microsoft.com/en-us/sql/connect/ado-net/appcontext-switches?view=sql-server-ver17).

Die ursprüngliche Abstractions-Lizenzlücke wird nicht mit irgendeiner aktuellen
xUnit-Lizenz überdeckt: Das Repository `xunit/abstractions.xunit` bezeichnet genau
seinen Bestand als eingefrorene xUnit-2.0-Abstractions und nennt Apache 2.
Der gesicherte [README-Beleg](../../licenses/third-party/nuget/xunit.abstractions/2.0.3/UPSTREAM-LICENSE-DECLARATION.md)
ist auf Commit `f2543476df0d5d30a23f65528b87871fed8edc4c` festgelegt.
Der Original-Lizenztext stammt aus demselben Abstractions-Commit. Der Vergleich
mit Tag `2.0.3` enthält ausschließlich README, Lizenz- und Build-/Projektdateien,
keine Änderungen an den C#-Abstractions-Quelldateien. Nutzungsgrundlage ist die
ausdrückliche Erklärung für diesen unveränderten Bestand, keine Behauptung,
dass das alte NuGet rückwirkend eine eingebettete Lizenzdatei enthielte.

## Verbleibende Pflichten

- Bei Weitergabe alle zugehörigen Hinweise und GNU-/LGPL-Quellen erhalten.
  LGPL-Rechte auf Änderungen/Relinking dürfen nicht durch eigene Bedingungen eingeschränkt werden.
- Die Microsoft-Binäranteile von Python behalten ihre besonderen Bedingungen.
  Die Umsetzung der Zustimmung ist ein technischer Nachweis, keine Garantie der
  rechtlichen Wirksamkeit in jeder Vertrags-/Vertriebssituation.
- WPF-/NET-Dateien werden unverändert aus offiziellen Runtimepacks mit den
  Original-Paketlizenzen weitergegeben. Das Inventar unterscheidet weiterhin deren
  Paketdeklaration von nativen Microsoft-Produktmetadaten.
- Importierte Nutzer-DLLs, zusätzliche pip-Pakete und externe Hersteller-Treiber
  sind separat zu prüfen. Ihre Importfunktion erteilt keine Lizenz.
- Neue Abhängigkeiten oder Versionen erfordern ein neues Inventar und Prüfung.
  Die Prüfung macht keine Zusage zur Freiheit von unbekannten Marken-/Patentrechten.
- Das Repository bleibt privat. Eine öffentliche Freigabe erfolgt nicht automatisch.

## Release-Nachprüfung

Der neue Bundle-Stand und seine vollständigen Versions-/Dateilisten werden nach
Fertigstellung in [Inventar](inventar.md) und [Rohdaten](inventory.json) ausgewiesen.
Der Build blockiert native SNI-Dateien, libtommath.dll, ungeprüfte OPC-Versionen,
bekannt beanstandete SQLite-Versionen, fehlende Root-Texte und abweichende Kit-Quellen.

## Lokaler Release-Ablauf mit aktuellem Inventar

Ein neues Release verwendet eine noch nicht vorhandene vierteilige Version.
Das GitHub-Verfahren führt diese Schritte bereits automatisch aus. Lokal:

```powershell
$releaseVersion = "1.0.14.0"
./build/msix.ps1 -Version $releaseVersion -PrepareOnly
$releaseBundle = "artifacts/msix/$releaseVersion/Testumgebung_$($releaseVersion)_x64.msixbundle"
python build/audit-dependencies.py --layout artifacts/publish/Testumgebung --bundle $releaseBundle
python build/enrich-license-inventory.py
python build/create-complete-manual.py
./build/update-license-layout.ps1
./build/msix.ps1 -Version $releaseVersion -UsePreparedLayout
python build/verify-release-inventory.py --bundle $releaseBundle
```

Für die Audit-/PDF-Buildwerkzeuge werden Python, reportlab 4.4.9 und pefile
2024.8.26 benötigt. Dies sind Buildwerkzeuge; die fertige Anwendung benötigt
keine separat installierte Python-Umgebung. Den Softwarestand für das Handbuch
kannst du vor der Erstellung mit `$env:BUILD_VERSION=$releaseVersion` angeben.
Nach jedem externen Buildwerkzeug muss dessen Exitcode 0 sein; andernfalls nicht
mit dem nächsten Schritt fortfahren. Die CI prüft diese Fehler automatisch.

`UsePreparedLayout` kompiliert die Anwendung nicht erneut: Es paketiert genau
den inventarisierten Publish-Stand. `verify-release-inventory.py` vergleicht jede
Binärdatei, alle gesammelten Originaltexte und die eingebetteten Berichte mit
diesem Stand und erzeugt `LICENSE-VERIFICATION.json` neben dem signierten Bundle.
