# Externe Programme, Python-Skripte und DLL-Main aufrufen

Im Ablaufeditor gibt es die Kategorie **Prozesse** mit **Prozess starten**,
**Auf Prozess warten**, **Prozessstatus abfragen** und **Prozess beenden**.
Alle Einstellungen werden im Prüfprojekt und dessen Release gespeichert.

## Aufrufarten

| Art | Bedeutung |
| --- | --- |
| EXE | Ausführbare Datei direkt starten, ohne automatisch eine Shell zu verwenden |
| Python | Skript mit der Python-Umgebung dieses Prüfprogramms starten |
| .NET-DLL | Den echten Einstiegspunkt einer ausführbaren .NET-Assembly aufrufen |
| DLL-Main | Eine öffentliche statische Main-Methode eines ausgewählten vollständigen .NET-Typnamens aufrufen |

Python verwendet die vorhandene Reihenfolge: Umgebung aus Programmauswahl,
Stringvariable `PythonVenv`, gespeicherte Umgebung, gebündeltes Python.
Ein ausdrücklich eingetragener Interpreter im Baustein überschreibt diese Auswahl.
Das Skript erhält seine Parameter über `sys.argv[1:]`; Python wird mit `-X utf8`
gestartet. Das Skriptverzeichnis wird für lokale Imports hinzugefügt, auch beim
gebündelten Python. Pakete müssen in dieser ausgewählten Umgebung vorhanden sein.

.NET-DLLs laufen in einem eigenen Prozess mit der mitgelieferten .NET-10-Laufzeit.
Kein Aufruf erfolgt im Speicher der Bedienoberfläche. Für DLL-Main wird genau eine
passende Methode benötigt: `public static Main()` oder `public static Main(string[])`
mit Rückgabe `void`, `int`, `Task` oder `Task<int>`. `void`/`Task` bedeuten bei
normalem Abschluss Exitcode 0; `int`/`Task<int>` liefern den Exitcode. Mehrdeutige
Überladungen werden abgewiesen. Eine normale Klassenbibliothek ohne Einstiegspunkt
ist kein ausführbares Programm; dafür gegebenenfalls DLL-Main verwenden.

Die benötigten DLLs und nativen Abhängigkeiten müssen neben dem Programm oder
über dessen `.deps.json` auflösbar sein. Die DLL muss zur mitgelieferten .NET-Laufzeit
passen. Das ist kein vollständiger Ersatz eines beliebigen fremden Apphosts:
fremde Laufzeit-/Framework-Anforderungen und Startoptionen aus `.runtimeconfig.json`
werden nicht als neue Laufzeit in den Host übernommen. Programme, die beispielsweise
eine andere Laufzeit oder ein zusätzliches Desktop-Framework zwingend benötigen,
als passende eigenständige EXE starten. Native DLL-Exports und .NET-Framework-DLLs
sind keine unterstützten Main-Verträge; für solche Formate ist ein eigener,
kompatibler EXE-Adapter erforderlich.

Beispiel einer DLL-Klasse (namespace und Typ vollständig unter **Typ** angeben):

```csharp
namespace MeinePruefung;
public static class Einstieg
{
    public static int Main(string[] args)
    {
        Console.WriteLine("Seriennummer: " + args[0]);
        return 0;
    }
}
```

## Parameter und Variablen

**Datei** ist ein absoluter Pfad. **Arbeitsverzeichnis** ist ebenfalls absolut;
leer bedeutet das Verzeichnis der Programm-/Skriptdatei. Beide unterstützen
Platzhalter wie `{ProgrammOrdner}` und `{Kennung}`. Literale geschweifte Klammern
innerhalb eines einzelnen Textwerts als `{{` und `}}` schreiben.

**Argumente** ist eine JSON-Liste von Strings, beispielsweise:

```json
["--serial", "{Kennung}", "Text mit Leerzeichen", "Anführungszeichen: \""]
```

Erst wird die JSON-Liste gelesen, danach jeder einzelne Wert erweitert.
So bleiben Leerzeichen, Anführungszeichen und Unicode Teil eines einzelnen
Arguments. Shellzeichen wie `&` führen keine zusätzlichen Befehle aus.
Batchdateien sind keine EXE-Dateien: Eine ausdrücklich gewünschte Shell muss
als Programm ausgewählt und ihre eigene Syntax bewusst konfiguriert werden.

