# Messwertverläufe und Grenzbandprüfung

Mit **Messwertverlauf aufnehmen** werden Geräte-/Variablenwerte über eine einstellbare Dauer erfasst, live angezeigt und gegen versionierte Grenzkurven bewertet. Für selbst gesteuerte Abläufe stehen zusätzlich **Messwertverlauf beginnen**, **Messpunkt ergänzen** und **Verlauf beenden und bewerten** bereit. X-Achse ist Zeit in Sekunden; andere numerische Bezugsgrößen sind noch nicht enthalten.

## Zwei getrennte Grenzen: Bewertung und vorzeitiges Aufnahmeende

Das normale Grenzband entscheidet immer über PASS/FAIL. Die zusätzliche prozentuale Abweichung erweitert dieses zulässige Band nicht. Sie entscheidet ausschließlich, ob die Aufnahme früher endet. Die nächsten Prüfschritte laufen anschließend regulär weiter; ein späteres PASS überschreibt das bereits entstandene FAIL nicht.

Beispiel **4–6 V**, **10 % zusätzliche Abweichung**, **Messdauer 10 s**:

| Messwert | Ergebnis | Aufnahme |
| --- | --- | --- |
| 5 V | PASS nur bei vollständiger, sonst fehlerfreier Messung | bis 10 s |
| 3,999 V | FAIL | bis 10 s weiter |
| 3,6 V | FAIL | bis 10 s weiter; Zusatzgrenze nur berührt |
| 3,599 V | FAIL | sofort beenden, dann nächster Prüfschritt |
| 6,001 V | FAIL | bis 10 s weiter |
| 6,601 V | FAIL | sofort beenden, dann nächster Prüfschritt |

Für Prozentsatz `p` liegen die zusätzlichen Abbruchgrenzen bei `Untergrenze − |Untergrenze| × p/100` und `Obergrenze + |Obergrenze| × p/100`. Negative Grenzen werden ebenfalls über ihren Betrag berechnet; bei −6 bis −4 V und 10 % ergibt sich −6,6 bis −3,6 V. Eine Grenze von 0 hat keinen prozentualen Zuschlag. Nur striktes Überschreiten löst das vorzeitige Ende aus; numerisches Rundungsrauschen der Prozentrechnung wird toleriert. Die normale Grenzprüfung bleibt strikt und unabhängig davon. Mit 0 % endet die Aufnahme bei jeder echten Grenzüberschreitung. Die Zusatzfunktion ist standardmäßig ausgeschaltet.

## Eigene Abbruchgrenzkurven

Eine zweite Kurve mit den Kanälen **Untergrenze** und **Obergrenze** erstellen und im Aufnahmebaustein
unter **Abbruchgrenzkurven** mit `ID@Version` eintragen. Die normalen Grenzkurven bewerten weiter PASS
oder FAIL. Eine Überschreitung der Abbruchkurve beendet die Aufnahme sofort mit FAIL und zeigt beide
Abbruchlinien an. Das bloße Berühren der Abbruchlinie beendet die Aufnahme nicht.

Bei mehreren Messkanälen die Abbruchkurven in Kanalreihenfolge kommasepariert angeben, z. B.
`abbruch-spannung@1,,abbruch-leistung@1`. Ein leerer Eintrag verwendet die vorhandene Prozentregel,
sofern sie aktiviert ist. Ein eingetragenes Abbruchband ersetzt die Prozentregel für diesen Kanal und
ist auch bei ausgeschalteter Prozentregel aktiv. Einheit und Zeitabdeckung werden wie beim normalen
Grenzband geprüft. Beispiel: Normale Untergrenze 0 V, Abbruch-Untergrenze −0,2 V. Ein Messwert von
−0,1 V ergibt FAIL bei weiterlaufender Aufnahme. Unter −0,2 V endet die Aufnahme vorzeitig.

## Grenzkurven und vollständiger Zeitraum

