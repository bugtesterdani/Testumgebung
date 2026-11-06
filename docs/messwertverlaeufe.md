# Messwertverläufe und Grenzbandprüfung

Die Kategorie **Messwertverläufe** ergänzt die vorhandene Kurvenbibliothek um drei Ablaufbausteine. Es werden zeitabhängige Verläufe mit X-Achse in **Sekunden** unterstützt. Andere numerische X-Achsen sind noch nicht Bestandteil dieser Umsetzung.

## Grenzband vorbereiten

Unter **Kurven** eine Kurve mit zwei Kanälen **Untergrenze** und **Obergrenze** definieren. Beide Kanäle brauchen die Einheit der späteren Messgröße, beispielsweise `V`. Möglich sind der vorhandene numerische Generator/Formeleditor oder der Import von Stützpunkten aus CSV. Eine passende Vorlage ist [grenzband-spannung.csv](beispiele/grenzband-spannung.csv). Beim Import die erste Spalte als Zeit in Sekunden und die beiden anderen Spalten als die genannten Kanäle zuordnen.

Die Zeitpunkte müssen nichtnegativ, endlich und streng steigend sein. Untergrenze darf an keinem Stützpunkt über Obergrenze liegen. Mindestens zwei, höchstens 10.000 Grenzstützpunkte pro Kurve sind erlaubt. Kurven mit Qualitätshinweis zuerst bereinigen. Eine Version speichern und ihre Kennung aus der Bibliothek für den Ablauf übernehmen, beispielsweise `meine-grenze@1`. Es werden keine Einheiten automatisch umgerechnet.

Grenzkurven sind gewöhnliche versionierte Projektkurven: Sie werden mit dem Projekt gespeichert, ins Freigabepaket aufgenommen und gehasht. Die Anzeige „Verwendet in“ berücksichtigt Verlaufstarts. Geänderte Grenzkurven außerhalb eines Prüflingstests verhindern einen Versionswechsel während der Prüfsitzung.

## Ablauf erstellen

Alle drei Bausteine in **denselben Prüflingstest** legen, damit Daten und Ergebnis zu dessen Seriennummer gehören. Eine Aufnahme außerhalb eines Prüflingstests ist ein eigener Laufkontext und kann nicht innerhalb eines Prüflingstests fortgeschrieben werden. Aufnahmen unterschiedlicher Seriennummern und paralleler Prüfläufe bleiben getrennt.

1. **Messwertverlauf beginnen:** Aufnahmekennung vergeben, z. B. `Verlauf`. Kanalnamen, Einheiten und Grenzkurven kommasepariert in gleicher Reihenfolge eintragen. Beispiel: `Spannung,Strom`, `V,A`, `spannungsband@1,stromband@1`. Jeder Messkanal hat sein eigenes Grenzband. Ohne explizite Version wird die neueste Kurve der laufenden Projektfassung verwendet; die tatsächliche Kennung und ihr Inhaltshash werden im Ergebnis festgehalten.
2. **Messpunkt ergänzen:** Innerhalb einer Wiederholung ausführen. Datenquelle **Geräte** liest die kommaseparierten abstrakten Gerätenamen unter Beachtung der bestehenden Ressourcen-/Sperrprüfung. Die gemeldeten Geräteeinheiten müssen passen. Datenquelle **Ausdrücke** wertet semikolongetrennte numerische Ausdrücke/Variablen aus, z. B. `U_mess;I_mess`. Je Punkt werden alle Kanäle derselben Aufnahme geliefert. Bei Ausdrücken ist der Autor für die richtig deklarierte Einheit verantwortlich.
3. **Zeit:** Leer bedeutet Empfangszeit relativ zum Aufnahmebeginn. Alternativ einen Ausdruck in Sekunden angeben, etwa die Variable `Messzeit`. Zwischen den Punkten muss Zeit vergehen oder die explizite Zeit steigen. Geräte werden nacheinander gelesen; der gemeinsame Empfangszeitpunkt verspricht keine hardwareseitig synchrone Abtastung.
4. **Verlauf beenden und bewerten:** Beendet die Aufnahme und liefert PASS, FAIL oder ERROR. Optional die Auswertungsdetails in eine vorher deklarierte Stringvariable über `ErgebnisZiel` schreiben lassen.

