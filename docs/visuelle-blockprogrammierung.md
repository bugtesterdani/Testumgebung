# Visuelle Blockprogrammierung

Ab Version 1.1.1 bietet der Ablaufeditor zwei Darstellungen desselben Projekts.
Oben **Blockansicht** einschalten. Die Darstellung ist eine Sitzungseinstellung;
das Prüfprogramm bleibt eine normale `.tproj.json`-Datei. Speichern, Freigabe,
Simulation, Haltepunkte, deaktivierte Elemente, Geräte und Codebausteine nutzen
dieselbe Engine und denselben Projektstand.

## Ablaufblöcke zusammenstecken

![Blockansicht mit verschachtelten Gruppen](handbuch/bilder/16-blockansicht.png)

Links stehen die bisherigen Bausteine sowie die Kategorie **Steuerung**:

| Vorlage | Verhalten |
| --- | --- |
| Wenn … | Gruppe mit Eintrittsbedingung |
| Wenn … dann … sonst | Bedingung einmal prüfen, danach genau einen Zweig ausführen |
| Wiederhole … mal | Gruppe mit anfangs drei Wiederholungen |
| Wiederhole solange … | Bedingung vor jeder Iteration prüfen; startet mit false |
| Wiederhole bis … | Bedingung nach der Iteration prüfen; startet mit true |
| Parallel ausführen | Direkte Kinder gleichzeitig starten und gemeinsam abwarten |
| Gruppe | Eigener Gültigkeitsbereich mit optionaler Exit-Gruppe |

Ein Bausteinklick fügt relativ zur Auswahl ein. Alternativ aus der Palette auf
einen Block ziehen: oberes Viertel = davor, unteres Viertel = danach,
Gruppenmitte oder leere Gruppenfläche = hinein. Bestehende Blöcke lassen sich
genauso verschieben. Ganze Gruppen nehmen ihre Kinder mit. Start, Loop und Ende
bleiben die festen Programmabschnitte. Unzulässige Selbstverschachtelungen
werden abgewiesen.

**Werte …** oder ein Klick auf den Block wählt dessen Eigenschaften rechts.
Dort Namen, Kommentar, Parameter, Bedingungen, Wiederholung und Schnittstellen
bearbeiten. Die vorhandenen Schaltflächen für Verschieben, Einrücken,
Ausrücken, Kopieren, Löschen und Rückgängig bleiben nutzbar. Gruppen können
mit +/− eingeklappt werden. Die Spaltenteiler und Bildlaufleisten schaffen Platz
für größere Programme; Farben werden zusätzlich durch Namen und Hinweise erklärt.

### Wenn / Dann / Sonst

Die Vorlage besteht im bestehenden Format aus einer umgebenden Gruppe,
**Bedingung einmal prüfen**, einer **Dann**-Gruppe und einer **Sonst**-Gruppe.
Die eigentliche Bedingung am ersten Block unter **Ausdruck → Blöcke …** ändern.
Die Prüfaktionen in die leeren Dann-/Sonst-Bereiche ziehen.

Eine lokale boolesche Variable mit Präfix `_BlockZweig_` hält die Entscheidung
für die beiden Zweige fest. Änderungen an den ursprünglichen Eingangsvariablen
im Dann-Zweig ändern deshalb nicht nachträglich die Auswahl des Sonst-Zweigs.
Bei jeder erneuten Ausführung wird die Bedingung wieder einmal geprüft.
Die Vorlagenbestandteile gehören zusammen. Wer sie im normalen Editor bewusst
umbaut oder die Entscheidungsvariable aus Code überschreibt, verändert die
Programmlogik. Die Variable ist kein zusätzlicher globaler Systemwert.

## Variablen anlegen und auswählen

**Variable anlegen …** öffnet einen Dialog für Gruppe, Namen, Typ und Anfangswert.
Leer bedeutet den Standardwert des gewählten Typs. Der Gruppenbereich bestimmt
die Sichtbarkeit. Gemeinsame Auftragsdaten im **Hauptablauf** anlegen.
Vorhandene Namen und reservierte Systemnamen werden nicht überschrieben.

An Zielvariablen öffnet **Blöcke …** eine Auswahlliste statt eines freien Ausdrucks.
Schreibgeschützte Werte wie `GruppenErgebnis` sind dort keine Schreibziele.
Bei Eingängen und Berechnungen können sichtbare Elternvariablen direkt gewählt
werden; die Liste nennt Pfad, Typ und Herkunft. Verdeckte gleichnamige Werte sind
über ihren Elternpfad erreichbar. Bausteingrenzen bleiben erhalten.