Unter **Kurven** eine Kurve mit Kanälen **Untergrenze** und **Obergrenze** und der Einheit der Messgröße anlegen. Vorhandener numerischer Generator/Formeleditor und CSV-Import können verwendet werden. [CSV-Beispiel](beispiele/grenzband-spannung.csv): erste Spalte als Zeit in Sekunden, restliche Spalten als die beiden Kanäle zuordnen. Die Vorlage reicht bis 1 s; für längere Aufnahmen Zeitpunkte entsprechend erweitern.

Jedes Grenzband muss den **gesamten eingestellten Zeitraum von 0 bis Messdauer** abdecken. Bei 10 s Messdauer muss die Grenzkurve bei 0 beginnen und mindestens bis 10 s reichen. Zwischen den Stützpunkten wird linear interpoliert; einzelne Punkte müssen nicht zu jedem Abfragetakt vorhanden sein. Ein zu spät beginnendes oder zu früh endendes Grenzband wird **vor der ersten Geräteabfrage** abgelehnt. Das gilt für jeden Kanal, auch wenn die Aufnahme wegen FAIL später früher endet. Es gibt keine stillschweigende Extrapolation.

Grenzzeiten müssen nichtnegativ, endlich und streng steigend sein; Untergrenze darf nie oberhalb Obergrenze liegen. Mindestens zwei, höchstens 10.000 Grenzstützpunkte sind zulässig. Kurven mit Qualitätshinweis zuerst bereinigen. Eine Version speichern und z. B. `spannungsband@1` im Baustein angeben. Ohne Versionsangabe wird die neueste Kurve der laufenden Projektfassung verwendet; tatsächliche Kennung und Inhaltshash bleiben im Ergebnis erhalten. Einheiten werden nicht automatisch umgerechnet.

Grenzkurven werden mit dem Prüfprogramm versioniert, freigegeben und gehasht. Die Anzeige „Verwendet in“ berücksichtigt beide Aufnahmearten. Geänderte, außerhalb eines Prüflingstests verwendete Grenzkurven verhindern einen unverträglichen Versionswechsel während der Prüfsitzung.

## Empfohlen: zeitgesteuerter Aufnahmebaustein

**Messwertverlauf aufnehmen** in den **Prüflingstest** legen, damit Ergebnis und Daten dessen Seriennummer zugeordnet werden.

1. Aufnahmekennung, z. B. `Verlauf`, eintragen.
2. Kanalnamen, Einheiten und Grenzkurven kommasepariert in gleicher Reihenfolge angeben: `Spannung,Strom`, `V,A`, `spannungsband@1,stromband@1`.
3. **Messdauer** in Sekunden und **Abfrageintervall** setzen, etwa `10` und `0.1`. Das ergibt regulär 101 Abfragen einschließlich Start- und Abschlusspunkt. Ist die Dauer kein Vielfaches des Intervalls, wird ein zusätzlicher Abschlusspunkt bei der Messdauer abgefragt.
4. Quelle **Geräte** liest die kommaseparierten abstrakten Gerätenamen unter Beachtung der vorhandenen Ressourcen-/Sperrprüfung. Geräteeinheiten müssen zur Aufnahme passen. Quelle **Ausdrücke** wertet semikolongetrennte Ausdrücke/Variablen aus, z. B. `U_mess;I_mess`; der Autor verantwortet hier die deklarierten Einheiten.
5. Optional **Aufnahme bei zusätzlicher Abweichung vorzeitig beenden (FAIL)** aktivieren und den Prozentsatz setzen. Die Aufnahme kehrt bei Auslösung sofort mit FAIL zurück. Folgende Schritte und parallele Geschwisterzweige werden weiter ausgeführt.
6. Optional **ErgebnisZiel** auf eine vorher deklarierte Stringvariable setzen. Sie erhält die kanalweisen Auswertungsdetails als JSON.

Die Zeitpositionen sind Software-Abfragepositionen. Start- und Abschlusspunkt werden 0 bzw. der eingestellten Messdauer zugeordnet; Geräte werden nacheinander abgefragt. Gerätelatenz und Betriebssystemplanung können die tatsächliche Laufzeit verlängern. Dies ist keine hardwareseitig synchrone DAQ-Erfassung. Überschreiten einzelne Abfragen oder Taktverspätungen die **maximale Messlücke / Abfragedauer**, ergibt die Aufnahme ERROR. Abfrageintervall und maximale Lücke zur Gerätegeschwindigkeit passend wählen. Ein zu kleines Punktlimit wird bereits bei der Bausteinkonfiguration gemeldet.