**Umgebung** ist ein JSON-Objekt aus Stringwerten:

```json
{"PRUEFMODUS":"Produktion", "SERIAL":"{Kennung}"}
```

Diese Werte ergänzen beziehungsweise überschreiben geerbte Umgebungsvariablen.
Der Baustein protokolliert weder Argumente noch Umgebungswerte automatisch.
**Kodierung** gilt für erfasste stdout/stderr-Ausgabe: standardmäßig `utf-8`,
für ältere EXE-Programme beispielsweise `windows-1252` oder `ibm850`.
Python verwendet ausschließlich `utf-8`. **Ausgabelimit** begrenzt jeden Stream
separat (Standard 65.536, höchstens 1.048.576 Zeichen); weitere Zeichen werden
weiter gelesen, aber nicht gespeichert. So blockieren volle Ausgabepuffer den
Prozess nicht. Eine Kürzung wird im Verlauf gemeldet. `0` speichert keine Zeichen.

## Gebunden oder unabhängig

**Gebunden** ist der Standard. Innerhalb einer Prüflingsgruppe gehört der
Prozess zu genau diesem Prüfling. Nach dessen Exit-Aktionen werden noch laufende
Prozesse einschließlich Kindprozessen beendet. Außerhalb einer Prüflingsgruppe,
beispielsweise in Init, gehören sie zur Prüfsitzung und enden spätestens mit ihr.
Auf Windows wird eine Prozessgruppe verwendet; das Zielprogramm startet erst,
nachdem der Host dieser Gruppe zugeordnet wurde. Auch später gestartete Kinder
werden damit erfasst. Bei Timeout oder Abbruch einer Warteaktion wird der
gebundene Prozess ebenfalls beendet.

**Unabhängig** erhält diese automatische Aufräumregel nicht. Prüflingsende,
Prüfsitzungsende oder das Abbrechen eines Wartevorgangs beenden diesen Prozess
nicht. Er läuft mit den Rechten und Betriebssystembedingungen des aufrufenden
Benutzers; die Option ist keine administrative Rechteerhöhung oder Sandbox.
Ein Betriebssystem-Elternprozess existiert weiterhin. Soll das Programm auch
nach dem Schließen der Bediensoftware unabhängig mit eigener Ausgabe arbeiten,
**Ausgabe erfassen** deaktivieren: Erfasste Streams benötigen die Bediensoftware
als Leser. Externe Programmdialoge sind keine gesperrten Werkeraufforderungen.

**Konsolenfenster zulassen** erlaubt ein Konsolenfenster. GUI-Programme steuern
ihre Fenster selbst; die Option versteckt keine beliebige GUI-Anwendung.
Gebundene Aufrufe sind für automatisierbare Programme gedacht; ihre stdin-Pipe
ist kein interaktiver Konsoleneingabekanal. Parameter über Argumente/Umgebung
übergeben oder eine geeignete externe Benutzeroberfläche verwenden.

Beim gezielten Testen einzelner Schritte/Gruppen im Debugger werden gebundene
Prozesse am Ende dieser gezielten Ausführung aufgeräumt. Zum Testen einer Folge
Start → Status → Warten daher die ganze entsprechende Gruppe gezielt ausführen.

## Warten und Exitcode bewerten

Mit **Auf Prozessende warten** wird bis zum Ende oder bis zum **Zeitlimit** gewartet.
Das Zeitlimit ist in Millisekunden (Standard 30.000). **Erlaubte Exitcodes** ist
beispielsweise `0`, `0;2;7` oder ein Platzhalter. Ein erlaubter Code ergibt PASS,
ein anderer FAIL. Leeres Feld bedeutet NRESULT, also keine fachliche Bewertung.
DLL-/Hostfehler ergeben immer ERROR, auch wenn die technische Fehlernummer 253
in der erlaubten Liste steht. Startfehler und Timeout sind ebenfalls ERROR;
ein Werkerabbruch wird nach der bestehenden Abbruchfolge behandelt.

