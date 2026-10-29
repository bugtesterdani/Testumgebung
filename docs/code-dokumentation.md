# Code-Dokumentation und Doxygen

Nicht alle Schnittstellen sind vollständig dokumentiert. Vorhanden sind Architektur-
und Benutzeranleitungen, XML-Kommentare an vielen Verträgen und Hinweise zu kritischen
Randbedingungen. Daneben gibt es unbeschriebene Mitglieder und veraltete Kommentare.
Zum Beispiel bezeichnete der Datenbankvertrag die bereits implementierten Provider
noch als zukünftige Etappe; dieser Hinweis wurde korrigiert.

## Bericht erzeugen

`Report-Doxygen.cmd` erzeugt und öffnet die HTML-Referenz. Voraussetzungen sind
PowerShell 7, Python 3 und beim ersten Bezug Internetzugang. Es ist keine globale
Doxygen-Installation erforderlich. Alternativ:

```powershell
pwsh -File build/doxygen.ps1
pwsh -File build/doxygen.ps1 -Python 'C:\Pfad\zu\python.exe' -OutputDirectory artifacts/doxygen/mein-bericht
```

Der Download erfolgt aus dem offiziellen GitHub-Release, ersatzweise vom Hersteller.
Nur ein Archiv mit der festgelegten SHA-256-Prüfsumme wird entpackt; fehlerhafte
Downloads werden verworfen und beschädigte Cache-Dateien erneut bezogen.

Doxygen 1.18.0 wird in `artifacts/tools/doxygen/1.18.0` zwischengespeichert. Jeder
Aufruf prüft den SHA-256 des Originalarchivs und extrahiert daraus das Werkzeug neu.
Die Ausgabe liegt standardmäßig in `artifacts/doxygen/current`:

- `html/index.html`: durchsuchbare API-Referenz mit Quellcodeansicht.
- `Dokumentationsstatus.html`: Filter nach Modul, Namen und fehlenden Texten.
- `Dokumentationsstatus.csv` / `.json`: maschinenlesbare Bestandsaufnahme.
- `warnings.log`: Parser- und Kommentarwarnungen.
- `xml/`: Rohdaten von Doxygen.
- `provenance.json`: Werkzeugversion, Archivprüfsumme und Git-Commit.
- `LICENSE` / `NOTICE.md`: Werkzeuglizenz und Verwendungsnachweis.

GitHub erzeugt dieselbe Ausgabe als Actions-Artefakt `API-Dokumentation-<Version>`
für Pull Requests, main und Release-Builds. HTML, Rohdaten und Hinweise werden
gemeinsam gespeichert. Die Werkzeug-EXE und ihr Downloadarchiv werden nicht verteilt.

## Was die Bestandsaufnahme aussagt

Erfasst sind die von Doxygen erkannten eigenen Typen und Mitglieder unter `src`.
`bin`, `obj`, Tests und Fremdbibliotheken werden nicht als eigener Code dokumentiert.
Auch private und statische Mitglieder bleiben sichtbar. Geerbte Beschreibungen werden
nicht automatisch übernommen. Die Auswertung prüft vorhandenen Text und benannte
Parameterbeschreibungen. Sie beurteilt weder fachliche Richtigkeit noch Verständlichkeit.

`EXTRACT_ALL` verhindert, dass fehlende Kommentare Mitglieder aus der Referenz
verschwinden lassen. Dabei meldet Doxygen nicht jede undokumentierte Deklaration als
Warnung; genau deshalb gibt es die separate XML-Auswertung. Moderne C#-Records,
Primärkonstruktoren und lokale Funktionen werden nicht immer wie vom C#-Compiler
verstanden. Warnungen bleiben sichtbar. Zahlen sind daher keine mit der Testabdeckung
vergleichbare Quote und kein Nachweis einer vollständigen Quellcodeprüfung.

## Kommentarregeln

