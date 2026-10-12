# Portable Python-Laufzeit

Die Windows-x64-Ausgabe enthält CPython 3.14.8 einschließlich Standardbibliothek und Lizenztexten unter `workers/python/runtime`. Python-Codebausteine, Auftragsauswertung und Python-Debugging verwenden automatisch diese Laufzeit. Eine globale Python-Installation oder PATH-Änderung ist nicht erforderlich.

`build/acquire-python.ps1` lädt das offizielle [Windows-Embed-Paket](https://www.python.org/downloads/release/python-3148/) herunter und prüft dessen fest hinterlegte SHA-256-Prüfsumme. `build/publish.ps1` und `build/msix.ps1` bereiten die Laufzeit automatisch vor. Für einen lokalen Entwicklungsbuild einmal `pwsh -File build/acquire-python.ps1` ausführen. Danach übernehmen die MSBuild-Targets die Dateien in Build- und Publish-Ausgaben. Quelle, Version und Archiv-Prüfsumme stehen in `runtime.json`.

Ein eigener Interpreter oder ein venv kann weiterhin verwendet werden. Die laufbezogene Reihenfolge ist unten beschrieben. `TESTUMGEBUNG_PYTHON` beziehungsweise `CodeRuntimeOptions.PythonExecutable` setzen die allgemeine Vorgabe, die nach Auswahlprogramm und Laufvariable geprüft wird. Ohne Bundle bleibt bei direkten Entwicklungsaufrufen die Suche nach `python`/`python3` erhalten; der Release-Build verlangt die portable Laufzeit.

Die mitgelieferte `_pth`-Konfiguration isoliert Python von globalem `PYTHONHOME`, `PYTHONPATH` und Benutzerpaketen. Standardbibliothek, Laufzeitordner, `Lib/site-packages` und der Python-Workerordner sind freigegeben. Zusätzliche projektspezifische Pakete können vor dem Build unter `toolchains/python/Lib/site-packages` abgelegt werden und werden mitgepackt; native Erweiterungen müssen zur Python-Version und x64-Architektur passen. Das installierte MSIX ist schreibgeschützt. Paketinstallation gehört zur Release-Vorbereitung, nicht in einen Prüfablauf.

Das Embed-Paket enthält kein Tkinter oder IDLE. Für beschreibbare externe Kopien wird zusätzlich das geprüfte pip-26.2.1-Wheel unter `pip-bootstrap/pip.whl` mitgeliefert. Diese Kopien aktivieren pip und `site`, damit installierte Pakete einschließlich ihrer `.pth`-Dateien funktionieren. Die schreibgeschützte gebündelte Laufzeit verwendet weiterhin ihre isolierte Konfiguration. Zusätzliche Bibliotheken wie NumPy sind nicht automatisch enthalten. Die offiziellen Hinweise zur [eingebetteten Distribution](https://docs.python.org/3.14/using/windows.html#the-embeddable-package) erläutern die Grenzen.

## Python-Konsole aus der Anwendung öffnen

Unter **System & Sicherung → Python-Umgebung und Konsole**:

1. **Python-Kopie erstellen und Konsole öffnen** wählen. Die Software kopiert ihre eigene Laufzeit aus dem installierten Paket in einen neuen Ordner unter `%TEMP%\Testumgebung\PythonEnvironments` und öffnet dort eine Windows-PowerShell-Konsole.
2. Die Konsole zeigt vorbereitete Befehle. Beispielsweise `python -m pip install numpy` ausführen. `python` und `pip` verweisen in dieser Konsole auf die neue Kopie. Eine globale Python- oder PowerShell-7-Installation wird nicht benötigt.
3. Nach der Installation in der Anwendung **Diese Umgebung verwenden** wählen. Vorher alle Prüfungen und Auftragsauswertungen beenden. Die Software beendet untätige Python-Worker, damit neue Aufrufe die installierten Pakete laden. Nach weiteren Paketänderungen diesen Knopf erneut drücken.
4. **Konsole öffnen** öffnet dieselbe Kopie erneut. **Mitgeliefertes Python verwenden** setzt die Laufzeitwahl zurück, ohne die externe Kopie zu löschen.

Die allgemeine Vorgabe wird pro Benutzer als Stringwert `PythonVenv` unter `HKEY_CURRENT_USER\Software\Testumgebung` gespeichert. Die bisherige Datei `%LOCALAPPDATA%\Testumgebung\python-environment.json` bleibt als kompatible Sicherung erhalten. Beim Start kann `TESTUMGEBUNG_PYTHON` diese allgemeine Vorgabe ersetzen. Falls Windows die temporäre Kopie entfernt, fällt der nächste Python-Schritt auf die nächste vorhandene Umgebung zurück. Für dauerhaft verwaltete Umgebungen kann ein eigener Interpreter oder venv-Ordner verwendet werden.

Die Konsole ist ein separates Windows-Fenster; Befehlsvorlagen und Laufzeitwahl sind in der Testumgebung sichtbar. Pakete werden erst installiert, wenn der Benutzer den entsprechenden Konsolenbefehl ausführt. Downloads benötigen Netzwerkzugriff; lokale Wheel-Dateien können mit `python -m pip install --no-index PFAD_ZUR_WHEELDATEI` installiert werden.

## Python je ausgewähltem Testprogramm festlegen

Vor jedem Python-Schritt prüft die Software in dieser Reihenfolge:

1. Den Pfad, den das **eigene Auswahlprogramm** zusammen mit dem Testprogramm übergibt.
2. Die aktuell sichtbare **String-Variable `PythonVenv`** im Testprogramm.
3. Die **allgemeine Vorgabe** aus System & Sicherung / Benutzer-Registry (oder die ausdrücklich konfigurierte allgemeine Umgebung).
4. Die **mitgelieferte Python-Laufzeit aus dem MSIX**.

Leerzeichen, nicht definierte Pfade, entfernte Ordner und Ordner ohne Interpreter werden übersprungen. Pfade müssen absolut sein. Ein venv-Ordner enthält `Scripts\python.exe`; eine portable Kopie enthält `python.exe` direkt im Ordner. Auch ein vollständiger Interpreterpfad ist möglich. Ein existierender Interpreter mit fehlenden Paketen oder einem Laufzeitfehler wird regulär als Fehler gemeldet; ein solcher Fehler löst keinen automatischen Wechsel aus.

### Aus dem C#-Auswahlcode

```csharp
var daten = FA.Auftragsauswertung.Lesen(Auftrag);
var umgebung = daten.Testprogramm == "Programm A"
    ? @"C:\Pruefumgebungen\ProgrammA"
    : @"C:\Pruefumgebungen\ProgrammB";
SelectTest(daten.Testprogramm, order: Auftrag,
    parameters: "Artikel=" + daten.Artikel, pythonVenv: umgebung);
return Pass(daten.Testprogramm);
```

Die bestehende DLL muss dafür nicht geändert werden. Sie kann bei Bedarf selbst einen Umgebungspfad zurückgeben; diesen an `pythonVenv` übergeben. Bisherige Aufrufe mit vier Parametern bleiben gültig. Die Umgebung wird beim Start an genau den ausgewählten Prüfablauf übergeben und gilt für Start, Loop und End. Bei einer neuen Auswahl wird sie neu bestimmt.

### Aus dem Python-Auswahlcode

```python
select_test("Programm A", order=Auftrag,
            python_venv=r"C:\Pruefumgebungen\ProgrammA")
return Pass()
```

Der Auswahlcode selbst verwendet die bis dahin bekannte Variable beziehungsweise allgemeine Vorgabe. Die mit `select_test` zurückgegebene Umgebung gilt für das anschließend gestartete Testprogramm.

### Über den Baustein „Test auswählen“

Im eigenen Auswahlablauf den Baustein **Test auswählen** einfügen. Im zusätzlichen Feld **Python-Umgebung (optional)** einen Stringausdruck mit dem absoluten Pfad oder den Namen einer String-Variable hinterlegen. Leer bedeutet, dass die nächste Stufe entscheidet.

### Über eine Variable vor dem Start

Im Hauptablauf des Testprogramms eine Variable **PythonVenv**, Typ **string**, anlegen. Als Anfangswert den Umgebungspfad eintragen oder ihn im Start-/Init-Abschnitt durch einen C#-Codeblock setzen:

```csharp
PythonVenv = @"C:\Pruefumgebungen\Gemeinsam";
return Pass();
```

Alternativ kann das Auswahlprogramm den Pfad als Prüfparameter `PythonVenv=C:\Pruefumgebungen\Gemeinsam` übergeben. Die Variable muss im Testprogramm vorhanden sein. Sie wird vor jedem Python-Aufruf erneut ausgewertet; eine Änderung wirkt ab dem nächsten Python-Schritt. Eine gültige Umgebung aus `pythonVenv` im Auswahlprogramm hat weiterhin Vorrang. Für Start, Loop und End die Variable im Hauptablauf anlegen; eine nur im Loop angelegte Variable ist außerhalb dieses Ordners nicht sichtbar.

Mehrere Prüfplätze können gleichzeitig unterschiedliche Umgebungen nutzen. Ein Ablauf ändert weder die Vorgabe noch die Umgebung anderer Prüfplätze. Die Anzeige unter System & Sicherung zeigt die allgemeine Vorgabe; den tatsächlich verwendeten Interpreter kann ein Python-Codeblock mit `import sys; return Pass(sys.executable)` ausgeben.