Das **Punktlimit** liegt zwischen 2 und 100.000 Punkten je Kanal; maximal acht Kanäle pro Aufnahme und acht aktive Aufnahmen je Lauf. **Maximale Messlücke** ist der größte erlaubte Abstand aufeinanderfolgender Punkte. Ein passendes Zeitbudget inklusive Gerätelatenzen wählen. **Mindestdauer** legt die erforderliche Zeit zwischen erstem und letztem Messpunkt fest. Standard `0` prüft nur den tatsächlich aufgezeichneten Bereich; wenn eine Mindestprüfdauer erforderlich ist, diese ausdrücklich setzen. Das Grenzband muss den aufgezeichneten Zeitbereich vollständig abdecken. Eine punktweise Geräteabfrage ist keine hochfrequente DAQ-Erfassung; dafür bleibt die vorhandene Kurvenaufnahme verfügbar.

## Bewertungsregeln

Messung und Grenzen werden stückweise linear verbunden. Geprüft werden **alle Messzeiten und alle dazwischenliegenden Grenzstützpunkte**. Damit werden auch Verletzungen zwischen zwei gemessenen Punkten erkannt. Berühren der Grenzen ist standardmäßig erlaubt und kann am Startbaustein ausgeschaltet werden.

Außerhalb des definierten Grenzbereichs erfolgt keine Extrapolation. Nichtendliche Werte, falsche Einheiten, fehlende Kanäle, doppelte/rückläufige Zeiten, zu große Messlücken und überschrittene Punktlimits machen die Aufnahme ungültig. Nach einem Messpunktfehler kann dieselbe Aufnahme nicht nachträglich PASS werden. Weniger als zwei Punkte oder unterschrittene Mindestdauer ergeben ERROR. Abgebrochene und nicht ausdrücklich beendete Aufnahmen werden beim Kontextende als unvollständig mit ERROR aufgezeichnet; ein Werkerabbruch bleibt zusätzlich ABORT für den Lauf. Eine bereits aktive Kennung muss beendet werden, bevor sie neu begonnen werden kann.

## Diagramm und gespeicherte Daten

Bereits der erste Messwert erscheint während der Aufnahme als farbiger Punkt. Weitere tatsächlich gemessene Werte werden an ihren jeweiligen Messzeiten ergänzt und durch Linien verbunden; unregelmäßige Zeitabstände bleiben erkennbar. Die beiden ersten Punkte erscheinen sofort, danach wird die Werkerkurve zur Entlastung der Oberfläche höchstens etwa viermal je Sekunde aktualisiert; Abschluss und FAIL-Abbruch zeigen immer den letzten Stand. Die Kurve ist sowohl in der einzelnen Werkeransicht als auch getrennt je Seite in der Doppelplatzansicht sichtbar. Bei mehr als 1.000 sichtbaren Messpunkten je Kanal reduziert die Anzeige die Punktmarkierungen einschließlich erstem und letztem Punkt; Messdaten und Bewertung bleiben vollständig. Zusätzliche interpolierte Stützpunkte erhalten keine Messpunktmarkierung. Das ist eine Anzeige, keine vorzeitige PASS-Bewertung. Beim Beenden erscheinen je Messkanal Messung, Untergrenze und Obergrenze in der vorhandenen Kurvenanzeige mit Cursor, Zoom und Kanalauswahl. Rote gestrichelte Linien markieren verletzte Prüfstützpunkte. Die Anzeige begrenzt die Anzahl dieser Markierungen auf 1.000; die Bewertung verwendet sämtliche Daten.

Die Kurven werden über die vorhandenen Kurven-Speicheroptionen zu Ergebnisdatei und Datenbank geführt. Sind diese Speicherziele ausgeschaltet, werden auch die neuen Kurven dort nicht abgelegt. Für das Wiederöffnen die vorhandene Ergebnisnachschau nutzen.