## Alternative: Messpunkte selbst steuern

Alle drei manuellen Bausteine in denselben Prüflingstest legen:

1. **Messwertverlauf beginnen:** Kanäle/Grenzbänder wie oben angeben. Mit **Messdauer > 0** muss die Messung ebenfalls den ganzen Zeitraum 0..Dauer liefern; Grenzbänder werden beim Start entsprechend geprüft. `0` behält das bisherige manuelle Ende ohne fest vorgegebenes Zeitfenster bei.
2. **Messpunkt ergänzen:** Datenquelle wie oben. **Zeit** ist ein Ausdruck in Sekunden; leer verwendet die Empfangszeit seit Aufnahmebeginn. Zeiten müssen streng steigen. Normale Grenzverletzungen werden sofort als FAIL festgehalten, auch wenn die Live-Anzeige gerade gedrosselt wird. Sie beenden die Aufnahme nicht und können durch spätere gute Werte nicht gelöscht werden.
3. Bei erreichter eingestellter Dauer oder Zusatzabweichung liefert der Messpunktschritt das Ergebnis samt Kurven. Weitere Messpunktschritte für diese bereits beendete Aufnahme lesen keine Geräte/Werte mehr und bleiben unbewertet. Die umgebende selbst programmierte Wiederholung und deren Wartebausteine werden nicht automatisch verlassen. Für die automatische zeitliche Steuerung stattdessen den empfohlenen Aufnahmebaustein verwenden.
4. **Verlauf beenden und bewerten:** Kennung freigeben und optional Details in eine Stringvariable schreiben. Ist die Kurve bereits beim beendenden Messpunktschritt gespeichert worden, wird keine zweite Kopie abgelegt. Ein zu früh beendetes Zeitfenster, ein fehlender Anfang bei 0 oder weniger als zwei normale Messpunkte erlauben kein PASS.

Ohne festgelegtes Zeitfenster prüft **Mindestdauer** weiterhin die Zeit zwischen erstem und letztem Messpunkt; `0` bewertet nur den aufgezeichneten Abschnitt. Ein Punkt nach der eingestellten Dauer ist ungültig. Bei manuell vorgegebenen Zeiten für den Abschlusspunkt exakt die Dauer liefern; es wird kein fehlender Messwert extrapoliert.

Im Baustein **Messpunkt ergänzen** kann optional **Vorzeitiges Ende → Boolvariable** gesetzt werden. Der Wert
ist nur dann `true`, wenn dieser Messpunkt die Aufnahme aufgrund der Zusatzabweichung vorzeitig beendet.
Damit können reguläre Folgeschritte über eine Gruppenbedingung übersprungen werden, während die
Exit-Gruppe die Aufnahme abschließt. Ein gewöhnliches FAIL innerhalb der Zusatzgrenze setzt das Ziel auf
`false`. Übersprungene Messpunkte ändern die Variable nicht.

## Messzeit beim Umschalten pausieren

Bei einer manuell gesteuerten Aufnahme vor dem Umschalten den Baustein **Messzeit pausieren / fortsetzen**
mit **Pausieren** und der Aufnahmekennung ausführen. Danach denselben Baustein mit **Fortsetzen** ausführen.
**Messpunkt ergänzen** verwendet bei leerem Feld **Zeit** die verstrichene Aufnahmezeit ohne Pausen.
Explizit eingetragene Zeitwerte werden unverändert ausgewertet. Während einer Pause liest der Messpunktschritt
keine Geräte und ergänzt keine Punkte. Der Verlauf und die bisherige Grenzbewertung bleiben erhalten.
Die Pause gilt für manuell gesteuerte Aufnahmen zwischen einzelnen Messpunkten.

Die [Demo mit simuliertem Spannungsmessgerät](beispiele/werkerkurve-messzeitpause.tproj.json) zeigt im Szenario **Kurve** eine dreisekündige Umschaltung.

## Bewertung, Abbruch und Grenzen

