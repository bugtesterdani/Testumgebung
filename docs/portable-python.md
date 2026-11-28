# Portable Python-Laufzeit

Die Windows-x64-Ausgabe enthält CPython 3.15.2 einschließlich Standardbibliothek und Lizenztexten unter `workers/python/runtime`. Python-Codebausteine, Auftragsauswertung und Python-Debugging verwenden automatisch diese Laufzeit. Eine globale Python-Installation oder PATH-Änderung ist nicht erforderlich.

Zusatzpakete für eine neue Kopie dieser Laufzeit müssen Python 3.15 unterstützen. Native Erweiterungen (`.pyd`) aus Python 3.14 bitte über pip passend neu installieren, statt sie in die neue Umgebung zu kopieren. Bereits separat ausgewählte externe Interpreter und Umgebungen können weiterhin mit ihrer eigenen Python-Version verwendet werden.

`build/acquire-python.ps1` lädt das offizielle [Windows-Embed-Paket](https://www.python.org/downloads/release/python-3152/) herunter und prüft dessen fest hinterlegte SHA-256-Prüfsumme. `build/publish.ps1` und `build/msix.ps1` bereiten die Laufzeit automatisch vor. Für einen lokalen Entwicklungsbuild einmal `pwsh -File build/acquire-python.ps1` ausführen. Danach übernehmen die MSBuild-Targets die Dateien in Build- und Publish-Ausgaben. Quelle, Version und Archiv-Prüfsumme stehen in `runtime.json`.

Ein eigener Interpreter oder ein venv kann weiterhin verwendet werden. Die laufbezogene Reihenfolge ist unten beschrieben. `TESTUMGEBUNG_PYTHON` beziehungsweise `CodeRuntimeOptions.PythonExecutable` setzen die allgemeine Vorgabe, die nach Auswahlprogramm und Laufvariable geprüft wird. Ohne Bundle bleibt bei direkten Entwicklungsaufrufen die Suche nach `python`/`python3` erhalten; der Release-Build verlangt die portable Laufzeit.

Die mitgelieferte `_pth`-Konfiguration isoliert Python von globalem `PYTHONHOME`, `PYTHONPATH` und Benutzerpaketen. Standardbibliothek, Laufzeitordner, `Lib/site-packages` und der Python-Workerordner sind freigegeben. Zusätzliche projektspezifische Pakete können vor dem Build unter `toolchains/python/Lib/site-packages` abgelegt werden und werden mitgepackt; native Erweiterungen müssen zur Python-Version und x64-Architektur passen. Das installierte MSIX ist schreibgeschützt. Paketinstallation gehört zur Release-Vorbereitung, nicht in einen Prüfablauf.

Das Embed-Paket enthält kein Tkinter oder IDLE. Für beschreibbare externe Kopien wird zusätzlich das geprüfte pip-26.3-Wheel unter `pip-bootstrap/pip.whl` mitgeliefert. Diese Kopien aktivieren pip und `site`, damit installierte Pakete einschließlich ihrer `.pth`-Dateien funktionieren. Die schreibgeschützte gebündelte Laufzeit verwendet weiterhin ihre isolierte Konfiguration. Zusätzliche Bibliotheken wie NumPy sind nicht automatisch enthalten. Die offiziellen Hinweise zur [eingebetteten Distribution](https://docs.python.org/3.14/using/windows.html#the-embeddable-package) erläutern die Grenzen.

## Python-Konsole aus der Anwendung öffnen

Unter **Verwaltung → Python → Python-Umgebung und Konsole**:

1. **Python-Kopie erstellen und Konsole öffnen** wählen. Die Software kopiert ihre eigene Laufzeit aus dem installierten Paket in einen neuen Ordner unter `%LOCALAPPDATA%\Testumgebung\PythonEnvironments` und öffnet dort eine Windows-PowerShell-Konsole.
2. Die Konsole zeigt vorbereitete Befehle. Beispielsweise `python -m pip install numpy` ausführen. `python` und `pip` verweisen in dieser Konsole auf die neue Kopie. Eine globale Python- oder PowerShell-7-Installation wird nicht benötigt.
3. Nach der Installation in der Anwendung **Diese Umgebung verwenden** wählen. Vorher alle Prüfungen und Auftragsauswertungen beenden. Die Software beendet untätige Python-Worker, damit neue Aufrufe die installierten Pakete laden. Nach weiteren Paketänderungen diesen Knopf erneut drücken.
4. **Konsole öffnen** öffnet dieselbe Kopie erneut. **Mitgeliefertes Python verwenden** setzt die Laufzeitwahl zurück, ohne die externe Kopie zu löschen.

Die allgemeine Vorgabe wird in `%LOCALAPPDATA%\Testumgebung\python-environment.json` gespeichert. Für verwaltete Umgebungen enthält sie `id:<ID>` statt eines Installationspfads; die bisherige Benutzer-Registry bleibt für alte Pfadkonfigurationen kompatibel. Die Datei hat Vorrang vor der Registry. Beim Start kann `TESTUMGEBUNG_PYTHON` die allgemeine Vorgabe ersetzen. Neue Kopien liegen dauerhaft im Benutzerprofil und werden nicht durch die temporäre Windows-Bereinigung entfernt.

## Mehrere Umgebungen und portable IDs

Jede neu erstellte Umgebung erhält eine zufällige, unveränderliche ID. Die Datei `testumgebung-python.json` im Umgebungsordner enthält diese ID als JSON-String. Unter **Verwaltete Umgebungen** lassen sich alle lokal erstellten und importierten Kopien auswählen, ihre Konsole öffnen und ihre ID kopieren. Die Auswahl in dieser Liste ändert die allgemeine Vorgabe erst mit **Diese Umgebung verwenden**.

In den **Projekteigenschaften → Python-Umgebung** wählen:

- **Leer:** bisheriges Verhalten mit Variablen und allgemeiner Vorgabe beibehalten.
- **bundled:** ausdrücklich die mitgelieferte Runtime verwenden, auch wenn eine andere allgemeine Vorgabe oder `PythonVenv` gesetzt ist.
- **id:<ID>:** genau diese lokal verwaltete Umgebung verwenden. Auch eine direkt eingegebene ID wird als ID behandelt.

Die Projektdatei und das Freigabepaket speichern diese Auswahl als `pythonEnvironmentId`. Damit können Programme auf demselben Prüfplatz verschiedene Umgebungen nutzen oder sich eine Umgebung teilen. Absolute Installationspfade sind keine Freigabeidentität. Der Freigabenachweis prüft weiterhin Python-Version, Interpreterinhalt und installierte Paketversionen; eine ID allein bestätigt keinen unveränderten Inhalt.

**Eine fehlende ausdrücklich ausgewählte ID oder eine fehlende ausdrücklich gewählte mitgelieferte Runtime verhindert den Start. Es gibt dabei keinen stillen Rückfall auf andere Umgebungen.** Das gilt auch für IDs aus Auswahlprogrammen, Variablen und der gespeicherten Vorgabe. Die passende ZIP-Datei auf dem Zielprüfplatz importieren. Eine automatische Verteilung über die zentrale Freigabeablage ist nicht Teil dieses Transfers.

Die Konsole ist ein separates Windows-Fenster; Befehlsvorlagen und Laufzeitwahl sind in der Testumgebung sichtbar. Pakete werden erst installiert, wenn der Benutzer den entsprechenden Konsolenbefehl ausführt. Downloads benötigen Netzwerkzugriff; lokale Wheel-Dateien können mit `python -m pip install --no-index PFAD_ZUR_WHEELDATEI` installiert werden.

## Python je ausgewähltem Testprogramm festlegen

Vor jedem Python-Schritt prüft die Software in dieser Reihenfolge:

1. Die Auswahl (`id:<ID>`, `bundled` oder bisheriger Pfad), die das **eigene Auswahlprogramm** mitgibt.
2. Die explizite **Python-Umgebung in den Projekteigenschaften**.
3. Die aktuell sichtbare **String-Variable `PythonVenv`** im Testprogramm.
4. Die **allgemeine Vorgabe** aus System & Sicherung / Benutzer-Registry (oder die ausdrücklich konfigurierte allgemeine Umgebung).
5. Die **mitgelieferte Python-Laufzeit aus dem MSIX**.

Nur bei alten Pfadkonfigurationen werden Leerzeichen, nicht definierte Pfade, entfernte Ordner und Ordner ohne Interpreter wie bisher übersprungen. Explizite IDs werden niemals übersprungen. Pfade müssen absolut sein. Ein venv-Ordner enthält `Scripts\python.exe`; eine portable Kopie enthält `python.exe` direkt im Ordner. Auch ein vollständiger Interpreterpfad ist möglich. Ein existierender Interpreter mit fehlenden Paketen oder einem Laufzeitfehler wird regulär als Fehler gemeldet; ein solcher Fehler löst keinen automatischen Wechsel aus.

### Aus dem C#-Auswahlcode

```csharp
var daten = FA.Auftragsauswertung.Lesen(Auftrag);
var umgebung = daten.Testprogramm == "Programm A"
    ? "id:0123456789abcdef0123456789abcdef"
    : "bundled";
SelectTest(daten.Testprogramm, order: Auftrag,
    parameters: "Artikel=" + daten.Artikel, pythonVenv: umgebung);
return Pass(daten.Testprogramm);
```

Die bestehende DLL muss dafür nicht geändert werden. Sie kann bei Bedarf selbst einen Umgebungspfad zurückgeben; diesen an `pythonVenv` übergeben. Bisherige Aufrufe mit vier Parametern bleiben gültig. Die Umgebung wird beim Start an genau den ausgewählten Prüfablauf übergeben und gilt für Start, Loop und End. Bei einer neuen Auswahl wird sie neu bestimmt.

### Aus dem Python-Auswahlcode

```python
select_test("Programm A", order=Auftrag,
            python_venv="id:0123456789abcdef0123456789abcdef")
return Pass()
```

Der Auswahlcode selbst verwendet die bis dahin bekannte Variable beziehungsweise allgemeine Vorgabe. Die mit `select_test` zurückgegebene Umgebung gilt für das anschließend gestartete Testprogramm.

### Über den Baustein „Test auswählen“

Im eigenen Auswahlablauf den Baustein **Test auswählen** einfügen. Im zusätzlichen Feld **Python-Umgebung (optional)** einen Stringausdruck mit `id:<ID>`, `bundled`, einem bisherigen absoluten Pfad oder dem Namen einer String-Variable hinterlegen. Leer bedeutet, dass die nächste Stufe entscheidet.

### Über eine Variable vor dem Start

Im Hauptablauf des Testprogramms eine Variable **PythonVenv**, Typ **string**, anlegen. Als Anfangswert den Umgebungspfad eintragen oder ihn im Start-/Init-Abschnitt durch einen C#-Codeblock setzen:

```csharp
PythonVenv = @"C:\Pruefumgebungen\Gemeinsam";
return Pass();
```

Alternativ kann das Auswahlprogramm den Pfad als Prüfparameter `PythonVenv=C:\Pruefumgebungen\Gemeinsam` übergeben. Die Variable muss im Testprogramm vorhanden sein. Sie wird vor jedem Python-Aufruf erneut ausgewertet; eine Änderung wirkt ab dem nächsten Python-Schritt. Eine gültige Umgebung aus `pythonVenv` im Auswahlprogramm und die explizite Projektauswahl haben Vorrang. Bei freigegebenen Programmen wird vor jedem Python-Codeaufruf zusätzlich der Fingerabdruck der tatsächlich gewählten Umgebung geprüft. Änderungen an `PythonVenv` dürfen keine andere als die freigegebene Laufzeit einschleusen. Für Start, Loop und End die Variable im Hauptablauf anlegen; eine nur im Loop angelegte Variable ist außerhalb dieses Ordners nicht sichtbar.

Mehrere Prüfplätze können gleichzeitig unterschiedliche Umgebungen nutzen. Ein Ablauf ändert weder die Vorgabe noch die Umgebung anderer Prüfplätze. Die Anzeige unter System & Sicherung zeigt die allgemeine Vorgabe; den tatsächlich verwendeten Interpreter kann ein Python-Codeblock mit `import sys; return Pass(sys.executable)` ausgeben.

### Python-Umgebung auf einen anderen Prüfplatz übertragen

**Umgebung exportieren** erstellt aus der angezeigten portablen Umgebung ein ZIP mit Python und den installierten
Paketen einschließlich der Umgebungs-ID. **Umgebung importieren** entpackt das ZIP in einen neuen Ordner im lokalen Windows-Benutzerprofil.
**Diese Umgebung verwenden** aktiviert die importierte Kopie. Die bisher aktive Umgebung läuft bis dahin weiter.

Der Transfer funktioniert zwischen Windows-x64-Prüfplätzen und benötigt keine erneute pip-Installation.
Pakete, die Dateien außerhalb des Umgebungsordners, fest eingetragene absolute Pfade oder zusätzliche
Systemkomponenten benötigen, brauchen diese Voraussetzungen auch am Ziel. Verwende nur ZIP-Dateien aus
vertrauenswürdigen Quellen.

Ein ZIP mit einer am Prüfplatz bereits vorhandenen ID wird abgelehnt; vorhandene Umgebungen werden nicht überschrieben. Für eine neue unabhängige Umgebung **Python-Kopie erstellen** verwenden. Ein geändertes ZIP derselben ID kann nach dem Beenden aller betroffenen Prüfungen, Umschalten der allgemeinen Vorgabe auf mitgeliefertes Python und Entfernen der bisherigen lokalen Kopie erneut importiert werden. Den Ordner zeigt die Umgebungsverwaltung an. Alte ZIPs ohne ID erhalten beim ersten Import eine neue ID; danach diese Kopie exportieren und dasselbe ZIP auf allen Zielprüfplätzen verwenden. Alte Pfadkonfigurationen, `PythonVenv`-Variablen, Auswahlprogramm-Aufrufe und Projektdateien ohne `pythonEnvironmentId` bleiben unterstützt.

Nach Paketänderungen die Umgebung erneut aktivieren, den Freigabenachweis neu erstellen und das neue ZIP auf die Zielprüfplätze übertragen. Eine andere Paketliste wird auch unter derselben ID abgelehnt. Der Nachweis wird beim Erstellen, Prüfen und Freigeben mit der Projektauswahl und beim Start zusätzlich mit der Vorgabe aus dem Auswahlprogramm verglichen.

Die übrigen Prüfungen des Freigabepakets (Anwendung, Worker, Treiber und Toolchains) bleiben bestehen. Eine Änderung dieser Komponenten kann weiterhin eine erneute Entwicklungsprüfung erfordern. Ein Wechsel der projektspezifischen Python-Umgebung wird während eines laufenden Auftrags nicht zwischen zwei Prüflingen übernommen, sondern erst mit einer neuen Prüfsitzung.

Eine Konfigurationssicherung enthält die allgemeine Auswahl, aber keine vollständigen Python-Umgebungen. Die benötigten Umgebungs-ZIPs deshalb separat übertragen. Alte portable Kopien außerhalb der neuen Verwaltung können exportiert und anschließend in die dauerhafte Verwaltung importiert werden; alte Pfadvorgaben bleiben bis zur Umstellung gültig.