Für gemeinsame Darstellung und exakte Bewertung werden zusätzliche linear interpolierte Stützpunkte eingefügt. Die Originalmesszeiten bleiben in den Metadaten erhalten; an diesen Zeiten stehen die unveränderten gemessenen Werte. Je Messkanal enthält `Waveform.Quality` nach dem Präfix `Grenzband:` ein JSON mit Grenzkurvenkennung, Inhaltshash, Originalmesszeiten, Vollständigkeit, Verletzungsanzahl, maximaler Abweichung und Details je Verletzung (Zeit, Wert, Unter-/Obergrenze, Abweichung). Diese Metadaten bleiben in der nativen Ergebnisdatei erhalten. `ErgebnisZiel` enthält dieselben kanalweisen Auswertungsdaten als JSON.

Bei Abschlussgruppen nach Werkerabbruch führt auch ein ausdrücklich aufgerufener Bewertungsbaustein nicht zu einer vollständigen PASS-Aufnahme. Zum Entwerfen einzelner Schritte ausgeführte Debug-Aufnahmen gehören zum separaten Debugkontext und werden bei dessen Ende verworfen.

## Vorzeitiger FAIL-Abschluss

Im Baustein **Messwertverlauf beginnen** optional **Prüfablauf bei zusätzlicher Abweichung sofort mit FAIL beenden** einschalten und **Zusätzliche Abweichung [% vom Betrag des Grenzwerts]** setzen (Standard 10 %, Funktion standardmäßig aus). Die Aufnahme prüft dann jeden neuen Punkt, auch wenn das Live-Diagramm gerade gedrosselt aktualisiert wird. Ein beliebiger Kanal genügt zur Auslösung.

Bei Prozentsatz `p` sind die zusätzlichen Abbruchgrenzen `Untergrenze − |Untergrenze| × p/100` und `Obergrenze + |Obergrenze| × p/100`. Beispiel: 4–6 V und 10 % ergeben 3,6–6,6 V. Bei −6 bis −4 V ergeben sich −6,6 bis −3,6 V. Ein Grenzwert von 0 hat keinen prozentualen Zuschlag. Nur striktes Überschreiten löst aus; numerisches Rundungsrauschen wird toleriert. 0 % beendet bei jeder echten Grenzüberschreitung. Der normale Grenzbandtest bleibt unverändert: Auch eine kleinere Verletzung ergibt bei abschließender Bewertung FAIL, beendet aber nicht sofort den Ablauf.

Die Überwachung berücksichtigt den zuletzt ergänzten linearen Abschnitt einschließlich Grenzstützpunkten und Nulldurchgängen der Grenzen. Eine Verletzung zwischen Messpunkten kann erst mit Empfang des nächsten Punkts erkannt werden. Es ist keine kontinuierliche hardwareseitige Schutzüberwachung.

Bei Auslösung wird die Aufnahme sofort beendet und der reguläre Ablauf dieses Prüflaufs einschließlich Wiederholungen/parallel laufender regulärer Zweige gestoppt. Exit-Gruppen und Finalization werden zum Aufräumen weiter ausgeführt. Der andere Prüfplatz hat einen unabhängigen Lauf. Das Ergebnis ist **FAIL**, kein Werker-ABORT; ein zusätzlicher Fehler in der Aufräumfolge kann weiterhin ERROR ergeben.

Die bis dahin erfassten Daten inklusive auslösendem Punkt werden einmal beim Messpunktschritt gespeichert. Die Metadaten enthalten unter `EarlyStop` Prozentsatz, Kanal, Zeit, Messwert und ursprüngliche Grenzen. `Complete` bleibt false, da die geplante Aufnahme vorzeitig endete. Ein abschließender Bewertungsbaustein in der Exit-Gruppe liefert ebenfalls FAIL und kann die Details in seine Ergebnisvariable schreiben, speichert aber keine zweite Kopie der Kurven. Weitere offene Aufnahmen werden mit ihren vorhandenen Daten als unvollständig beim automatischen FAIL-Abschluss gesichert.
