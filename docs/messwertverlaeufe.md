# Messwertverläufe und Grenzbandprüfung

Mit **Messwertverlauf aufnehmen** werden Geräte-/Variablenwerte über eine einstellbare Dauer erfasst, live angezeigt und gegen versionierte Grenzkurven bewertet. Für selbst gesteuerte Abläufe stehen zusätzlich **Messwertverlauf beginnen**, **Messpunkt ergänzen** und **Verlauf beenden und bewerten** bereit. X-Achse ist Zeit in Sekunden; andere numerische Bezugsgrößen sind noch nicht enthalten.

Unter **Messkanäle → Tabelle bearbeiten** stehen Kanalname, Einheit und die Kurvenreferenzen für Wert-Grenzbänder in derselben Zeile. In bestehenden Projekten gespeicherte Listen werden beim Öffnen der Tabelle zeilenweise angezeigt.

## Grenzband aus Formeln berechnen

In **Messwertverlauf beginnen** oder **Messwertverlauf aufnehmen** unter **Grenzauswertung** das **Berechnete Grenzband** wählen. In die zwei Formelfelder beispielsweise `4 + 0.2 * t` für die Untergrenze und `6 + 0.2 * t` für die Obergrenze eintragen. `t` ist die Messzeit in Sekunden. Konstante Zahlen sind ebenfalls gültig.

Die Formeln gelten für alle Kanäle in ihrer jeweiligen Einheit. Die Aufnahme berechnet und prüft die Grenzen zu jedem erfassten Messpunkt und zeichnet sie im Diagramm. Der optionale Sofortabbruch verwendet ebenfalls diese Grenzen. Ungültige Ausdrücke, nicht endliche Ergebnisse und eine Untergrenze oberhalb der Obergrenze ergeben einen Fehler.

## Steigung mit Min-/Max-Grenzen bewerten

In **Messwertverlauf beginnen** oder **Messwertverlauf aufnehmen** die **Grenzauswertung** wählen:

| Auswertung | Verwendete Punkte |
| --- | --- |
| Wert-Grenzband (Standard) | Messwerte gegen die eingetragenen Grenzkurven |
| Steigung je Nachbarpaar | 1–2, 2–3, 3–4, … |
| Steigung in getrennten Fenstern | bei 5 Punkten: 1–5, 6–10, 11–15, … |
| Steigung im gleitenden Fenster | bei 5 Punkten: 1–5, 2–6, 3–7, … |

Für jedes vollständige Fenster wird die Differenz zwischen End- und Startwert durch die verstrichene Messzeit in Sekunden geteilt. Eine Änderung von 2 V auf 5 V in 2 s ergibt +1,5 V/s. Das Diagramm zeigt diese Steigung am letzten Zeitpunkt des Fensters und die eingestellten Min-/Max-Grenzen. Die Grenzen gelten für jeden Kanal in seiner jeweiligen Einheit pro Sekunde.

Unvollständige Fenster am Ende bleiben unbewertet. Ohne vollständiges Fenster ergibt die Aufnahme ERROR. Der optionale Sofortabbruch verwendet die Steigungsgrenzen. Separate Abbruchgrenzkurven stehen für das Wert-Grenzband zur Verfügung.

Eine vollständig erfasste Haltephase unterhalb der Mindeststeigung zählt je Kanal als gültiges Steigungsfenster, auch ohne Zielband. Nur fehlende Fenster oder ausschließlich Anlaufausnahmen ergeben ERROR; beide Fälle werden getrennt erläutert. Ein stabiles Ziel darf bereits vor dem ersten vollständigen Fenster erreicht werden.

## Zwei getrennte Grenzen: Bewertung und vorzeitiges Aufnahmeende

Das normale Grenzband entscheidet immer über PASS/FAIL. Die zusätzliche Abweichung erweitert dieses zulässige Band nicht. Sie entscheidet ausschließlich, ob die Aufnahme früher endet. Die nächsten Prüfschritte laufen anschließend regulär weiter; ein späteres PASS überschreibt das bereits entstandene FAIL nicht.

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

Unter **Art der Abbruchabweichung** lässt sich **Absolut** auswählen. Der Zuschlag wird von der Untergrenze abgezogen und zur Obergrenze addiert. Er gilt in der Einheit der Auswertung, beispielsweise V bei Spannung oder V/s bei Steigung. Bei Steigungsgrenzen von 0 bis 4 V/s und einem Zuschlag von 0,5 V/s löst erst eine Steigung unter −0,5 oder über 4,5 V/s den Sofortabbruch aus. Prozent ist der Standard für bestehende Projekte.

