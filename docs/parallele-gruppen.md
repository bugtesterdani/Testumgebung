# Schritte einer Gruppe parallel ausführen

Ab Version 1.1.0: Gruppe im Ablaufeditor auswählen und unter den Eigenschaften
**Schritte und Untergruppen parallel starten** einschalten. Die Ablaufkarte zeigt
dann den Zusatz **parallel**. Ohne Häkchen gilt die bisherige Reihenfolge.

## Ausführung

Alle direkten Kinder werden als unabhängige Zweige gestartet. Eine Untergruppe
arbeitet innerhalb ihres Zweigs weiterhin nacheinander, solange ihre eigene
Paralleloption ausgeschaltet bleibt. Verschachtelte Parallelgruppen sind möglich.
Die übergeordnete Gruppe wartet auf alle Zweige einschließlich deren Exit-Gruppen.
Erst dann folgen die nächste Wiederholung, die Gruppen-Exit-Gruppe und nachfolgende
Schritte außerhalb dieser Gruppe.

Ein FAIL oder ERROR fließt wie bisher in die Gruppenbewertung ein. Bereits
laufende Geschwister werden dadurch nicht automatisch abgebrochen. Der
Werkerabbruch stoppt alle regulären Zweige kooperativ. Ihre Exit-Gruppen laufen
zum Aufräumen; ein weiterer Abbruch beendet die zuletzt gestartete aktive
Exit-Gruppe. Die Eltern-Exit-Gruppe startet erst nach Abschluss ihrer Kinder.

Bei einer Zeitspanne wird zwischen vollständigen parallelen Iterationen geprüft.
Die Option, die restlichen Schritte einer Iteration nach Fristablauf auszulassen,
ist für Parallelgruppen deaktiviert: Die Kinder werden gemeinsam gestartet.
Systemzeit und virtuelle Simulationszeit unterstützen überlappende Wartezeiten;
beim Zusammenführen gilt die späteste Endzeit der Zweige. Gerätesperren können die
ausführbare Parallelität begrenzen.

## Abhängigkeiten und Variablen

Seriennummer und Auftragsdaten **vor** der Parallelgruppe erfassen. Auch benötigte
Vorberechnungen gehören davor. Ein Zweig kann nicht voraussetzen, dass ein
Geschwisterzweig schon Ergebnisse geliefert hat. Ein typischer Ablauf ist:

1. Seriennummer erfassen.
2. Parallelgruppe mit Untergruppen „Messung links“ und „Messung rechts“.
3. Beide Untergruppen führen ihre intern abhängigen Schritte nacheinander aus.
4. Nach der Parallelgruppe die beiden Ergebnisse vergleichen.

Für jeden Zweig eigene Zielvariablen oder lokale Variablen verwenden. Einzelne
Variablenzugriffe sind typgesichert und gesperrt; eine ganze Rechnung wie
`Zaehler = Zaehler + 1` ist über mehrere Schritte kein atomarer Vorgang. Gemeinsame
Änderungen gegebenenfalls in Untergruppen mit derselben Ressourcensperre bündeln.

## Geräte und Sperren

Parallele Schritte reservieren benutzte Geräteschlüssel dynamisch vom ersten
Zugriff bis zum Schrittende. Zwei Schritte mit derselben Ressource beziehungsweise
demselben konfigurierten Sperrschlüssel greifen damit nicht gleichzeitig zu.
Unabhängige Geräte können gleichzeitig arbeiten.

Für eine zusammengehörige Transaktion über mehrere Schritte die Ressource an der
jeweiligen **Untergruppe** reservieren. Die Reservierung umfasst deren Schritte
und Exit. Geschwister-Untergruppen mit überschneidenden Reservierungen warten
aufeinander. Verschachtelte Gruppen dürfen ihre Elternreservierung weiter benutzen;
der Besitz eines Geschwisters ist keine eigene Reservierung. Bei Hardware mit
Reservierungspflicht bleibt eine passende Gruppenreservierung erforderlich.
Zyklen im Wartegraph werden als ERROR gemeldet, statt dauerhaft zu warten.

Die Paralleloption erzeugt keine hardwaregenaue Trigger-Synchronisation. Für eine
gemeinsame Messzeitbasis die dafür vorgesehene Kurvenaufnahme verwenden.

## Prüflinge und Debugging

Innerhalb **eines** Prüflingsdurchlaufs können beliebig viele Zweige laufen.
Mehrere Prüflingsdurchläufe dürfen in demselben Run nicht gleichzeitig betreten
werden, damit Seriennummern und Berichte eindeutig bleiben. Für zwei unabhängige
Prüflinge die vorhandene Konfiguration mit zwei Prüfplätzen verwenden. Der
Hauptablauf mit Init, Loop und Finalization bleibt geordnet.

Haltepunkte halten den jeweiligen Zweig an. Andere Zweige können bis zu ihren
eigenen Haltepunkten weiterlaufen. Gleichzeitige Ablauf-Haltepunkte und gleichzeitige
Code-Haltepunkte werden jeweils nacheinander bedient; ein Haltekontext wird nicht
von einem Geschwister überschrieben.

## Projektformat und Beispiel

An einer Gruppe bedeutet `"parallel": true` die neue Ausführungsart. Fehlt das Feld
oder steht es auf `false`, läuft sie nacheinander. Die Einstellung bleibt beim
Speichern, Kopieren und Erstellen eines grafischen Bausteins erhalten und erscheint
im Versionsvergleich. Solche Prüfprogramme mit einer Software ab 1.1.0 verwenden;
ältere Versionen kennen das Feld noch nicht.

Das Beispiel liegt in `docs/beispiele/parallele-gruppen/`. Es erfasst zuerst eine
Seriennummer und startet danach zwei intern sequenzielle Zweige parallel. Die
Szenariodatei lässt sich mit der Kommandozeile testen:

```powershell
testumgebung-cli test docs/beispiele/parallele-gruppen --junit ergebnisse.xml
```
