# Elemente aktivieren und Haltepunkte konfigurieren

Ab Version 1.1.0 lassen sich Schritte und normale Untergruppen im Ablaufeditor
vorübergehend deaktivieren. Sie werden nicht gelöscht. Außerdem lassen sich
Haltebedingungen sowohl für Ablaufkarten als auch für einzelne Codezeilen setzen.

## Schritte und Gruppen deaktivieren

Karte auswählen und in den Eigenschaften **Element aktiv** ausschalten.
Alternativ die Schaltfläche **Aktiv / inaktiv** verwenden. Zum Reaktivieren wieder
einschalten. Deaktivierte Karten erscheinen blasser mit dem Hinweis „deaktiviert“;
auch Kinder einer deaktivierten Gruppe sind entsprechend gekennzeichnet.

Ein deaktivierter Schritt wird vollständig übersprungen: kein Gerätezugriff,
kein Prozessstart, keine Variablenänderung und kein Haltepunkt. Eine deaktivierte
Gruppe wird gar nicht betreten; ihre Kinder, Ressourcenreservierung und eigene
Exit-Gruppe entfallen ebenfalls. Die Exit-Gruppe eines weiterhin aktiven Elternteils
bleibt davon unberührt. Die festen Bereiche Init, Loop und Finalization werden
selbst nicht abgeschaltet; stattdessen ihre Kinder deaktivieren.

Überspringen erzeugt keinen PASS und keine fiktive Messung. Die Gruppenbewertung
entsteht aus den tatsächlich ausgeführten Elementen. Die Werkeransicht zeigt die
übersprungenen Punkte entsprechend; das Ergebnisprotokoll enthält den Hinweis
„Deaktiviert“. Eine verpflichtende Seriennummer bleibt verpflichtend, auch wenn
man versehentlich ihren Eingabeschritt abschaltet.

Aktivierung bleibt beim Speichern, Kopieren, Rückgängig/Wiederholen und im
Versionsvergleich erhalten. Änderungen betreffen den Entwurf. Eine bereits
laufende Prüfung behält ihre eigene Projektkopie; eine aktive Aktion wird dadurch
nicht plötzlich abgebrochen. Im gespeicherten Projekt kennzeichnet `disabled: true`
den abgeschalteten Knoten. Ältere Software kennt das Feld nicht: solche Projekte
nur mit einer Softwareversion ab 1.1.0 verwenden.

Die Konfiguration eines deaktivierten Schritts bleibt bearbeitbar. Ablauf-Codeblöcke
in deaktivierten Teilbäumen werden bei der Entwicklungs-/CLI-Buildprüfung nicht
übersetzt. Bibliotheksquellen und deklarierte Hardwareprofile bleiben eigenständige
Projektbestandteile und behalten ihre Prüfungen.

## Haltepunkte an Ablaufkarten

Karte auswählen, **Ablauf-Haltepunkt** aufklappen und **Haltepunkt aktiv** einschalten.
F9 schaltet den Haltepunkt der ausgewählten Karte weiterhin um.

| Auswahl | Wirkung |
| --- | --- |
| Immer | Jeder passende Treffer hält; unveränderter Standard. |
| Bei FAIL oder ERROR | Nur bei einer dieser beiden Bewertungen halten. |
| Nur bei FAIL | Ausschließlich bei FAIL halten. |
| Nur bei ERROR | Ausschließlich bei ERROR halten. |
| Wenn Ausdruck wahr ist | Typisierten Ausdruck gegen die Variablen auswerten. |

Ablauf-Haltepunkte liegen weiterhin **nach** der Ausführung und **vor** der
Bewertungsrückgabe. Ein haltender Schritt hat seine Ausgänge bereits geschrieben.
Bei Gruppen können die gerade verlassenen lokalen Variablen aus dem Schnappschuss
für die Bedingung verwendet werden; sie sind nicht dadurch wieder aktiv.

Beispiele für Ausdrücke:

```text
Spannung > 240
Debug.Iteration >= 3 && Temperatur > 60
Debug.Bewertung == "FAIL"
Debug.Treffer % 5 == 0
```

Es gilt dieselbe sprachunabhängige Ausdruckssprache wie bei Gruppenbedingungen:
Vergleiche, Klammern, `&&`, `||` und typisierte Variablen. Es wird kein C#- oder
Python-`eval` ausgeführt. `Debug.Treffer`, `Debug.Iteration` und an Ablaufkarten
`Debug.Bewertung` sind reservierte Debugwerte. Normale Namen suchen im aktuellen
Variablenkontext; bei einer gerade verlassenen Gruppe haben deren lokale Werte
Vorrang. `../Name` greift bei dieser Gruppe auf den Elternkontext zu.

