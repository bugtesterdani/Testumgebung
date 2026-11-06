# Beispiel zu PR #76: Live-Messkurve mit Grenzband

**Projektdatei:** [grenzband-demo.tproj.json](grenzband-demo.tproj.json)

Das Beispiel benötigt einen Build aus **PR #76** oder eine spätere Version mit den Messwertverlauf-Bausteinen. Alle Werte entstehen im Projekt; zusätzliche Geräte, Skripte und Bibliotheken werden nicht benötigt.

## Starten

1. Eine laufende Prüfung beziehungsweise aktive Auftragsauswahl vorher beenden. Als Entwickler anmelden, den **Ablaufeditor** öffnen und über **Projekt öffnen** die Projektdatei laden.
2. **Simulation starten** wählen und anschließend links zu **Prüfen** wechseln. Für diese lokale Demonstration ist keine Release-Freigabe erforderlich.
3. Die Demo-Seriennummer `DEMO-001` übernehmen oder eine eigene eingeben.
4. Einen der vier Fälle auswählen. Während der Aufnahme die Live-Kurve in der Werkeransicht beobachten.
5. Am letzten Hinweis Kurve und Protokoll ansehen und bestätigen. Das Projekt führt genau einen Prüfling durch; zum nächsten Vergleich erneut starten.

## Fälle und erwartete Ergebnisse

Das eingebettete Grenzband `demo-spannungsband@1` hat die Stützpunkte **0 s und 5 s**, jeweils Untergrenze **4 V** und Obergrenze **6 V**. Es deckt den gesamten Zeitraum ab. Die bereits eingebetteten Grenzen liegen zusätzlich als lesbare [CSV-Vorlage](grenzband-demo-grenzen.csv) bei; ein erneuter Import ist für den Start nicht nötig. Zusatzabweichung ist **10 %**: vorzeitiges Aufnahmeende unter **3,6 V** beziehungsweise über **6,6 V**.

| Auswahl | Daten | Aufnahme | Erwartung |
| --- | --- | --- | --- |
| PASS | 5 V konstant, alle 0,5 s | volle 5 s, 11 Messpunkte | PASS |
| FAIL | 3,999 V konstant, alle 0,5 s | volle 5 s, 11 Messpunkte | FAIL, auch wenn die Zusatzgrenze nicht erreicht wird |
| Fruehes Ende | 3,599 V | endet sofort beim ersten Messpunkt | FAIL; der folgende Prüfschritt wird trotzdem ausgeführt |
| Kurve | 5 / 5,4 / 4,7 / 5,8 / 5,1 / 5,4 V bei 0 / 1 / 2 / 3 / 4 / 5 s | sechs selbst gesteuerte Punkte über 5 s | PASS mit sichtbarem Kurvenverlauf |

Die ersten drei Fälle verwenden **Messwertverlauf aufnehmen** mit einstellbarer Dauer und Intervall. Der vierte Fall zeigt die Alternative mit **Messwertverlauf beginnen → Messpunkt ergänzen → Verlauf beenden und bewerten**. Die kurzen Wartebausteine gehören ausschließlich zu dieser manuellen Demonstration.

## Was im Editor zu sehen ist

- Gruppe **Grenzband-Demoprüfling**: Variablen `Kennung`, `Szenario`, `Messwert`, `Details` und `NaechsterSchrittErreicht`.
- Bedingte Gruppen setzen für die drei zeitgesteuerten Fälle den Wert. Die Aufnahme bezieht ihn über den Ausdruck `Messwert`.
- Unter **Kurven** liegt die versionierte Unter-/Obergrenze. Wird die Messdauer erhöht, muss diese Kurve mindestens bis zur neuen Endzeit reichen.
- **Folgeschritt – auch nach frühem FAIL** setzt die Bool-Variable auf `true`. Der abschließende Werkerhinweis bestätigt, dass der Ablauf weitergegangen ist. Ein entstandenes FAIL bleibt das Gesamtergebnis.
- `Details` enthält die JSON-Auswertung. Die Live-Anzeige unterscheidet Messpunkte, Verbindungslinie und Grenzlinien. Bei frühem Ende gibt es nur einen Punkt; dies ist beabsichtigt.

Die Messkurve wird dem Demoprüfling zugeordnet. Dauerhafte Ablage erfolgt gemäß den vorhandenen Kurven-Speichereinstellungen; das Beispiel ändert diese Einstellungen nicht.

**Anpassung an echte Geräte:** Im zeitgesteuerten Aufnahmebaustein Quelle **Geräte** und die abstrakten Gerätenamen des eigenen Prüfplatzes wählen. Kanalnamen, Einheiten und Grenzkurven entsprechend anpassen. Erst danach reale Hardware verwenden.

Weitere Einzelheiten: [Messwertverläufe und Grenzbandprüfung](../messwertverlaeufe.md).
