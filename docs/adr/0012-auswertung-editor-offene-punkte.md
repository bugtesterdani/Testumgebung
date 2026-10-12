# ADR 0012 – Auswertung & Berichte, Editor-Komfort, offene Punkte aus Abschnitt 15

Status: angenommen (Oktober 2026).

## Auswertung & Berichte

* **Datenquelle Ergebnisdateien** (`.tres`): ohne Datenbank verfügbar, lesbar ohne installierte Testversion. Kurven werden für
  die Statistik nicht im Speicher gehalten. Beschädigte Dateien werden gemeldet, nicht übersprungen ohne Hinweis.
* **Kennzahlen:** Ausbeute je Lauf, First Pass Yield (erster Lauf je Kennung PASS), Endausbeute (letzter Lauf PASS),
  Wiederholprüfungen, Werkerabbrüche, mittlere Dauer; Fehler-Pareto je Schritt (FAIL/ERROR, häufigste Meldung);
  Messwertstatistik mit Cp/Cpk gegen die zuletzt gültigen Grenzwerte (σ als Stichprobenstandardabweichung), Histogramm,
  Tages- und Prüfplatzübersicht. Entwicklungsläufe standardmäßig ausgeschlossen.
* **PDF ohne Fremdbibliothek** (`PdfDocument`, PDF 1.4, Standardschriften Helvetica/Courier mit WinAnsi): keine Lizenz- oder
  NuGet-Abhängigkeit, genügt für Tabellen, Kopf-/Fußzeilen und Kurvenbilder. Zeichen außerhalb WinAnsi werden ersetzt (σ → s).
* **CSV für Excel (deutsch):** Semikolon, Dezimalkomma, UTF-8 mit BOM.
* Prüfbericht je Prüfling (Seite „Ergebnisse“) und Auswertung (Seite „Auswertung“, Rechte Entwickeln oder Freigeben).

## Editor-Komfort

* **Rückgängig/Wiederholen** über Projektstände (Projekt-JSON nach jeder Änderung, höchstens 100 Schritte). Tippfolgen am
  selben Element innerhalb von 1,5 s bilden einen Schritt; Struktur­änderungen (Einfügen, Löschen, Verschieben, Einfügen aus der
  Zwischenablage) immer eigene Schritte. Laden/Neu leert den Verlauf. Einfacher und robuster als einzelne Befehlsobjekte, weil
  alle Eigenschaftsseiten ohnehin `MarkChanged` melden.
* **Kopieren/Ausschneiden/Einfügen/Duplizieren** von Schritten und Gruppen samt Inhalt und Exit-Gruppe; beim Einfügen neue IDs
  (Haltepunkte, Ergebnisse und Releasevergleiche hängen an IDs). Einfügeposition wie bei der Palette.
* **Suche** über Namen, Beschreibung, Bausteintyp, Parameterwerte (Ressourcen, Grenzwerte, Code), Variablen, Bedingungen und
  Reservierungen; F3/Umschalt+F3 springt und klappt Gruppen auf.
* **Versionsvergleich** (Seite Releases): Knoten über stabile IDs – neu/entfernt/geändert/verschoben, Parameter alt → neu,
  Gruppeneinstellungen, Variablen, CodeBlocks, Gruppenbausteine, Kurvenversionen, Werkeransicht, Simulationsprofil.

## Offene Punkte aus Abschnitt 15 – Entscheidungen

| Punkt | Entscheidung |
|---|---|
| Zeitbasierte Wiederholung | Standard: Frist zwischen Iterationen (Vorschlag). Wählbar je Gruppe: Frist auch zwischen den Schritten einer Iteration – restliche Schritte werden übersprungen (Hinweis im Protokoll). Ein laufender Schritt wird nicht unterbrochen; Exit-Gruppen laufen. |
| Step-Into/Step-Over | Einzelschritt (F11): nächstes fertiges Element jeder Ebene; Prozedurschritt (F10): nächstes Element derselben oder einer äußeren Ebene; bis Gruppenende (Umschalt+F11). Angehalten wird – wie bei Haltepunkten – nach der Ausführung, vor der Bewertungsrückgabe. |
| Abmelden im pausierten Test | Lauf bleibt pausiert, wird weder fortgesetzt noch übernimmt er Änderungen; Hinweis am Prüfplatz und Audit-Eintrag; Werkerabbruch bleibt möglich. Gilt auch für Inaktivitäts-Abmeldung. |
| Rechte lokal oder zentral | Wählbar: Lokal (Liste je Prüfplatz, Standard), AppRollen (Entra-App-Rollen, Claim „roles“), Beide. Rollen nur aus bestätigten Entra-Anmeldungen. |
| Verschachtelte Exit-Gruppen bei erneutem Abbruch | unverändert (E08): erneuter Abbruch beendet nur die innerste laufende Exit-Gruppe. |
| Lock-Erwerb vs. Variableninitialisierung | unverändert nach Vorschlag: Scope initialisieren, Bedingung prüfen, dann erwerben. |
| Dateispeicherfehler, Stromverlust, unvollständige Runs | Wiederherstellungsordner, Outbox, Zwischenaufzeichnung (ADR 0010). |
| Formate für Import und Berichte | Import: CSV, COMTRADE, Excel, `.tres` (ADR 0007); Berichte: PDF und CSV (oben). |