Ohne Warten bleibt **Prozess starten** immer NRESULT. **Prozesskennung → Stringvariable**
ist dann Pflicht. Die Kennung ist ein undurchsichtiges Handle dieses Prüflaufs;
es ist keine Betriebssystem-PID. Andere Prüfplätze/Läufe können sie nicht verwenden.
In **Auf Prozess warten**, **Prozessstatus abfragen** und **Prozess beenden**
wird beispielsweise `{MeinProzess}` eingetragen. Die separat verfügbare
**Host-Prozess-ID** kann bei gebundenen/DLL-Aufrufen die ID des Hosts sein.

Mögliche Ergebnisziele:

| Ziel | Variablentyp | Bedeutung |
| --- | --- | --- |
| Prozesskennung | String | Kennung für spätere Bausteine |
| Host-Prozess-ID | Int | ID des gestarteten Hosts/Programms |
| Exitcode | Int | Gültig erst nach normalem Prozessende |
| stdout / stderr | String | Getrennte, begrenzte Ausgabe |
| Prozess beendet | Bool | Abschluss ist erfolgt; bei laufendem Prozess false |

Zielvariablen vorher anlegen. **Prozess beendet** prüfen, bevor ein Exitcode aus
einer Zwischenabfrage verwendet wird. Vor dem Ende wird kein erfundener Exitcode
0 geschrieben; ein früherer Variablenwert ist noch keine neue Rückgabe.
Timeout/Abbruch liefern keinen erfolgreichen Exitcode. **Prozess beenden** selbst
ist unbewertet; eine spätere Warteaktion meldet eine erzwungene Beendigung als ERROR.
Ein bereits beendeter unabhängiger Starter erlaubt keine nachträgliche zuverlässige
Zuordnung seiner weiterlaufenden Kinder; automatische Baumverwaltung ist die
Aufgabe des gebundenen Modus.

**Ausgabe protokollieren** ist standardmäßig aus. Nur aktivieren, wenn die Ausgabe
keine Zugangsdaten oder sonstigen geheimen Inhalte enthält. Nach Prüflingsende
werden gebundene Kennungen freigegeben. Je Prüflauf werden höchstens 128 laufende
Prozesse verwaltet; bei Erreichen dieser Grenze werden ältere abgeschlossene
Kennungen freigegeben. Unabhängige Kennungen gelten ebenfalls nur in ihrer
ursprünglichen Prüfsitzung.

## Beispiel im Ablaufeditor

1. In der Prüflingsgruppe die Variablen `MeinProzess` (String), `Fertig` (Bool),
   `Exitcode` (Int), `Ausgabe` und `Fehlertext` (String) anlegen.
2. **Prozess starten** einfügen: Art Python, Datei `C:\Pruefung\check.py`,
   Argumente `["--serial","{Kennung}"]`, gebunden, Warten aus,
   Prozesskennung nach `MeinProzess` schreiben.
3. Weitere sinnvolle Testschritte ausführen.
4. **Auf Prozess warten** einfügen: Prozess `{MeinProzess}`, Zeitlimit 60.000,
   Exitcode nach `Exitcode`, stdout/stderr nach `Ausgabe`/`Fehlertext`,
   Fertig nach `Fertig`, erlaubter Exitcode `0`.
5. Erst mit einer bekannten simulierten/ungefährlichen Testdatei prüfen, danach
   den fachlichen Normalfall, Fehlercode, Timeout und Werkerabbruch abnehmen.

Für einen dauerhaft externen Prozess unabhängig wählen und Ausgabeerfassung
deaktivieren. Für Messwerkzeuge mit verlässlicher Aufräumregel gebunden verwenden.

## Mitgeliefertes Beispiel

[prozess-python.tproj.json](beispiele/prozess-python.tproj.json) und [prozess-demo.py](beispiele/prozess-demo.py) zeigen einen Python-Aufruf pro Seriennummer ohne Hardwarezugriffe. Hauptvariable `SkriptDatei` vor dem Start auf den absoluten Pfad des Skripts setzen. `DemoExitcode = 0` ergibt PASS, andere Werte ergeben FAIL. **Auch im Simulationsmodus werden Prozessbausteine wirklich ausgeführt.** Daher nur bekannte Testprogramme verwenden.
