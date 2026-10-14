/**
 * @file
 * @brief Verbindet gespeicherte Ergebnisse und Statistik mit der Ausgabe von Prüf- und Auswertungsberichten.
 */

using System.Globalization;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Reports;

/// <summary>PDF-Berichte: Prüfbericht je Prüfling und Auswertung über viele Prüfläufe.</summary>
public static class Reports
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static PdfColor? ColorOf(ResultRating? rating) => rating switch
    {
        ResultRating.Pass => PdfColor.Pass,
        ResultRating.Fail => PdfColor.Fail,
        ResultRating.Error => PdfColor.Warn,
        _ => null,
    };

    private static string Num(double? v) => v is { } d ? d.ToString("0.####", De) : "–";

    private static string ValueText(object? v) => v switch
    {
        null => "–",
        double d => d.ToString("0.#####", De),
        bool b => b ? "ja" : "nein",
        DateTime t => t.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", De),
        _ => Convert.ToString(v, De) ?? "",
    };

    private static string Limits(double? low, double? high) => low is null && high is null ? "–" : $"{Num(low)} … {Num(high)}";

    /// <summary>Prüfbericht eines Prüflingstests (aus der Ergebnisdatei – ohne installierte Testversion).</summary>
    public static PdfDocument TestReport(ResultRun run, string? sourceFile = null, string? company = null)
    {
        var duration = run.Ended - run.Started;
        var writer = new PdfReportWriter($"Prüfbericht {run.DutId}", $"{company ?? "Testumgebung"} · Prüfbericht · {run.TestName} v{run.TestVersion} · {run.DutId}");
        void WriteHeader()
        {
            writer.Title("Prüfbericht", $"Prüfling {(string.IsNullOrWhiteSpace(run.DutId) ? "ohne Kennung" : run.DutId)}");
            writer.KeyValues(
            [
                ("Ergebnis", run.Status.Rating.ToDisplay() + (run.OperatorAbort ? " (Werkerabbruch – WERROR)" : ""), ColorOf(run.Rating) ?? PdfColor.Black),
            ("Prüfling", string.IsNullOrWhiteSpace(run.DutId) ? "ohne Kennung" : run.DutId, null),
            ("Prüfvorschrift", $"{run.TestName} v{run.TestVersion}", null),
            ("Prüfplatz", run.Station, null),
            ("Beginn / Ende", $"{run.Started.LocalDateTime:dd.MM.yyyy HH:mm:ss} – {run.Ended.LocalDateTime:HH:mm:ss} ({duration.TotalSeconds.ToString("0.0", De)} s)", null),
            ("Auftrag", OrderOf(run) ?? "–", null),
            ("Lauf-ID", run.RunId, null),
            ("Art", run.IsDevelopmentRun ? "Entwicklungslauf (nicht für Freigabezwecke)" : "Produktionsprüfung", run.IsDevelopmentRun ? PdfColor.Warn : null),
            ("Quelle", (sourceFile is null ? "" : Path.GetFileName(sourceFile) + " · ") + (string.IsNullOrEmpty(run.FilteredFor) ? "ungefiltert" : "gefiltert für " + run.FilteredFor), null),
        ]);

        }

        void WriteObservations()
        {
            var measurements = run.Events.Where(e => e.Kind == ResultEventKind.Step && !e.Cancelled && (e.LowLimit is not null || e.HighLimit is not null || e.Unit is { Length: > 0 }))
                .ToList();
            if (measurements.Count > 0)
            {
                writer.Heading("Messwerte");
                writer.Table([("Messung", 3.2), ("Wert", 1.4), ("Einheit", 0.8), ("Grenzwerte", 1.8), ("Ergebnis", 1)],
                    measurements.Select(e => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (ResultAnalysis.NameOf(run, e) + (e.Iteration > 1 ? $" [{e.Iteration}]" : ""), null),
                    (ValueText(e.Value), null), (e.Unit ?? "", null), (Limits(e.LowLimit, e.HighLimit), null),
                    (e.Rating?.ToDisplay() ?? "–", ColorOf(e.Rating)),
                    ]));
            }

            var failures = run.Events.Where(e => e.Rating is ResultRating.Fail or ResultRating.Error && e.Kind is ResultEventKind.Step or ResultEventKind.Diagnostic).ToList();
            if (failures.Count > 0)
            {
                writer.Heading("Abweichungen");
                foreach (var f in failures)
                    writer.Paragraph($"{f.Rating?.ToDisplay()} · {(f.NodeId is null ? "Ablauf" : ResultAnalysis.NameOf(run, f))}: {f.Message ?? ValueText(f.Value)}", 9, ColorOf(f.Rating));
            }

            var answers = run.Events.Where(e => e.Kind == ResultEventKind.OperatorAnswer).ToList();
            if (answers.Count > 0)
            {
                writer.Heading("Werkereingaben");
                writer.Table([("Zeit", 1.2), ("Schritt", 3), ("Eingabe", 3)],
                    answers.Select(e => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (e.Time.LocalDateTime.ToString("HH:mm:ss", De), null), (ResultAnalysis.NameOf(run, e), null), (ValueText(e.Value), null),
                    ]));
            }

        }

        void WriteGroups()
        {
            var groups = run.Events.Where(e => e.Kind is ResultEventKind.Group or ResultEventKind.GroupNotEntered).ToList();
            if (groups.Count > 0)
            {
                writer.Heading("Ablauf");
                writer.Table([("Gruppe", 4), ("Durchläufe", 1), ("Ergebnis", 1.2), ("Hinweis", 2.5)],
                    groups.Select(e => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (ResultAnalysis.NameOf(run, e), null),
                    (e.Kind == ResultEventKind.Group ? ValueText(e.Value) : "0", null),
                    (e.Kind == ResultEventKind.Group ? e.Rating?.ToDisplay() ?? "–" : "nicht betreten", ColorOf(e.Rating)),
                    (e.Message ?? "", null),
                    ]));
            }

        }

        WriteHeader();

        WriteObservations();

        WriteGroups();

        WriteWaveforms();

        void WriteWaveforms()
        {
            foreach (var group in run.Waveforms.GroupBy(w => w.EventSequence))
            {
                var ev = run.Events.FirstOrDefault(e => e.Sequence == group.Key);
                var title = ev is null ? "Kurven" : $"Kurven: {ResultAnalysis.NameOf(run, ev)}";
                var series = group.Select(w =>
                {
                    var wave = w.Data;
                    var indices = new List<int>();
                    // Pro PDF-Spalte Minimum/Maximum erhalten; keine Vollkopie großer Aufnahmen.
                    var width = Math.Max(1, (wave.Count + 599) / 600);
                    for (var a = 0; a < wave.Count; a += width)
                    {
                        int min = a, max = a;
                        for (var i = a; i < Math.Min(wave.Count, a + width); i++)
                        {
                            if (wave.Values[i] < wave.Values[min]) min = i;
                            if (wave.Values[i] > wave.Values[max]) max = i;
                        }
                        indices.Add(Math.Min(min, max));
                        if (min != max) indices.Add(Math.Max(min, max));
                    }
                    return (wave.Channel, indices.Select(i => wave.TimeOf(i) * 1000).ToArray(), indices.Select(i => wave.Values[i]).ToArray());
                }).ToList();
                writer.Plot(title, series, "ms", group.First().Data.Unit);
                writer.Paragraph(string.Join(" · ", group.Select(w => $"{w.Data.Channel}: Effektivwert {w.Data.Rms().ToString("0.###", De)} {w.Data.Unit}, {w.Data.Count} Samples")), 8, PdfColor.Gray);
            }

        }

        var steps = run.Events.Where(e => e.Kind == ResultEventKind.Step).ToList();
        writer.Heading("Alle Schritte");
        writer.Table([("Zeit", 1.1), ("Schritt", 3.4), ("Wert", 2), ("Ergebnis", 1), ("Meldung", 3)],
            steps.Select(e => (IReadOnlyList<(string, PdfColor?)>)
            [
                (e.Time.LocalDateTime.ToString("HH:mm:ss", De), null),
                (ResultAnalysis.NameOf(run, e) + (e.Iteration > 1 ? $" [{e.Iteration}]" : ""), null),
                (ValueText(e.Value) + (e.Unit is { Length: > 0 } u ? " " + u : ""), null),
                (e.Cancelled ? "abgebr." : e.Rating?.ToDisplay() ?? "–", ColorOf(e.Rating)),
                (e.Message ?? "", null),
            ]), 7.5);

        writer.Space(24);
        writer.Need(40);
        writer.Paragraph("Prüfer: ________________________     Datum: ______________     Unterschrift: ________________________", 9);
        return writer.Finish($"Erstellt {DateTime.Now:dd.MM.yyyy HH:mm} · Lauf {run.RunId}");
    }

    /// <summary>Auftrag aus dem Schritt, der die Variable „Auftrag“ schreibt (falls vorhanden).</summary>
    public static string? OrderOf(ResultRun run)
        => run.Events.Where(e => e.Kind == ResultEventKind.Diagnostic && e.Message is { } m && m.StartsWith("Aus der Auswahl übernommen: Auftrag = ", StringComparison.Ordinal))
               .Select(e => e.Message!["Aus der Auswahl übernommen: Auftrag = ".Length..].Trim('"')).FirstOrDefault()
           ?? run.Events.Where(e => e.Kind == ResultEventKind.Step && e.NodeId is { } id && id.Contains("auftrag", StringComparison.OrdinalIgnoreCase) && e.Value is string)
               .Select(e => (string)e.Value!).LastOrDefault();

    /// <summary>Auswertung: Kennzahlen, Fehler-Pareto, Prozessfähigkeit der Messwerte, Tages- und Prüfplatzübersicht.</summary>
    public static PdfDocument StatisticsReport(ResultStatistics s, string? company = null)
    {
        string P(double? v) => v is { } d ? d.ToString("0.0 %", De) : "–";
        var writer = new PdfReportWriter("Auswertung Prüfergebnisse", $"{company ?? "Testumgebung"} · Auswertung · {s.Filter.Describe()}");
        writer.Title("Auswertung der Prüfergebnisse", s.Filter.Describe());
        writer.KeyValues(
        [
            ("Zeitraum", s.First is null ? "keine Prüfläufe" : $"{s.First.Value.LocalDateTime:dd.MM.yyyy HH:mm} – {s.Last!.Value.LocalDateTime:dd.MM.yyyy HH:mm}", null),
            ("Prüfläufe", $"{s.Runs} (PASS {s.Pass}, FAIL {s.Fail}, ERROR {s.Error}, NRESULT {s.NResult})", null),
            ("Ausbeute je Lauf", P(s.Yield), s.Yield is >= 0.95 ? PdfColor.Pass : s.Yield is null ? null : PdfColor.Fail),
            ("Prüflinge", $"{s.Duts} mit Kennung, {s.Retests} Wiederholprüfung(en)", null),
            ("First Pass Yield", P(s.FirstPassYield), null),
            ("Endausbeute", P(s.FinalYield), null),
            ("Werkerabbrüche", s.OperatorAborts.ToString(De), null),
            ("Mittlere Prüfdauer", s.MeanDuration is { } d ? $"{d.TotalSeconds.ToString("0.0", De)} s" : "–", null),
        ]);

        WriteFailureAndMeasurementStatistics();

        void WriteFailureAndMeasurementStatistics()
        {
            if (s.Failures.Count > 0)
            {
                writer.Heading("Fehlerhäufigkeit je Schritt (Pareto)");
                writer.Table([("Schritt", 3), ("Läufe", 0.8), ("FAIL", 0.7), ("ERROR", 0.7), ("Anteil", 0.9), ("häufigste Meldung", 4)],
                    s.Failures.Take(25).Select(f => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (f.Name, null), (f.Runs.ToString(De), null), (f.Fail.ToString(De), f.Fail > 0 ? PdfColor.Fail : null),
                        (f.Error.ToString(De), f.Error > 0 ? PdfColor.Warn : null), (P(s.Runs == 0 ? null : (double)f.Runs / s.Runs), null), (f.TypicalMessage ?? "", null),
                    ]));
            }

            if (s.Measurements.Count > 0)
            {
                writer.Heading("Messwerte und Prozessfähigkeit");
                writer.Table([("Messwert", 2.6), ("n", 0.6), ("Min", 1), ("Max", 1), ("Mittel", 1), ("σ", 0.9), ("Grenzen", 1.6), ("außerh.", 0.8), ("Cpk", 0.8), ("Bewertung", 1.4)],
                    s.Measurements.Select(m => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        ($"{m.Name}{(m.Unit.Length > 0 ? $" [{m.Unit}]" : "")}", null), (m.Count.ToString(De), null), (Num(m.Min), null), (Num(m.Max), null),
                        (Num(m.Mean), null), (Num(m.StdDev), null), (Limits(m.Low, m.High), null), (m.OutOfLimits.ToString(De), m.OutOfLimits > 0 ? PdfColor.Fail : null),
                        (m.Cpk is { } c ? c.ToString("0.00", De) : "–", null),
                        (m.Capability, m.Cpk switch { null => null, >= 1.33 => PdfColor.Pass, >= 1.0 => PdfColor.Warn, _ => PdfColor.Fail }),
                    ]), 8);
                writer.Paragraph("Cpk = min(OG − μ, μ − UG) / 3σ über alle Werte des Zeitraums; ≥ 1,33 fähig, ≥ 1,0 bedingt fähig. Bei wenigen Werten nur als Hinweis verwenden.", 7.5, PdfColor.Gray);
            }

        }

        if (s.Daily.Count > 0)
        {
            writer.Heading("Verlauf je Tag");
            writer.Table([("Tag", 1.5), ("Läufe", 1), ("PASS", 1), ("FAIL", 1), ("ERROR", 1), ("Ausbeute", 1.2)],
                s.Daily.Select(d => (IReadOnlyList<(string, PdfColor?)>)
                [
                    (d.Day.ToString("ddd dd.MM.yyyy", De), null), (d.Total.ToString(De), null), (d.Pass.ToString(De), null),
                    (d.Fail.ToString(De), d.Fail > 0 ? PdfColor.Fail : null), (d.Error.ToString(De), d.Error > 0 ? PdfColor.Warn : null), (P((double)d.Pass / d.Total), null),
                ]));
        }

        if (s.Stations.Count > 1)
        {
            writer.Heading("Prüfplätze");
            writer.Table([("Prüfplatz", 2), ("Läufe", 1), ("PASS", 1), ("Ausbeute", 1)],
                s.Stations.Select(st => (IReadOnlyList<(string, PdfColor?)>)
                    [(st.Station, null), (st.Runs.ToString(De), null), (st.Pass.ToString(De), null), (P(st.Yield), null)]));
        }
        return writer.Finish($"Erstellt {DateTime.Now:dd.MM.yyyy HH:mm} · Datenquelle: Ergebnisdateien");
    }
}