Messkurve und Grenzlinien werden stückweise linear verbunden. Die normale Bewertung berücksichtigt alle Messzeiten und alle dazwischenliegenden Grenzstützpunkte. So werden Verletzungen zwischen Messpunkten erkannt. Die Zusatzabweichung berücksichtigt außerdem Nulldurchgänge der Grenzen. Eine Verletzung zwischen Messpunkten kann erst beim Empfang des nächsten Punkts erkannt werden.

Berühren einer normalen Grenzlinie ist standardmäßig zulässig und kann deaktiviert werden. Die prozentuale Zusatzgrenze ersetzt diese Einstellung nicht. Bekannte Verletzungen bleiben FAIL, auch wenn später wieder gute Messwerte auftreten. Unvollständige Daten, Geräte-/Einheitenfehler, ungültige Zahlen, Zeitlücken und fehlende Kanäle ergeben ERROR. Ein Werkerabbruch bleibt zusätzlich ABORT. Vorzeitiges Ende wegen Zusatzabweichung ist dagegen ein bekanntes **FAIL** und wird mit Ursache gespeichert, obwohl `Complete` false bleibt.

Aufnahmen sind je Prüfling und Lauf getrennt. Maximal acht Kanäle pro Aufnahme, acht gleichzeitig aktive Aufnahmen je Lauf und 100.000 Messpunkte pro Kanal sind erlaubt. Eine aktive Kennung muss beendet/freigegeben werden, bevor sie erneut benutzt wird. Nicht abschließend bewertete Aufnahmen werden beim Kontextende als unvollständig gesichert; gezielt ausgeführte Debug-Aufnahmen gehören zum separaten Debugkontext.

## Live-Diagramm und gespeicherte Daten

Der erste Wert erscheint sofort als farbiger Punkt. Weitere tatsächlich gelieferte Messwerte stehen an ihren Zeitpositionen und werden durch Linien verbunden. Beide ersten Punkte, die erste Grenzverletzung und das Aufnahmeende werden sofort gemeldet; sonst wird die Darstellung bei schnellen Aufnahmen auf etwa vier Aktualisierungen pro Sekunde begrenzt. Die Kurve ist in der Einzelansicht und getrennt je Seite in der Doppelplatzansicht sichtbar. Auf kleinen Bildschirmen bleibt der Bereich scrollbar.

Die Anzeige nennt bereits während der Aufnahme **FAIL festgestellt**, sobald eine normale Grenzverletzung bekannt ist. Dies beendet die Aufnahme ohne zusätzliche Abweichung noch nicht. Messung, Unter-/Obergrenze und rote Verletzungsmarkierungen bleiben sichtbar. Bis zu 1.000 sichtbare Originalpunkte je Kanal werden als Punkte dargestellt, bei größeren Mengen einschließlich erstem und neuestem Punkt reduziert. Zusätzlich interpolierte Stützpunkte erhalten keine Punktmarkierung. Die Bewertung verwendet weiterhin sämtliche Daten.

Die vorhandenen Kurven-Speicheroptionen führen Messung und Grenzen zu Ergebnisdatei und Datenbank; bei ausgeschalteten Speicherzielen werden sie dort nicht abgelegt. Zum Wiederöffnen die Ergebnisnachschau nutzen. Je Messkanal enthält `Waveform.Quality` nach `Grenzband:` ein JSON mit Grenzkurvenkennung, Hash, Originalmesszeiten, `PlannedDuration`, Vollständigkeit und Verletzungsdetails. `EarlyStop` enthält bei vorzeitigem FAIL Prozentsatz, Kanal, Zeit, Wert und Grenzen. `ErgebnisZiel` liefert dieselben kanalweisen Details. Daten und Herkunft bleiben auch beim vorzeitigen Ende erhalten.


## Direkt ladbares Beispiel

[Grenzband-Demo ohne Hardware](beispiele/README-grenzband-demo.md) enthält vier auswählbare Fälle einschließlich 3,999 V, vorzeitigem Ende bei 3,599 V und einer Live-Kurve aus sechs Einzelpunkten. [Projektdatei öffnen/herunterladen](beispiele/grenzband-demo.tproj.json).
