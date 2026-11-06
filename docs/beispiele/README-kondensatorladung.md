# Kondensatorladung mit Simulationsgeräten

Dieses Beispiel zu PR #76 läuft ohne angeschlossene Hardware. Es verwendet vorhandene Simulationsressourcen und liest die Spannung über den Gerätezugriff des Messwertverlaufs ein.

## Öffnen und ausprobieren

1. Einen Build aus PR #76 oder neuer mit den Messwertverlauf-Bausteinen verwenden.
2. `kondensatorladung-demo.tproj.json` herunterladen und im **Ablaufeditor → Projekt öffnen** laden. Einen laufenden Prüflauf vorher beenden.
3. **Simulation starten**, anschließend zur Ansicht **Prüfen** wechseln.
4. Seriennummer eingeben, beispielsweise `CAP-001`.
5. Die Livekurve beobachten. Nach acht Sekunden wird das simulierte Netzteil abgeschaltet. Das Bestätigungsfenster bleibt zur Betrachtung der Kurve geöffnet.
6. Bestätigen. Erwartetes Gesamtergebnis: **PASS**. Für einen weiteren Versuch die Simulation neu starten.

Es wird ein einzelner Prüfling geprüft. Eine Freigabe ist für diese lokale Simulation nicht erforderlich. Die vorhandenen Einstellungen bestimmen die Speicherung der Ergebnisse; das Beispiel verändert diese Einstellungen nicht.

## Simulierte Geräte

| Ressource | Aufgabe | Vorgabe |
|---|---|---|
| `Netzteil_Spannung` | Spannungslimit des Netzteils | 5 V |
| `Netzteil_Strom` | Strombegrenzung | 0,1 A |
| `Netzteil_Ausgang` | Ausgang ein/aus | 0 → 1 → 0 |
| `DMM_Spannung` | Separater Spannungsmesskanal am Kondensator | 0 bis 5 V |

Die Ressourcen sind im Projekt enthalten. Alle Anfangswerte sind bewusst **0**, ohne Rauschen oder Sinusgenerator: So können die Modellschritte die simulierten Kanalwerte schreiben und anschließend über den Gerätezugriff lesen.

## Ablauf und erwartete Kurve

Der Kondensator hat **0,1 F (100.000 µF)** und startet entladen. Das Netzteil arbeitet zunächst in Strombegrenzung. Das ideale Modell lautet:

`U(t) = min(5 V, 0,1 A × t / 0,1 F)`

| Zeit | Spannung |
|---|---|
| 0 s | 0 V |
| 1 s | 1 V |
| 2 s | 2 V |
| 3 s | 3 V |
| 4 s | 4 V |
| 5 s | 5 V |
| 6 bis 8 s | 5 V |

Bei idealer Konstantstromladung steigt die Spannung linear. Nach Erreichen des Spannungslimits bleibt sie konstant. Eine exponentielle RC-Ladekurve würde ein anderes Modell, etwa eine Spannungsquelle mit Serienwiderstand, erfordern.

Die Aufzeichnung wird zuerst vorbereitet. Unmittelbar danach schaltet der Ablauf das Netzteil ein; dieser Punkt ist die Modellzeit **t = 0**. Der erste Messpunkt folgt ohne Warteblock. Danach werden alle **200 ms** Punkte aufgenommen, insgesamt **41 Punkte einschließlich 0 und 8 s**. Die manuell gesetzte Zeitachse bezeichnet die Modellzeit. Tatsächliche Ablaufzeiten können durch Bedienoberfläche oder Debugger länger werden; dies ist kein Beispiel für eine hardwareseitig synchronisierte Erfassung.

## Bewertung

Das eingebettete Grenzband folgt der erwarteten Ladekurve mit **±0,15 V** und deckt den vollständigen Zeitraum 0–8 s ab. Bereits eine Verletzung ergibt FAIL, auch wenn spätere Werte wieder innerhalb liegen. Für dieses Beispiel ist ein vorzeitiges Aufnahmeende ausgeschaltet: Beide Testfälle werden über die vollständigen acht Sekunden gemessen.

Die Grenzstützpunkte sind zusätzlich in `kondensatorladung-grenzen.csv` enthalten. Ein Import ist nicht erforderlich, die Kurve ist bereits eingebettet.

Zum absichtlichen Erzeugen eines Fehlers im Ablaufeditor in den Variablen der Gruppe **Kondensator laden und Spannung messen** die Variable `Kapazitaet` von `0.1` auf `0.2` F ändern. Die Sollkurve unverändert lassen. Bei 5 s werden dann nur 2,5 V erreicht, bei 8 s 4 V: erwartetes Ergebnis **FAIL**. Für den Normalfall wieder auf `0.1` zurückstellen. Änderungen an `Kapazitaet` beeinflussen das Modell; Änderungen an Dauer oder Intervall erfordern auch passende Wiederholungszahl, Zeitberechnung und Grenzkurven.

## Wie die Simulation funktioniert

Die mit **SIMULATION** bezeichneten Ablaufpunkte bilden den Kondensator nach: Sie lesen die Netzteilvorgaben, berechnen die Modellspannung, begrenzen sie auf die Sollspannung und schreiben den Wert in den simulierten DMM-Kanal. Der nächste Punkt **Spannung vom DMM lesen und live aufzeichnen** verwendet anschließend tatsächlich `Quelle = Geräte` und `Ressourcen = DMM_Spannung`. Messung und Grenzbewertung laufen somit über den regulären Gerätepfad.

Dies ist ein explizites, idealisiertes Modell im Beispielablauf, kein neuer autonomer Kondensator-Gerätetreiber. ESR, Leckstrom, Regelverhalten, Messrauschen und Entladung sind nicht modelliert. Das Ausschalten des Netzteils entlädt einen echten Kondensator nicht.

**Die Modellschritte nicht mit realen Geräten ausführen.** Insbesondere das Schreiben auf `DMM_Spannung` ist ausschließlich für diese Simulation gedacht. Für eine reale Prüfung diese Schritte ersetzen: Netzteil über seinen Treiber konfigurieren, Messgerät ausschließlich lesen und Zeitbezug/Trigger passend zur Ladegeschwindigkeit wählen. Die programmierte Exit-Gruppe schaltet den simulierten Ausgang auch beim Werkerabbruch aus; sie ersetzt keine elektrische Schutz- oder Entladeschaltung.

## Automatische Prüfung

Die mitgelieferte Projektdatei wird in zwei Tests ausgeführt: nominaler Kondensator → PASS, doppelte Kapazität → FAIL. Geprüft werden außerdem 41 Messpunkte, acht Sekunden Modelllaufzeit, gespeicherte Kurvendaten und der ausgeschaltete Netzteilausgang am Ende.
