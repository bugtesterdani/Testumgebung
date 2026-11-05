# Werkerabbruch und Gesamtstatus ABORT

Bricht der Werker einen begonnenen Prüflingstest ab, lautet dessen Gesamtstatus **ABORT**. Das gilt auch dann, wenn einzelne bereits ausgeführte Schritte PASS, FAIL oder ERROR geliefert haben. Exit-/Aufräumfolgen laufen weiterhin nach den vorhandenen Abbruchregeln.

Die Werkeransicht, Ergebnisansicht, das Abschlussprotokoll und Einzelberichte/CSV zeigen ABORT. Bereits abgeschlossene Prüflinge eines laufenden Fertigungsauftrags werden nicht rückwirkend geändert. Noch nicht begonnene Prüflingstests erhalten dadurch kein erfundenes Ergebnis.

Abgebrochene Läufe zählen in der Auswertung ausschließlich zur Kategorie ABORT, nicht zusätzlich zu PASS/FAIL/ERROR/NRESULT. Die Ausbeute bleibt der Anteil der PASS-Läufe an allen ausgewählten Läufen. Vorhandene Messwerte und Schrittfehler bleiben zur Diagnose und Messwertauswertung erhalten.

## Speicherung und ältere Ergebnisse

Die bestehende Trennung zwischen fachlicher Bewertung und Werkerabbruch bleibt erhalten. In Ergebnisdateien steht zusätzlich der explizite Gesamtstatus `status: ABORT`; die bisherige `rating`-Bewertung und das Abbruchkennzeichen `werror` werden nicht gelöscht. JSON-Serialisierungen des Laufstatus enthalten `Outcome: ABORT`.

In der Datenbank wird weiterhin das vorhandene Abbruchkennzeichen `tu_runs.werror` gespeichert. Beim Laden bildet die Anwendung daraus den Gesamtstatus ABORT. Es ist kein Schema-Upgrade erforderlich. Direkte SQL-Auswertungen können den Gesamtstatus so bilden:

```sql
SELECT run_id,
       CASE WHEN werror <> 0 THEN 'ABORT' ELSE rating END AS status,
       rating AS bewertung_vor_abbruch
FROM tu_runs;
```

Auch ältere Ergebnisdateien und Datenbankeinträge mit `werror` werden als ABORT angezeigt. Ihre vorhandenen Messwerte und Dateiprüfsummen werden dadurch nicht umgeschrieben. Ein wiederhergestellter Lauf nach Software-/Rechnerabsturz wird weiterhin als ERROR behandelt, wenn kein Werkerabbruch nachgewiesen ist.

## Kommandozeilenszenarien

Für den Lauf und die erwarteten Prüflingsergebnisse kann `ABORT` angegeben werden. Bestehende Erwartungen wie `PASS + WERROR` bleiben als zusätzliche Prüfung der Bewertung vor dem Abbruch unterstützt. ABORT ist ein Gesamtstatus, keine neue Schrittbewertung; die Rangfolge NRESULT < PASS < FAIL < ERROR bleibt erhalten.
