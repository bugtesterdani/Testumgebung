# Live-Log und eigener Fortschritt

Ab Version 1.1.0 können laufende Programme Text, einen eigenen Status und einen
Prozentwert auf der Werkerseite anzeigen. Jede Seite des Doppelprüfplatzes hat
ihre eigenen Anzeigen. Parallele Schritte bekommen getrennte Karten; eigene
Kanäle innerhalb eines Codebausteins sind ebenfalls möglich. Der Fortschritt
bewertet keinen Prüfschritt und ersetzt nicht die Ablaufübersicht.

## Prozessausgabe ohne eigenen Code anzeigen

1. In der Gruppe eine **Stringvariable** `Prozesskennung` anlegen.
2. Unter **Prozesse → Prozess starten** das Skript oder Programm einstellen,
   **stdout/stderr erfassen** aktivieren, **Auf Prozessende warten** ausschalten
   und **Prozesskennung → Stringvariable** auf `Prozesskennung` setzen.
3. Danach **Anzeige → Live-Log und Fortschritt** einfügen. Quelle
   **Prozess-stdout** und Prozesskennung `{Prozesskennung}` wählen. Für stderr
   gibt es **Prozess-stderr**; die Streams werden bewusst getrennt verarbeitet.
4. Optional Regeln und einen absoluten Pfad für die **UTF-8-Mitschrift** setzen.
   Die Mitschrift hängt an eine vorhandene Datei an und wird nach jedem
   gelesenen Textstück geleert. Der Zielordner muss existieren.
5. Anschließend mit **Auf Prozess warten** den Exitcode bewerten. Eine angezeigte
   100-Prozent-Meldung ist allein kein Nachweis eines erfolgreichen Prozesses.

Ohne Ende-Muster läuft die Anzeige bis zum Ende des gewählten Streams. Danach
kann das Programm noch laufen oder einen Fehler liefern. Mit Ende-Muster kann
man früher zum nächsten Schritt wechseln und etwa einen Befehl senden. Die
bestehende Funktion **Auf Prozessausgabe warten** behält ihren eigenen Cursor;
Live-Anzeige und Mitschrift verbrauchen keine Prozessdialog-Signale.

Python-Prozessbausteine starten Python mit `-u` (ungepufferte Ausgabe). Andere
Programme müssen ihre Ausgabe selbst zeitnah flushen. Ein Programm, das seine
Ausgabe intern puffert, kann von der Testumgebung nicht zum Ausgeben gezwungen
werden. Der Prozess muss vor dem Monitor gestartet worden sein und zu demselben
Prüflauf gehören. Die Überwachung beendet den Prozess nicht selbst. Für
produktionsgebundene Aufrufe **Gebunden** verwenden.

## Texte auf feste Prozentwerte abbilden

In **Text-/Regex-Regeln** steht eine JSON-Liste. Die erste passende Regel gewinnt:

```json
[
  {"text":"START", "percent":5, "status":"Vorbereitung"},
  {"text":"ERASE OK", "percent":20, "status":"Speicher gelöscht"},
  {"regex":"VERIFY (.+)", "percent":90, "status":"Prüfe $1"},
  {"text":"FERTIG", "percent":100, "status":"Übertragung abgeschlossen"}
]
```

`text` sucht eine Zeichenfolge in der Zeile (Groß-/Kleinschreibung beachten).
`regex` ist ein .NET-Regulärausdruck. Genau eines davon muss je Regel gesetzt
sein. `status` ist optional; bei Regex können `$1`, `$2` usw. verwendet werden.
Prozentwerte müssen zwischen 0 und 100 liegen. Maximal 100 Regeln sind erlaubt.

## Prozent aus zwei Zahlen berechnen

Beispielausgabe des Programms:

```text
37 von 100
38 von 100
```

**Zahlenmuster**:

```regex
(?<current>\d+) von (?<total>\d+)
```

**Status bei Zahlentreffer**: `Übertragen: {current} von {total}`.
Die Anzeige berechnet `current / total * 100`. Dezimalpunkt oder Dezimalkomma
sind möglich, wenn das Muster sie erfasst. Ungültige Werte, negative Zähler und
Nenner ≤ 0 verändern den letzten gültigen Prozentwert nicht. Werte über 100 %
werden auf 100 % begrenzt. Textregeln haben Vorrang vor dem Zahlenmuster.

Auswertung erfolgt pro vollständiger Zeile (`LF`, `CRLF` oder `CR`). Ein noch
unvollständiger Zähler wird nicht vorzeitig berechnet. Am Streamende wird auch
eine letzte Zeile ohne Zeilenumbruch ausgewertet. Ein Ende-Muster kann bereits
ohne Zeilenumbruch erkannt werden. Ohne passenden Treffer bleibt der letzte
Prozentwert stehen, der Status zeigt die aktuelle Zeile.

## Laufend geschriebene Logdatei mitlesen