## Trefferzahlen

**Ab Treffer** legt den ersten berücksichtigten Treffer fest, **alle … Treffer**
den Abstand danach. Standard ist jeweils **1**. Beispielsweise ab Treffer 3,
alle 2 Treffer: mögliche Haltepositionen 3, 5, 7 usw. Ein zusätzlicher
Bewertungs-/Ausdrucksfilter muss dort ebenfalls passen.

Gezählt wird jede erreichte, ausgeführte Halteposition, nicht nur eine wahre
Bedingung. Jede Position wird genau einmal gezählt. Der Zähler beginnt pro
Prüflauf neu; eine geänderte Filtereinstellung oder erneute Aktivierung beginnt
die Zählung dieses Haltepunkts ebenfalls neu. Bei parallelen Aufrufen desselben
Knotens zählt der Haltepunkt deren Treffer gemeinsam.

## Codezeilen-Haltepunkte

CodeBlock oder Bibliotheksaufruf auswählen und **Codezeilen-Haltepunkt** aufklappen.
Die gewünschte **Quelltextzeile ab 1** eintragen, den Haltepunkt aktivieren und
**Immer** oder **Wenn Ausdruck wahr ist** wählen. Für andere Zeilen die Zeilennummer
wechseln. Jede Zeile behält ihren eigenen Filter und ihre eigene Trefferzählung.
Eine Codezeile hat noch keine fertige Schrittbewertung; FAIL-/ERROR-Filter gehören
daher an die Ablaufkarte.

Beispiel für Python oder C# bei einer Codezeile innerhalb einer Schleife:

```text
i == 3 && Debug.Iteration == 1
```

Codebedingungen verwenden die vom Worker erfassten Variablen am Haltepunkt sowie
`Debug.Treffer` und `Debug.Iteration`. C# erfasst zusätzlich Zählvariablen von
`for`-/`foreach`-Schleifen beim Eintritt in ihren Block. Verfügbar ist der
Debug-Schnappschuss des Workers, keine beliebige Auswertung aller Sprachobjekte.
Bei nativem Debugging werden primitive GDB-Werte für Bedingungen vor der
Halteentscheidung eingelesen und die Inspektion anschließend wiederverwendet.
Komplexe native Werte bleiben in ihrer textuellen GDB-Darstellung.

Bei Sprachen/Umgebungen mit reduziertem Debugging bleibt es beim Halt am
Bausteineintritt. Codezeilen werden dort nicht künstlich simuliert; die gesetzten
Filter werden am vorhandenen Eintrittsschnappschuss geprüft. Der globale Schalter
„Bei Codeeintritt halten“ und explizite Einzelschritte bleiben eigenständige
Halteanforderungen und können auch dann halten, wenn ein Filter nicht zutrifft.

## Fehler und Speicherung

Ungültige Syntax oder Trefferzahlen zeigt die Eigenschaftenansicht an; die letzte
gültige Einstellung bleibt bestehen. Kann ein syntaktisch gültiger Ausdruck zur
Laufzeit nicht ausgewertet werden, hält der Debugger mit einer Diagnose an. Ein
Tippfehler soll einen gewünschten Halt nicht unbemerkt unterdrücken.

Gespeichert werden die **Filtereinstellungen** im Projekt. Ob ein Haltepunkt gerade
aktiv ist, bleibt wie bisher sitzungs- und prüfplatzbezogen. Das Öffnen eines
Projekts aktiviert also nicht automatisch Debug-Halte für den Werkerbetrieb.
Beim erneuten Einschalten werden die gespeicherten Filter verwendet. Karten und
Codezeilen haben getrennte Regeln; „Immer“ bleibt jeweils die Vorgabe.

Gleichzeitige echte Halte werden nacheinander bedient. Eine falsche Bedingung
wartet nicht hinter dem Haltepunkt eines anderen parallelen Zweigs. Haltefilter
werden beim Erreichen der Position ausgewertet; eine bereits wartende
Halteanforderung kann noch aus der vorherigen Filtereinstellung stammen.

Im Ablaufeditor und in der Baustein-Bibliothek zeigt der Codeeditor links die
Quelltextzeilennummern ab 1 an. Diese Nummern entsprechen dem Feld **Zeile**
für Code-Haltepunkte. Die Nummerierung bleibt beim horizontalen Scrollen stehen
und folgt beim vertikalen Scrollen den sichtbaren Codezeilen.