## Eigene Abbruchgrenzkurven

Eine zweite Kurve mit den Kanälen **Untergrenze** und **Obergrenze** erstellen und im Aufnahmebaustein
unter **Abbruchgrenzkurven** mit `ID@Version` eintragen. Die normalen Grenzkurven bewerten weiter PASS
oder FAIL. Eine Überschreitung der Abbruchkurve beendet die Aufnahme sofort mit FAIL und zeigt beide
Abbruchlinien an. Das bloße Berühren der Abbruchlinie beendet die Aufnahme nicht.

Bei mehreren Messkanälen die Abbruchkurven in Kanalreihenfolge kommasepariert angeben, z. B.
`abbruch-spannung@1,,abbruch-leistung@1`. Ein leerer Eintrag verwendet die gewählte Abweichungsregel,
sofern sie aktiviert ist. Ein eingetragenes Abbruchband ersetzt die gewählte Abweichungsregel für diesen Kanal und
ist auch bei ausgeschalteter Abweichungsregel aktiv. Einheit und Zeitabdeckung werden wie beim normalen
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
5. Optional **Aufnahme bei zusätzlicher Abweichung vorzeitig beenden (FAIL)** aktivieren und die Abweichungsart sowie den Zuschlag einstellen. Die Aufnahme kehrt bei Auslösung sofort mit FAIL zurück. Folgende Schritte und parallele Geschwisterzweige werden weiter ausgeführt.
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

## Bei vorzeitigem Messende direkt zum Exit

In **Messpunkt ergänzen** oder **Messwertverlauf aufnehmen** die Option
**Bei vorzeitigem Ende zur Prüflings-Exit-Gruppe** aktivieren. Wenn die Prozentgrenze oder
die Abbruchgrenzkurve die Aufnahme vorzeitig beendet, hält der Baustein den Messwert und FAIL fest.
Die Ablaufsteuerung überspringt weitere reguläre Schritte und Wiederholungen des aktuellen Prüflings.
Sie führt dessen Exit-Gruppe sowie die Exit-Gruppen bereits betretener Untergruppen aus.
Bereits laufende parallele Schritte dürfen ihre aktuelle Ausführung abschließen.
Ein normales FAIL ohne vorzeitiges Messende und ein reguläres Aufnahmeende lösen den Wechsel nicht aus.

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

## Rohdatenformat und Anzeige (Ergebnisformat 2)

Neue Messwertverläufe speichern nur tatsächlich erfasste Messwerte, je Kanal als f64le, und einmal die gemeinsamen relativen Messzeitpunkte. Das gilt für Grenzband-, Formel- und Steigungsauswertung. In der Ergebnisdatei und in `tu_waveform_chunks` stehen keine abgeleiteten Steigungen, interpolierten Punkte oder vervielfachten Grenzlinien mehr. Das Punktlimit bleibt maximal 100000 erfasste Punkte je Kanal; ein Sicherheitsabbruch-Messpunkt ist darin enthalten.

Die Quality-Information des ersten Kanals enthält unter `TraceRaw:1:` die unveränderliche Aufnahme-Momentaufnahme: Auswerteversion, vollständige Einstellungen einschließlich Filter, Sicherheitsgrenzen, Zielband, Formeln, Grenzkurven mit Version, Hash und Stützpunkten sowie die gespeicherte Bewertung mit Verletzungen, Abbruch und ausgenommenen Steigungen. Die Momentaufnahme benötigt deshalb weder ein Release noch die aktuelle Projektdatei. Die Zeitachse liegt nur beim ersten Kanal; `sync_group` verbindet die anderen Kanäle derselben Aufnahme damit. Die Quality-Momentaufnahme liegt nur im ersten Chunk.

Beim Laden werden die bisherigen Auswertekurven mit ihren bisherigen Kanalnamen und Einheiten wiederhergestellt. Maßgeblich bleibt die gespeicherte Bewertung. Bei anderer Auswerteversion oder abweichenden Verletzungen erscheint ein Hinweis; die Markierungen stammen weiterhin aus der gespeicherten Bewertung. Alte Dateien der Version 1 und alte Datenbankeinträge mit vollständig gespeicherten Kurven bleiben lesbar.