Quelle **Datei** wählen und den absoluten Pfad eintragen; `{Variable}` ist möglich.
Die Kodierung muss zum schreibenden Programm passen, standardmäßig UTF-8.
**Datei erst ab aktuellem Ende lesen** blendet vorhandenen Inhalt aus. Fehlt die
Datei noch, wartet der Leser bis zur Erstellung oder bis zum Gesamtzeitlimit.
Ein Ende-Muster wie `^FERTIG$` beendet die Überwachung. Ohne solches Muster läuft
eine Dateiüberwachung bis Werkerabbruch oder bis zum Zeitlimit (dann ERROR).

Der Leser öffnet mit `FileShare.ReadWrite | FileShare.Delete` und hält keinen
exklusiven Schreibzugriff. Die Datei darf gleichzeitig geschrieben werden,
sofern der Schreiber Lesen erlaubt. Eine exklusive Sperre (`FileShare.None`)
kann die Anwendung nicht umgehen; sie meldet den Grund verständlich.

Die Überwachung folgt der geöffneten Datei. Für Logrotation durch Umbenennen und
Ersetzen die Überwachung neu starten. Eine beobachtete Dateikürzung setzt den
Lesecursor und die halbe gepufferte Zeile zurück. Sehr schnelles Kürzen und
Wiederauffüllen kann zwischen zwei Lesevorgängen unentdeckt bleiben. Deshalb
für Produktionslogs vorzugsweise anhängen oder je Vorgang eine neue Datei nutzen.
Quelle und Mitschrift dürfen nicht derselbe Pfad sein.

Ein Python-Schreibbaustein und der Datei-Monitor können in einer Parallelgruppe
laufen. Benötigte Pfadvariablen vorher setzen. Beispiel zum Erzeugen einer Datei:

```python
import subprocess
with open(var("Logpfad"), "a", encoding="utf-8", buffering=1) as logfile:
    result = subprocess.run(
        [var("Programmdatei"), "--test"],
        stdout=logfile, stderr=subprocess.STDOUT,
        check=False)
return Pass(result.returncode) if result.returncode == 0 else Fail("Prozessfehler", result.returncode)
```

Der Prozess selbst muss flushen; `buffering=1` beeinflusst nur Python-Schreibzugriffe
auf die geöffnete Textdatei, nicht den Puffer des gestarteten Programms.

## Direkt im Python- oder C#-Baustein

Python stellt `live_output(text="", percent=None, status=None, channel="Ausgabe", reset=False)`
bereit. C# hat dieselbe Funktion als `LiveOutput(...)`. `text` wird angehängt;
`None` beziehungsweise `null` lässt Prozent und Status unverändert. Mit
`reset=True` beginnt eine neue Anzeige desselben Kanals. Ohne Prozentwert zeigt
die Karte zunächst eine unbestimmte Fortschrittsanzeige.

```python
live_output("Vorbereitung\n", percent=5, status="Initialisieren", reset=True)
# Eigene Logik, Gerätezugriffe oder subprocess-Auswertung ...
live_output("37 von 100\n", percent=37, status="Übertragen", channel="Ausgabe")
live_output("Fertig\n", percent=100, status="Abgeschlossen")
return Pass()
```

Das Beispielprojekt `docs/beispiele/live-ausgabe.tproj.json` kann ohne Hardware
simuliert werden. Für die Textregeln des grafischen Monitors eignet sich das
beigelegte Skript `docs/beispiele/live-prozess-demo.py` (im Prozessbaustein dessen
absoluten Pfad angeben).

`print()` allein ist im Python-Codebaustein keine Werkeranzeige. Für ein eigenes
`subprocess.Popen(..., stdout=subprocess.PIPE, text=True)` die gelesenen Zeilen
an `live_output(line, ...)` weiterreichen und bei Bedarf gleichzeitig in eine
Datei schreiben. Für automatische Prozesslebensdauer und die konfigurierbare
Textauswertung ist der grafische Prozess-/Live-Log-Ablauf oben vorgesehen.

## Grenzen und Abbruch

Die Werkeranzeige hält je Karte die letzten 20.000 Zeichen und maximal acht
Karten je Prüfplatz. Ganze Logs bleiben in der optionalen Mitschrift. Der
Prozess-Lesepuffer hält 1 Mi Zeichen je Stream: Überschreitet ein langsamer Leser
diesen Rückstand, wird ein ERROR gemeldet, statt Text unbemerkt zu überspringen.
Dann die Ausgabe direkt in eine Datei umleiten und die Datei überwachen.
Einzelne Zeilen und direkte API-Textaufrufe sind auf 65.536 Zeichen begrenzt;
Statusmeldungen auf 2.048 Zeichen. Regex-Auswertung hat ein Zeitbudget.

Gesamtzeitlimit und Werkerabbruch schließen die Lesedatei und Mitschrift. Sie
umgehen keine Eingabeschemas oder Bedienersperren. Anzeigen werden vor einem
neuen Prüfling beziehungsweise Prüflauf geleert. Vertrauliche Ausgaben erscheinen
unverändert beim Werker beziehungsweise in der Mitschrift; nur geeignete
Programmausgaben dafür auswählen.