## Berechnungen und Bedingungen aus Blöcken bauen

![Ausdrucksbaukasten mit Vergleich und UND](handbuch/bilder/17-ausdrucksbloecke.png)

An Ausdrucksparametern **Blöcke …**, bei Gruppen **Bedingung aus Blöcken …**
anklicken. Bibliotheksein- und -ausgänge haben **Blöcke / Variable …**.
Feste Zahlenparameter bleiben die vorhandenen Zahlenfelder.

1. Einen Steckplatz oder vorhandenen Block anklicken.
2. Links einen Werte-, Variablen-, Rechen-, Vergleichs- oder Logikblock einsetzen
   oder in den Steckplatz ziehen.
3. Zahlen und Texte eingeben, Ja/Nein schalten oder eine Variable auswählen.
4. Weitere Blöcke in die Eingänge des Operators einsetzen.
5. Unten Vorschau und Ergebnistyp prüfen, dann **Übernehmen**.

**↶** legt einen Operator um die aktuelle Auswahl. Beim Wechsel zwischen
Operatoren gleicher Bauart bleiben verbundene Eingaben erhalten. Vorhandene
Ausdrucksblöcke können in andere Steckplätze bewegt werden; **Strg** zieht eine
unabhängige Kopie. Selbstverschachtelungen und fremde Dialogobjekte werden
abgewiesen. Leere Steckplätze sind keine verschiebbaren Werte.

Verfügbar sind Zahl, Text, Ja/Nein und Variable sowie `+`, `−`, `×`, `÷`, Rest,
Vorzeichenwechsel, Vergleiche und **UND**, **ODER**, **NICHT**. Text plus Text
verbindet Zeichenketten. Nicht passende Typkombinationen und leere Steckplätze
zeigen eine Meldung und verhindern die Übernahme. Gruppenbedingungen erwarten
Ja/Nein; Bibliothekseingänge ihren Porttyp, Zuweisungen bei bekannter Zielvariable
deren Typ. Zielvariablen einer Bibliothek müssen zum Ausgabetyp passen.

`1` ist eine Ganzzahl, `1.0` beziehungsweise `1,0` eine Gleitkommazahl.
Die bestehende Ganzzahldivision bleibt erhalten: `3 / 2` ergibt 1,
`3.0 / 2` ergibt 1.5. Eingegebene Dezimalkommas werden für den gespeicherten
Ausdruck in Punkte übersetzt. Klammerung, Elternpfade und Kurzschlussauswertung
von UND/ODER entsprechen der vorhandenen Ausdruckssprache. Die Typprüfung
liest keine echten Laufzeitwerte und führt keine Gerätezugriffe aus.
Wertabhängige Fehler, etwa Division durch null, sind weiterhin in der Simulation
beziehungsweise Ausführung zu prüfen.

## Übernehmen, Abbrechen und bestehende Programme

Der Ausdrucksdialog bearbeitet zunächst eine eigene Kopie. **Abbrechen** oder
das Schließen des Fensters verändert das Projekt nicht. **Übernehmen** schreibt
den fertigen Prüfausdruck als einen eigenen Rückgängig-Schritt. **Leer / Standard**
ist nur an optionalen Feldern vorhanden. Änderungen an einem zwischenzeitlich
gewechselten Projekt oder veränderten Zielwert werden nicht überschrieben.

Im Dialog gibt es zusätzlich einen eigenen Rückgängig-/Wiederholen-Verlauf.
Er betrifft nur die aktuelle Berechnung. Nach der Übernahme greift wieder der
Verlauf des Ablaufeditors.

Bestehende gültige Ausdrücke werden über den vorhandenen Parser als Blöcke
übernommen. Beim Übernehmen kann die Schreibweise durch eindeutige Klammern
vereinheitlicht werden; beim Abbrechen bleibt der Originaltext exakt erhalten.
Unbekannte Variablen oder unpassende Typen müssen vor einer neuen
Übernahme korrigiert werden; mit Abbrechen bleibt der ursprüngliche Ausdruck
erhalten. Sehr große oder tiefe Ausdrücke bleiben im Texteditor bearbeitbar.
Die Blockansicht begrenzt einzelne Ausdrücke auf 256 Knoten und 32 Ebenen.
Die neue Ansicht schreibt weder eine zweite Programmiersprache noch separate
Bildschirmkoordinaten in die Projektdatei.

Ein ausführbares Beispiel ohne Hardware liegt unter
[Beispielprogramm](beispiele/blockprogrammierung/README.md).
