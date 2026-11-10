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

    private static string Num(double? number2) => number2 is { } number ? number.ToString("0.####", De) : "–";

    private static string ValueText(object? value) => value switch
    {
        null => "–",
        double number => number.ToString("0.#####", De),
        bool flag => flag ? "ja" : "nein",
        DateTime dateTime => dateTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", De),
        _ => Convert.ToString(value, De) ?? "",
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
                ("Ergebnis", run.Status.ToString(), run.OperatorAbort ? PdfColor.Warn : ColorOf(run.Rating) ?? PdfColor.Black),
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
            var measurements = run.Events.Where(resultEvent => resultEvent.Kind == ResultEventKind.Step && !resultEvent.Cancelled && (resultEvent.LowLimit is not null || resultEvent.HighLimit is not null || resultEvent.Unit is { Length: > 0 }))
                .ToList();
            if (measurements.Count > 0)
            {
                writer.Heading("Messwerte");
                writer.Table([("Messung", 3.2), ("Wert", 1.4), ("Einheit", 0.8), ("Grenzwerte", 1.8), ("Ergebnis", 1)],
                    measurements.Select(resultEvent => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (ResultAnalysis.NameOf(run, resultEvent) + (resultEvent.Iteration > 1 ? $" [{resultEvent.Iteration}]" : ""), null),
                    (ValueText(resultEvent.Value), null), (resultEvent.Unit ?? "", null), (Limits(resultEvent.LowLimit, resultEvent.HighLimit), null),
                    (resultEvent.Rating?.ToDisplay() ?? "–", ColorOf(resultEvent.Rating)),
                    ]));
            }

            var failures = run.Events.Where(resultEvent => resultEvent.Rating is ResultRating.Fail or ResultRating.Error && resultEvent.Kind is ResultEventKind.Step or ResultEventKind.Diagnostic).ToList();
            if (failures.Count > 0)
            {
                writer.Heading("Abweichungen");
                foreach (var resultEvent in failures)
                    writer.Paragraph($"{resultEvent.Rating?.ToDisplay()} · {(resultEvent.NodeId is null ? "Ablauf" : ResultAnalysis.NameOf(run, resultEvent))}: {resultEvent.Message ?? ValueText(resultEvent.Value)}", 9, ColorOf(resultEvent.Rating));
            }

            var answers = run.Events.Where(resultEvent2 => resultEvent2.Kind == ResultEventKind.OperatorAnswer).ToList();
            if (answers.Count > 0)
            {
                writer.Heading("Werkereingaben");
                writer.Table([("Zeit", 1.2), ("Schritt", 3), ("Eingabe", 3)],
                    answers.Select(resultEvent2 => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (resultEvent2.Time.LocalDateTime.ToString("HH:mm:ss", De), null), (ResultAnalysis.NameOf(run, resultEvent2), null), (ValueText(resultEvent2.Value), null),
                    ]));
            }

        }

        void WriteGroups()
        {
            var groups = run.Events.Where(resultEvent2 => resultEvent2.Kind is ResultEventKind.Group or ResultEventKind.GroupNotEntered).ToList();
            if (groups.Count > 0)
            {
                writer.Heading("Ablauf");
                writer.Table([("Gruppe", 4), ("Durchläufe", 1), ("Ergebnis", 1.2), ("Hinweis", 2.5)],
                    groups.Select(resultEvent2 => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (ResultAnalysis.NameOf(run, resultEvent2), null),
                    (resultEvent2.Kind == ResultEventKind.Group ? ValueText(resultEvent2.Value) : "0", null),
                    (resultEvent2.Kind == ResultEventKind.Group ? resultEvent2.Rating?.ToDisplay() ?? "–" : "nicht betreten", ColorOf(resultEvent2.Rating)),
                    (resultEvent2.Message ?? "", null),
                    ]));
            }

        }

        WriteHeader();

        WriteObservations();

        WriteGroups();

        WriteWaveforms();

        void WriteWaveforms()
        {
            foreach (var group in run.Waveforms.GroupBy(waveform => waveform.EventSequence))
            {
                var ev = run.Events.FirstOrDefault(resultEvent2 => resultEvent2.Sequence == group.Key);
                var title = ev is null ? "Kurven" : $"Kurven: {ResultAnalysis.NameOf(run, ev)}";
                var series = group.Select(waveform =>
                {
                    var wave = waveform.Data;
                    var indices = new List<int>();
                    // Pro PDF-Spalte Minimum/Maximum erhalten; keine Vollkopie großer Aufnahmen.
                    var width = Math.Max(1, (wave.Count + 599) / 600);
                    for (var sampleIndex = 0; sampleIndex < wave.Count; sampleIndex += width)
                    {
                        int min = sampleIndex, max = sampleIndex;
                        for (var index = sampleIndex; index < Math.Min(wave.Count, sampleIndex + width); index++)
                        {
                            if (wave.Values[index] < wave.Values[min]) min = index;
                            if (wave.Values[index] > wave.Values[max]) max = index;
                        }
                        indices.Add(Math.Min(min, max));
                        if (min != max) indices.Add(Math.Max(min, max));
                    }
                    return (wave.Channel, indices.Select(indice => wave.TimeOf(indice) * 1000).ToArray(), indices.Select(indice => wave.Values[indice]).ToArray());
                }).ToList();
                writer.Plot(title, series, "ms", group.First().Data.Unit);
                writer.Paragraph(string.Join(" · ", group.Select(waveform => $"{waveform.Data.Channel}: Effektivwert {waveform.Data.Rms().ToString("0.###", De)} {waveform.Data.Unit}, {waveform.Data.Count} Samples")), 8, PdfColor.Gray);
            }

        }

        var steps = run.Events.Where(resultEvent2 => resultEvent2.Kind == ResultEventKind.Step).ToList();
        writer.Heading("Alle Schritte");
        writer.Table([("Zeit", 1.1), ("Schritt", 3.4), ("Wert", 2), ("Ergebnis", 1), ("Meldung", 3)],
            steps.Select(resultEvent2 => (IReadOnlyList<(string, PdfColor?)>)
            [
                (resultEvent2.Time.LocalDateTime.ToString("HH:mm:ss", De), null),
                (ResultAnalysis.NameOf(run, resultEvent2) + (resultEvent2.Iteration > 1 ? $" [{resultEvent2.Iteration}]" : ""), null),
                (ValueText(resultEvent2.Value) + (resultEvent2.Unit is { Length: > 0 } unit ? " " + unit : ""), null),
                (resultEvent2.Cancelled ? "abgebr." : resultEvent2.Rating?.ToDisplay() ?? "–", ColorOf(resultEvent2.Rating)),
                (resultEvent2.Message ?? "", null),
            ]), 7.5);

        writer.Space(24);
        writer.Need(40);
        writer.Paragraph("Prüfer: ________________________     Datum: ______________     Unterschrift: ________________________", 9);
        return writer.Finish($"Erstellt {DateTime.Now:dd.MM.yyyy HH:mm} · Lauf {run.RunId}");
    }

    /// <summary>Auftrag aus dem Schritt, der die Variable „Auftrag“ schreibt (falls vorhanden).</summary>
    public static string? OrderOf(ResultRun run)
        => run.Events.Where(resultEvent => resultEvent.Kind == ResultEventKind.Diagnostic && resultEvent.Message is { } text && text.StartsWith("Aus der Auswahl übernommen: Auftrag = ", StringComparison.Ordinal))
               .Select(resultEvent => resultEvent.Message!["Aus der Auswahl übernommen: Auftrag = ".Length..].Trim('"')).FirstOrDefault()
           ?? run.Events.Where(resultEvent => resultEvent.Kind == ResultEventKind.Step && resultEvent.NodeId is { } id && id.Contains("auftrag", StringComparison.OrdinalIgnoreCase) && resultEvent.Value is string)
               .Select(resultEvent => (string)resultEvent.Value!).LastOrDefault();

    /// <summary>Auswertung: Kennzahlen, Fehler-Pareto, Prozessfähigkeit der Messwerte, Tages- und Prüfplatzübersicht.</summary>
    public static PdfDocument StatisticsReport(ResultStatistics s, string? company = null)
    {
        string P(double? value) => value is { } number ? number.ToString("0.0 %", De) : "–";
        var writer = new PdfReportWriter("Auswertung Prüfergebnisse", $"{company ?? "Testumgebung"} · Auswertung · {s.Filter.Describe()}");
        writer.Title("Auswertung der Prüfergebnisse", s.Filter.Describe());
        writer.KeyValues(
        [
            ("Zeitraum", s.First is null ? "keine Prüfläufe" : $"{s.First.Value.LocalDateTime:dd.MM.yyyy HH:mm} – {s.Last!.Value.LocalDateTime:dd.MM.yyyy HH:mm}", null),
            ("Prüfläufe", $"{s.Runs} (PASS {s.Pass}, FAIL {s.Fail}, ERROR {s.Error}, NRESULT {s.NResult}, ABORT {s.OperatorAborts})", null),
            ("Ausbeute je Lauf", P(s.Yield), s.Yield is >= 0.95 ? PdfColor.Pass : s.Yield is null ? null : PdfColor.Fail),
            ("Prüflinge", $"{s.Duts} mit Kennung, {s.Retests} Wiederholprüfung(en)", null),
            ("First Pass Yield", P(s.FirstPassYield), null),
            ("Endausbeute", P(s.FinalYield), null),
            ("Werkerabbrüche", s.OperatorAborts.ToString(De), null),
            ("Mittlere Prüfdauer", s.MeanDuration is { } duration ? $"{duration.TotalSeconds.ToString("0.0", De)} s" : "–", null),
        ]);

        WriteFailureAndMeasurementStatistics();

        void WriteFailureAndMeasurementStatistics()
        {
            if (s.Failures.Count > 0)
            {
                writer.Heading("Fehlerhäufigkeit je Schritt (Pareto)");
                writer.Table([("Schritt", 3), ("Läufe", 0.8), ("FAIL", 0.7), ("ERROR", 0.7), ("Anteil", 0.9), ("häufigste Meldung", 4)],
                    s.Failures.Take(25).Select(failureItem => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        (failureItem.Name, null), (failureItem.Runs.ToString(De), null), (failureItem.Fail.ToString(De), failureItem.Fail > 0 ? PdfColor.Fail : null),
                        (failureItem.Error.ToString(De), failureItem.Error > 0 ? PdfColor.Warn : null), (P(s.Runs == 0 ? null : (double)failureItem.Runs / s.Runs), null), (failureItem.TypicalMessage ?? "", null),
                    ]));
            }

            if (s.Measurements.Count > 0)
            {
                writer.Heading("Messwerte und Prozessfähigkeit");
                writer.Table([("Messwert", 2.6), ("n", 0.6), ("Min", 1), ("Max", 1), ("Mittel", 1), ("σ", 0.9), ("Grenzen", 1.6), ("außerh.", 0.8), ("Cpk", 0.8), ("Bewertung", 1.4)],
                    s.Measurements.Select(measurementStatistics => (IReadOnlyList<(string, PdfColor?)>)
                    [
                        ($"{measurementStatistics.Name}{(measurementStatistics.Unit.Length > 0 ? $" [{measurementStatistics.Unit}]" : "")}", null), (measurementStatistics.Count.ToString(De), null), (Num(measurementStatistics.Min), null), (Num(measurementStatistics.Max), null),
                        (Num(measurementStatistics.Mean), null), (Num(measurementStatistics.StdDev), null), (Limits(measurementStatistics.Low, measurementStatistics.High), null), (measurementStatistics.OutOfLimits.ToString(De), measurementStatistics.OutOfLimits > 0 ? PdfColor.Fail : null),
                        (measurementStatistics.Cpk is { } number2 ? number2.ToString("0.00", De) : "–", null),
                        (measurementStatistics.Capability, measurementStatistics.Cpk switch { null => null, >= 1.33 => PdfColor.Pass, >= 1.0 => PdfColor.Warn, _ => PdfColor.Fail }),
                    ]), 8);
                writer.Paragraph("Cpk = min(OG − μ, μ − UG) / 3σ über alle Werte des Zeitraums; ≥ 1,33 fähig, ≥ 1,0 bedingt fähig. Bei wenigen Werten nur als Hinweis verwenden.", 7.5, PdfColor.Gray);
            }

        }

        if (s.Daily.Count > 0)
        {
            writer.Heading("Verlauf je Tag");
            writer.Table([("Tag", 1.5), ("Läufe", 1), ("PASS", 1), ("FAIL", 1), ("ERROR", 1), ("ABORT", 1), ("Ausbeute", 1.2)],
                s.Daily.Select(dailyCount => (IReadOnlyList<(string, PdfColor?)>)
                [
                    (dailyCount.Day.ToString("ddd dd.MM.yyyy", De), null), (dailyCount.Total.ToString(De), null), (dailyCount.Pass.ToString(De), null),
                    (dailyCount.Fail.ToString(De), dailyCount.Fail > 0 ? PdfColor.Fail : null), (dailyCount.Error.ToString(De), dailyCount.Error > 0 ? PdfColor.Warn : null), (dailyCount.Abort.ToString(De), dailyCount.Abort > 0 ? PdfColor.Warn : null), (P((double)dailyCount.Pass / dailyCount.Total), null),
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