Spannung und Steigung werden in getrennten Bereichen je Einheit angezeigt. So beeinflussen beispielsweise 200 V/s nicht die Skalierung eines 5-V-Verlaufs. Zeitbereich, Zoom und Cursor sind gemeinsam. Schnellschalter erlauben Messwert, Steigung, Grenzen, Abbruch-/Sicherheitsgrenzen und ausgenommene Steigungen einzeln auszublenden. Die Kurven-Checkboxen bleiben zusätzlich wirksam. Ausgenommene Steigungen erscheinen neutral mit ihren protokollierten Start-/Endwerten und ihrem Grund. Dieselbe Anzeige wird in Ergebnissen, Live- und Werkeransicht verwendet.

### Speicherbedarf

Beispiel: 10000 Messpunkte, ein Kanal, Steigung je Nachbarpaar. Vorher wurden 9999 Steigungen sowie jeweils 9999 untere und obere Grenzen einschließlich dreifacher Zeitachse gespeichert: 479952 Byte reine f64le-Daten. Jetzt sind es 10000 Rohwerte und 10000 Zeitpunkte: 160000 Byte, rund 67 Prozent weniger. Hinzu kommen jeweils Metadaten; die tatsächliche Dateigröße hängt von der Deflate-Kompression und den Messwerten ab. Konstante Grenzen komprimieren besonders gut, daher entspricht die Dateiersparnis nicht direkt der Einsparung der f64le-Daten. Der Regressionstest `Large_recording_reports_physical_file_sizes` misst zusätzlich beide vollständigen Dateien mit einer reproduzierbaren Sinusaufnahme: 167093 Byte vorher, 97477 Byte danach (einschließlich Snapshot und Deflate, rund 42 Prozent weniger).

### Messwerte und Zeiten per SQL lesen (PostgreSQL)

Die bestehende binäre f64le-Speicherung bleibt erhalten. Eine SQL-Hilfsfunktion dekodiert einen endlichen Double-Wert; Rohmesswerte werden vor dem Speichern auf Endlichkeit geprüft. Für andere Datenbankanbieter ist eine entsprechende f64le-Funktion erforderlich.

```sql
CREATE OR REPLACE FUNCTION tu_f64le(b bytea, i integer)
RETURNS double precision LANGUAGE sql IMMUTABLE STRICT AS $$
 WITH p AS (SELECT i*8 AS o), bits AS (
 SELECT ((get_byte(b,o+7)&127)<<4) | (get_byte(b,o+6)>>4) AS e,
        CASE WHEN (get_byte(b,o+7)&128)=0 THEN 1 ELSE -1 END AS s,
        (get_byte(b,o+6)&15)::numeric*281474976710656 +
        get_byte(b,o+5)::numeric*1099511627776 +
        get_byte(b,o+4)::numeric*4294967296 +
        get_byte(b,o+3)::numeric*16777216 +
        get_byte(b,o+2)::numeric*65536 + get_byte(b,o+1)::numeric*256 + get_byte(b,o)::numeric AS m
 FROM p)
 SELECT (s * power(2::numeric, CASE WHEN e=0 THEN -1022 ELSE e-1023 END) *
        (CASE WHEN e=0 THEN 0 ELSE 1 END + m/4503599627370496))::double precision
 FROM bits;
$$;

SELECT w.run_id, w.channel, w.unit, w.sample_offset+n AS sample_index,
       tu_f64le(a.times,n) AS seconds_from_start,
       tu_f64le(w.samples,n) AS measured_value
FROM tu_waveform_chunks w
JOIN tu_waveform_chunks a ON a.run_id=w.run_id AND a.event_seq=w.event_seq
  AND a.sync_group=w.sync_group AND a.part=w.part AND a.times IS NOT NULL
CROSS JOIN LATERAL generate_series(0,w.sample_count-1) n
WHERE w.run_id='GEWUENSCHTE-LAUF-ID' AND w.sync_group LIKE 'TraceRaw:1:%'
ORDER BY w.waveform_no,w.part,n;
```

`start_utc` enthält zusätzlich den absoluten Aufnahmebeginn in UTC-Ticks (100 ns seit 0001-01-01); `seconds_from_start` ist der tatsächliche relative Messzeitpunkt. Durch den Join erhält auch jeder weitere Kanal die einmal gespeicherte Zeitachse.
