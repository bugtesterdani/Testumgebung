/**
 * @file
 * @brief Filtert Prüfläufe und berechnet Messwertstatistik, Fehlerhäufigkeit und Berichtszellen.
 */

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Reports;

/// <summary>Auswahl der Prüfläufe für die Auswertung.</summary>
public sealed record StatisticsFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Test { get; init; }
    public string? Version { get; init; }
    public string? Station { get; init; }

    /// <summary>Platzhalter * und ? über die Prüflingskennung.</summary>
    public string? DutPattern { get; init; }

    public bool IncludeDevelopment { get; init; }

    public bool Matches(ResultRun run)
    {
        if (From is { } timestamp && run.Started < timestamp) return false;
        if (To is { } timestamp2 && run.Started > timestamp2) return false;
        if (!string.IsNullOrWhiteSpace(Test) && !string.Equals(run.TestName, Test, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(Version) && run.TestVersion != Version) return false;
        if (!string.IsNullOrWhiteSpace(Station) && run.Station != Station) return false;
        if (!IncludeDevelopment && run.IsDevelopmentRun) return false;
        if (!string.IsNullOrWhiteSpace(DutPattern))
        {
            var regex = "^" + Regex.Escape(DutPattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (!Regex.IsMatch(run.DutId, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        }
        return true;
    }

    public string Describe()
    {
        var parts = new List<string>();
        if (From is { } timestamp) parts.Add($"ab {timestamp.LocalDateTime:dd.MM.yyyy HH:mm}");
        if (To is { } timestamp2) parts.Add($"bis {timestamp2.LocalDateTime:dd.MM.yyyy HH:mm}");
        if (!string.IsNullOrWhiteSpace(Test)) parts.Add($"Test {Test}{(string.IsNullOrWhiteSpace(Version) ? "" : " v" + Version)}");
        if (!string.IsNullOrWhiteSpace(Station)) parts.Add(Station!);
        if (!string.IsNullOrWhiteSpace(DutPattern)) parts.Add($"Kennung {DutPattern}");
        parts.Add(IncludeDevelopment ? "inkl. Entwicklungsläufe" : "ohne Entwicklungsläufe");
        return string.Join(" · ", parts);
    }
}

/// <summary>Kennzahlen eines Messwerts über alle ausgewählten Prüfläufe (Prozessfähigkeit gegen die Grenzwerte).</summary>
public sealed record MeasurementStatistics(string NodeId, string Name, string Unit, int Count, double Min, double Max, double Mean, double StdDev,
    double? Low, double? High, int OutOfLimits, double? Cp, double? Cpk, IReadOnlyList<double> Values)
{
    /// <summary>Bewertung der Fähigkeit: Cpk ≥ 1,33 gut, ≥ 1,0 grenzwertig, sonst unzureichend.</summary>
    public string Capability => Cpk switch
    {
        null => Count < 2 ? "zu wenige Werte" : "ohne Grenzwerte",
        >= 1.33 => "fähig",
        >= 1.0 => "bedingt fähig",
        _ => "nicht fähig",
    };

    /// <summary>Histogramm mit <paramref name="bins"/> Klassen zwischen Min und Max.</summary>
    public IReadOnlyList<int> Histogram(int bins = 12)
    {
        var counts = new int[bins];
        if (Values.Count == 0) return counts;
        var range = Max - Min;
        foreach (var value in Values)
        {
            var index = range <= 0 ? bins / 2 : (int)Math.Min(bins - 1, (value - Min) / range * bins);
            counts[index]++;
        }
        return counts;
    }
}

/// <summary>Fehlerhäufigkeit eines Schritts (Pareto).</summary>
public sealed record FailureItem(string NodeId, string Name, int Fail, int Error, int Runs, string? TypicalMessage)
{
    public int Total => Fail + Error;
}

public sealed record DailyCount(DateOnly Day, int Pass, int Fail, int Error, int Other, int Abort = 0)
{
    public int Total => Pass + Fail + Error + Other + Abort;
}

public sealed record StationCount(string Station, int Runs, int Pass, double Yield);

/// <summary>Auswertung der ausgewählten Prüfläufe.</summary>
public sealed class ResultStatistics
{
    public required StatisticsFilter Filter { get; init; }
    public int Runs { get; init; }
    public int Pass { get; init; }
    public int Fail { get; init; }
    public int Error { get; init; }
    public int NResult { get; init; }
    public int OperatorAborts { get; init; }
    public int DevelopmentRuns { get; init; }
    public int Duts { get; init; }
    public int Retests { get; init; }

    /// <summary>Anteil der Prüflinge, die beim ersten Prüflauf PASS waren (Prüflinge mit Kennung).</summary>
    public double? FirstPassYield { get; init; }

    /// <summary>Anteil der Prüflinge, deren letzter Prüflauf PASS war.</summary>
    public double? FinalYield { get; init; }

    public double? Yield => Runs == 0 ? null : (double)Pass / Runs;
    public TimeSpan? MeanDuration { get; init; }
    public DateTimeOffset? First { get; init; }
    public DateTimeOffset? Last { get; init; }
    public IReadOnlyList<MeasurementStatistics> Measurements { get; init; } = [];
    public IReadOnlyList<FailureItem> Failures { get; init; } = [];
    public IReadOnlyList<DailyCount> Daily { get; init; } = [];
    public IReadOnlyList<StationCount> Stations { get; init; } = [];
}

/// <summary>Geladene Ergebnisdateien (Kurven werden für die Statistik nicht gehalten).</summary>
public sealed record LoadedResults(IReadOnlyList<ResultRun> Runs, IReadOnlyList<string> Problems, int Files);

public static class ResultAnalysis
{
    /// <summary>Alle Ergebnisdateien eines Ordners lesen (beschädigte melden, nicht abbrechen).</summary>
    public static LoadedResults Load(string folder, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder)) return new LoadedResults([], [$"Ordner {folder} existiert nicht."], 0);
        var files = Directory.GetFiles(folder, "*" + ResultFile.Extension);
        var runs = new List<ResultRun>();
        var problems = new List<string>();
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = ResultFile.Read(files[index]);
                var run = content.Run;
                runs.Add(run.CopyWith(run.FilteredFor, run.Events, [], []));
                if (content.Problems.Count > 0) problems.Add($"{Path.GetFileName(files[index])}: {string.Join("; ", content.Problems)}");
            }
            catch (ResultFileException ex)
            {
                problems.Add($"{Path.GetFileName(files[index])}: {ex.Message}");
            }
            progress?.Report((index + 1, files.Length));
        }
        return new LoadedResults(runs.OrderBy(run2 => run2.Started).ToList(), problems, files.Length);
    }

    public static ResultStatistics Compute(IEnumerable<ResultRun> all, StatisticsFilter filter)
    {
        var runs = all.Where(filter.Matches).OrderBy(run => run.Started).ToList();
        var withId = runs.Where(run => !string.IsNullOrWhiteSpace(run.DutId)).GroupBy(run => run.DutId, StringComparer.Ordinal).ToList();
        var durations = runs.Where(run => run.Ended > run.Started).Select(run => (run.Ended - run.Started).TotalSeconds).ToList();

        return new ResultStatistics
        {
            Filter = filter,
            Runs = runs.Count,
            Pass = runs.Count(run => HasOutcome(run, ResultRating.Pass)),
            Fail = runs.Count(run => HasOutcome(run, ResultRating.Fail)),
            Error = runs.Count(run => HasOutcome(run, ResultRating.Error)),
            NResult = runs.Count(run => HasOutcome(run, ResultRating.NResult)),
            OperatorAborts = runs.Count(run => run.OperatorAbort),
            DevelopmentRuns = runs.Count(run => run.IsDevelopmentRun),
            Duts = withId.Count,
            Retests = withId.Sum(group => group.Count() - 1),
            FirstPassYield = withId.Count == 0 ? null : (double)withId.Count(group => HasOutcome(group.First(), ResultRating.Pass)) / withId.Count,
            FinalYield = withId.Count == 0 ? null : (double)withId.Count(group => HasOutcome(group.Last(), ResultRating.Pass)) / withId.Count,
            MeanDuration = durations.Count == 0 ? null : TimeSpan.FromSeconds(durations.Average()),
            First = runs.FirstOrDefault()?.Started,
            Last = runs.LastOrDefault()?.Started,
            Measurements = MeasurementsOf(runs),
            Failures = FailuresOf(runs),
            Daily = runs.GroupBy(run => DateOnly.FromDateTime(run.Started.LocalDateTime)).OrderBy(group => group.Key)
                .Select(group => new DailyCount(group.Key, group.Count(run => HasOutcome(run, ResultRating.Pass)), group.Count(run => HasOutcome(run, ResultRating.Fail)),
                    group.Count(run => HasOutcome(run, ResultRating.Error)), group.Count(run => HasOutcome(run, ResultRating.NResult)), group.Count(run => run.OperatorAbort))).ToList(),
            Stations = runs.GroupBy(run => run.Station).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new StationCount(group.Key, group.Count(), group.Count(run => HasOutcome(run, ResultRating.Pass)), (double)group.Count(run => HasOutcome(run, ResultRating.Pass)) / group.Count())).ToList(),
        };
    }

    private static bool HasOutcome(ResultRun run, ResultRating rating) => !run.OperatorAbort && run.Rating == rating;

    public static string NameOf(ResultRun run, ResultEvent e)
        => run.Structure.FirstOrDefault(resultNode => resultNode.Id == e.NodeId)?.Name ?? (e.Path.LastIndexOf(" > ", StringComparison.Ordinal) is var index and >= 0 ? e.Path[(index + 3)..] : e.Path);

    private static bool IsMeasurement(ResultEvent resultEvent)
        => resultEvent.Kind == ResultEventKind.Step && !resultEvent.Cancelled && resultEvent.Value is double or long && (resultEvent.LowLimit is not null || resultEvent.HighLimit is not null || !string.IsNullOrEmpty(resultEvent.Unit));

    private static IReadOnlyList<MeasurementStatistics> MeasurementsOf(IReadOnlyList<ResultRun> runs)
    {
        var map = new Dictionary<string, (string Name, string Unit, List<double> Values, double? Low, double? High, int Order)>(StringComparer.Ordinal);
        var order = 0;
        foreach (var run in runs)
        {
            foreach (var resultEvent in run.Events.Where(IsMeasurement))
            {
                var key = resultEvent.NodeId ?? resultEvent.Path;
                var value = Convert.ToDouble(resultEvent.Value, CultureInfo.InvariantCulture);
                if (!map.TryGetValue(key, out var namedEntry)) namedEntry = (NameOf(run, resultEvent), resultEvent.Unit ?? "", [], null, null, order++);
                namedEntry.Values.Add(value);
                namedEntry = namedEntry with { Low = resultEvent.LowLimit ?? namedEntry.Low, High = resultEvent.HighLimit ?? namedEntry.High, Unit = string.IsNullOrEmpty(namedEntry.Unit) ? resultEvent.Unit ?? "" : namedEntry.Unit };
                map[key] = namedEntry;
            }
        }
        return map.OrderBy(kv => kv.Value.Order).Select(kv =>
        {
            var (name, unit, values, low, high, _) = kv.Value;
            var mean = values.Average();
            var sd = values.Count > 1 ? Math.Sqrt(values.Sum(value2 => (value2 - mean) * (value2 - mean)) / (values.Count - 1)) : 0;
            var (cp, cpk) = Capability(values.Count, mean, sd, low, high);
            var outside = values.Count(value2 => (low is { } l3 && value2 < l3) || (high is { } h3 && value2 > h3));
            return new MeasurementStatistics(kv.Key, name, unit, values.Count, values.Min(), values.Max(), mean, sd, low, high, outside, cp, cpk, values);
        }).ToList();
    }

    private static (double? Cp, double? Cpk) Capability(int count, double mean, double sd, double? low, double? high)
    {
        double? cp = null, cpk = null;
        if (count >= 2 && sd > 0)
        {
            if (low is { } lowerValue && high is { } upperValue) cp = (upperValue - lowerValue) / (6 * sd);
            var parts = new List<double>();
            if (high is { } h2) parts.Add((h2 - mean) / (3 * sd));
            if (low is { } l2) parts.Add((mean - l2) / (3 * sd));
            if (parts.Count > 0) cpk = parts.Min();
        }
        return (cp, cpk);
    }

    private static IReadOnlyList<FailureItem> FailuresOf(IReadOnlyList<ResultRun> runs)
    {
        var map = new Dictionary<string, (string Name, int Fail, int Error, HashSet<string> Runs, Dictionary<string, int> Messages)>(StringComparer.Ordinal);
        foreach (var run in runs)
        {
            foreach (var resultEvent in run.Events.Where(resultEvent2 => resultEvent2.Kind is ResultEventKind.Step or ResultEventKind.Diagnostic && resultEvent2.Rating is ResultRating.Fail or ResultRating.Error))
            {
                var key = resultEvent.NodeId ?? "(Ablauf)";
                if (!map.TryGetValue(key, out var namedEntry)) namedEntry = (resultEvent.NodeId is null ? "Ablauf" : NameOf(run, resultEvent), 0, 0, [], []);
                if (resultEvent.Rating == ResultRating.Fail) namedEntry.Fail++;
                else namedEntry.Error++;
                namedEntry.Runs.Add(run.RunId);
                if (!string.IsNullOrWhiteSpace(resultEvent.Message))
                {
                    var msg = resultEvent.Message.Length > 120 ? resultEvent.Message[..120] : resultEvent.Message;
                    namedEntry.Messages[msg] = namedEntry.Messages.GetValueOrDefault(msg) + 1;
                }
                map[key] = namedEntry;
            }
        }
        return map.Select(kv => new FailureItem(kv.Key, kv.Value.Name, kv.Value.Fail, kv.Value.Error, kv.Value.Runs.Count,
                kv.Value.Messages.OrderByDescending(entry => entry.Value).Select(entry => entry.Key).FirstOrDefault()))
            .OrderByDescending(failureItem => failureItem.Runs).ThenByDescending(failureItem => failureItem.Total).ToList();
    }

    // ------------------------------------------------------------------ CSV (Excel, deutsch: Semikolon, Dezimalkomma)

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Csv(IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.AppendLine(string.Join(";", row.Select(Cell)));
        return sb.ToString();
    }

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            double number => double.IsNaN(number) ? "" : number.ToString("0.######", De),
            float floatingPointValue => ((double)floatingPointValue).ToString("0.######", De),
            long or int => Convert.ToString(value, De)!,
            DateTimeOffset timestamp => timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", De),
            DateTime dateTime => dateTime.ToString("dd.MM.yyyy HH:mm:ss", De),
            bool flag => flag ? "ja" : "nein",
            ResultRating rating => rating.ToDisplay(),
            _ => value.ToString() ?? "",
        };
        return text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>Messwertstatistik, Fehlerhäufigkeit und Tagesübersicht in einer CSV-Datei (Abschnitte durch Leerzeilen getrennt).</summary>
    public static string StatisticsCsv(ResultStatistics s)
    {
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Auswertung", s.Filter.Describe() },
            new object?[] { "Prüfläufe", s.Runs, "PASS", s.Pass, "FAIL", s.Fail, "ERROR", s.Error, "ABORT", s.OperatorAborts },
            new object?[] { "Prüflinge", s.Duts, "Wiederholprüfungen", s.Retests, "FPY", s.FirstPassYield, "Endausbeute", s.FinalYield },
            Array.Empty<object?>(),
            new object?[] { "Messwert", "Einheit", "Anzahl", "Min", "Max", "Mittelwert", "Standardabweichung", "Untergrenze", "Obergrenze", "außerhalb", "Cp", "Cpk", "Bewertung" },
        };
        rows.AddRange(s.Measurements.Select(measurementStatistics => new object?[] { measurementStatistics.Name, measurementStatistics.Unit, measurementStatistics.Count, measurementStatistics.Min, measurementStatistics.Max, measurementStatistics.Mean, measurementStatistics.StdDev, measurementStatistics.Low, measurementStatistics.High, measurementStatistics.OutOfLimits, measurementStatistics.Cp, measurementStatistics.Cpk, measurementStatistics.Capability }));
        rows.Add(Array.Empty<object?>());
        rows.Add(["Schritt", "FAIL", "ERROR", "betroffene Prüfläufe", "häufigste Meldung"]);
        rows.AddRange(s.Failures.Select(failureItem => new object?[] { failureItem.Name, failureItem.Fail, failureItem.Error, failureItem.Runs, failureItem.TypicalMessage }));
        rows.Add(Array.Empty<object?>());
        rows.Add(["Tag", "PASS", "FAIL", "ERROR", "NRESULT", "ABORT"]);
        rows.AddRange(s.Daily.Select(dailyCount => new object?[] { dailyCount.Day.ToString("dd.MM.yyyy", De), dailyCount.Pass, dailyCount.Fail, dailyCount.Error, dailyCount.Other, dailyCount.Abort }));
        return Csv(rows);
    }

    /// <summary>Alle Ereignisse eines Prüflaufs als CSV.</summary>
    public static string RunCsv(ResultRun run)
    {
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Prüfling", run.DutId, "Test", run.TestName, "Version", run.TestVersion, "Prüfplatz", run.Station, "Bewertung", run.Status.ToString(), "Start", run.Started, "Ende", run.Ended },
            Array.Empty<object?>(),
            new object?[] { "Nr", "Zeit", "Art", "Schritt", "Pfad", "Iteration", "Wert", "Einheit", "Untergrenze", "Obergrenze", "Bewertung", "Meldung" },
        };
        rows.AddRange(run.Events.Select(resultEvent => new object?[]
        {
            resultEvent.Sequence, resultEvent.Time, KindText(resultEvent.Kind), resultEvent.NodeId is null ? "" : NameOf(run, resultEvent), resultEvent.Path, resultEvent.Iteration, resultEvent.Value, resultEvent.Unit, resultEvent.LowLimit, resultEvent.HighLimit,
            resultEvent.Rating?.ToDisplay(), resultEvent.Message,
        }));
        return Csv(rows);
    }

    public static string KindText(ResultEventKind kind) => kind switch
    {
        ResultEventKind.Step => "Schritt",
        ResultEventKind.Group => "Gruppe",
        ResultEventKind.GroupNotEntered => "nicht betreten",
        ResultEventKind.OperatorShown => "Anzeige",
        ResultEventKind.OperatorAnswer => "Werkereingabe",
        ResultEventKind.Diagnostic => "Hinweis",
        ResultEventKind.Development => "Entwicklung",
        _ => "Werkerabbruch",
    };

    /// <summary>CSV mit BOM speichern (Excel erkennt UTF-8).</summary>
    public static void SaveCsv(string path, string csv)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