Öffentliche Verträge erklären Zweck, Einheiten, Nullwerte, Seiteneffekte, Abbruch und
Fehlerverhalten. `summary` beschreibt die Aufgabe, `param` die Bedeutung der Eingaben,
`returns` den Ergebnisvertrag und `remarks` Randbedingungen. `exception` wird nur für
tatsächlich relevante Fehler ergänzt. Interne Kommentare erklären vor allem Gründe:
Sperrreihenfolge, Transaktionsgrenzen, Protokollformate und Sicherheitszustände.
Offensichtliche Zuweisungen benötigen keine Nacherzählung.

In Issue #5 wurden insbesondere `ICodeHost`, die Build-Prüfung von `ICodeRuntime`,
`IRunDatabase` und `IStreamTransport` ergänzt. Weitere Lücken bleiben im Bericht
sichtbar; es wurden keine automatisch erfundenen Kommentare eingesetzt.

## Lizenzentscheidung

Doxygen ist ein separates GPL-v2-Entwicklungswerkzeug. Die offizielle
[Lizenzseite](https://www.doxygen.nl/license.html) erklärt ausdrücklich, dass die
erzeugten Dokumente aus ihren Eingaben abgeleitet sind und nicht der Werkzeuglizenz
unterliegen. Damit erzwingt dieser Einsatz keinen Lizenzwechsel des MIT-Projekts.
Originaltext, feste Version, Herkunft und Verwendungsnachweise liegen unter
`licenses/third-party/doxygen/1.18.0`. Die Lizenzhinweise der erzeugten HTML-Assets
bleiben unverändert. Eine spätere Weitergabe des Werkzeugs selbst erfordert eine
gesonderte Prüfung und Erfüllung der GPL-Quellcodepflichten.

## Bestandsaufnahme vom 13.10.2026

Doxygen 1.18.0 erfasste nach den Ergänzungen 7.168 Typen und Mitglieder aus allen
14 Produktionsmodulen, davon 1.254 mit Beschreibungstext. 2.559 Mitglieder besitzen
mindestens einen benannten Parameter ohne eigenen Beschreibungstext. Es handelt
sich um Parser-Ergebnisse einschließlich privater Mitglieder, nicht um eine
Dokumentationsqualitätsquote. Die Referenz wird erfolgreich erstellt; Parserwarnungen
zu modernen C#-Konstrukten bleiben in `warnings.log` sichtbar. Solution-Build und die
drei Tests der Bestandsauswertung bestehen.

## Ergänzung vom 14.10.2026: Einstieg und Ablaufgründe

Alle 188 eigenen C#-Quelldateien besitzen nun einen Doxygen-Dateikommentar mit
Aufgabe und fachlicher Einordnung. Der Python-Worker hat bereits eine ausführliche
Modulbeschreibung; seine Kommentare wurden an den Protokollstreams und der
Nachrichtensperre ergänzt. Generierter Code unter bin/obj bleibt unverändert.

Zusätzliche Kommentare erklären insbesondere Transaktionsgrenzen, idempotente
Wiederholung, überholte Anmeldeantworten, UI-Dispatcher, unabhängige Speicherfilter,
SCPI-Antwortzuordnung sowie Signalwechsel bei der Prüfplatzübergabe. Der Vertrag
für Übergabekanäle und die Priorität der Python-Umgebung haben eigene XML-Texte.
Veraltete Hinweise auf noch ausstehende Datenbankanbindung wurden korrigiert.

Die Änderungen wurden über einen Roslyn-Tokenvergleich aller C#-Dateien und einen
Python-Tokenvergleich gegen den vorherigen Commit geprüft: ausführbare Tokens und
eingebettete Quelltext-/SQL-Strings bleiben nach Normalisierung der Zeilenenden
identisch. Solution-Build und Doxygen-Erzeugung bestehen. Dateikommentare sind
Orientierungshilfen, kein Nachweis vollständig beschriebener Mitglieder oder einer
abgeschlossenen fachlichen Prüfung aller Implementierungen.
