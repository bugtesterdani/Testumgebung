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
        if (From is { } f && run.Started < f) return false;
        if (To is { } t && run.Started > t) return false;
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
        if (From is { } f) parts.Add($"ab {f.LocalDateTime:dd.MM.yyyy HH:mm}");
        if (To is { } t) parts.Add($"bis {t.LocalDateTime:dd.MM.yyyy HH:mm}");
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
        foreach (var v in Values)
        {
            var i = range <= 0 ? bins / 2 : (int)Math.Min(bins - 1, (v - Min) / range * bins);
            counts[i]++;
        }
        return counts;
    }
}

/// <summary>Fehlerhäufigkeit eines Schritts (Pareto).</summary>
public sealed record FailureItem(string NodeId, string Name, int Fail, int Error, int Runs, string? TypicalMessage)
{
    public int Total => Fail + Error;
}

public sealed record DailyCount(DateOnly Day, int Pass, int Fail, int Error, int Other)
{
    public int Total => Pass + Fail + Error + Other;
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
        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = ResultFile.Read(files[i]);
                var r = content.Run;
                runs.Add(r.CopyWith(r.FilteredFor, r.Events, [], []));
                if (content.Problems.Count > 0) problems.Add($"{Path.GetFileName(files[i])}: {string.Join("; ", content.Problems)}");
            }
            catch (ResultFileException ex)
            {
                problems.Add($"{Path.GetFileName(files[i])}: {ex.Message}");
            }
            progress?.Report((i + 1, files.Length));
        }
        return new LoadedResults(runs.OrderBy(r => r.Started).ToList(), problems, files.Length);
    }

    public static ResultStatistics Compute(IEnumerable<ResultRun> all, StatisticsFilter filter)
    {
        var runs = all.Where(filter.Matches).OrderBy(r => r.Started).ToList();
        var withId = runs.Where(r => !string.IsNullOrWhiteSpace(r.DutId)).GroupBy(r => r.DutId, StringComparer.Ordinal).ToList();
        var durations = runs.Where(r => r.Ended > r.Started).Select(r => (r.Ended - r.Started).TotalSeconds).ToList();

        return new ResultStatistics
        {
            Filter = filter,
            Runs = runs.Count,
            Pass = runs.Count(r => r.Rating == ResultRating.Pass),
            Fail = runs.Count(r => r.Rating == ResultRating.Fail),
            Error = runs.Count(r => r.Rating == ResultRating.Error),
            NResult = runs.Count(r => r.Rating == ResultRating.NResult),
            OperatorAborts = runs.Count(r => r.OperatorAbort),
            DevelopmentRuns = runs.Count(r => r.IsDevelopmentRun),
            Duts = withId.Count,
            Retests = withId.Sum(g => g.Count() - 1),
            FirstPassYield = withId.Count == 0 ? null : (double)withId.Count(g => g.First().Rating == ResultRating.Pass) / withId.Count,
            FinalYield = withId.Count == 0 ? null : (double)withId.Count(g => g.Last().Rating == ResultRating.Pass) / withId.Count,
            MeanDuration = durations.Count == 0 ? null : TimeSpan.FromSeconds(durations.Average()),
            First = runs.FirstOrDefault()?.Started,
            Last = runs.LastOrDefault()?.Started,
            Measurements = MeasurementsOf(runs),
            Failures = FailuresOf(runs),
            Daily = runs.GroupBy(r => DateOnly.FromDateTime(r.Started.LocalDateTime)).OrderBy(g => g.Key)
                .Select(g => new DailyCount(g.Key, g.Count(r => r.Rating == ResultRating.Pass), g.Count(r => r.Rating == ResultRating.Fail),
                    g.Count(r => r.Rating == ResultRating.Error), g.Count(r => r.Rating == ResultRating.NResult))).ToList(),
            Stations = runs.GroupBy(r => r.Station).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new StationCount(g.Key, g.Count(), g.Count(r => r.Rating == ResultRating.Pass), (double)g.Count(r => r.Rating == ResultRating.Pass) / g.Count())).ToList(),
        };
    }

    public static string NameOf(ResultRun run, ResultEvent e)
        => run.Structure.FirstOrDefault(n => n.Id == e.NodeId)?.Name ?? (e.Path.LastIndexOf(" > ", StringComparison.Ordinal) is var i and >= 0 ? e.Path[(i + 3)..] : e.Path);

    private static bool IsMeasurement(ResultEvent e)
        => e.Kind == ResultEventKind.Step && !e.Cancelled && e.Value is double or long && (e.LowLimit is not null || e.HighLimit is not null || !string.IsNullOrEmpty(e.Unit));

    private static IReadOnlyList<MeasurementStatistics> MeasurementsOf(IReadOnlyList<ResultRun> runs)
    {
        var map = new Dictionary<string, (string Name, string Unit, List<double> Values, double? Low, double? High, int Order)>(StringComparer.Ordinal);
        var order = 0;
        foreach (var run in runs)
        {
            foreach (var e in run.Events.Where(IsMeasurement))
            {
                var key = e.NodeId ?? e.Path;
                var value = Convert.ToDouble(e.Value, CultureInfo.InvariantCulture);
                if (!map.TryGetValue(key, out var m)) m = (NameOf(run, e), e.Unit ?? "", [], null, null, order++);
                m.Values.Add(value);
                m = m with { Low = e.LowLimit ?? m.Low, High = e.HighLimit ?? m.High, Unit = string.IsNullOrEmpty(m.Unit) ? e.Unit ?? "" : m.Unit };
                map[key] = m;
            }
        }
        return map.OrderBy(kv => kv.Value.Order).Select(kv =>
        {
            var (name, unit, values, low, high, _) = kv.Value;
            var mean = values.Average();
            var sd = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)) : 0;
            var (cp, cpk) = Capability(values.Count, mean, sd, low, high);
            var outside = values.Count(v => (low is { } l3 && v < l3) || (high is { } h3 && v > h3));
            return new MeasurementStatistics(kv.Key, name, unit, values.Count, values.Min(), values.Max(), mean, sd, low, high, outside, cp, cpk, values);
        }).ToList();
    }

    private static (double? Cp, double? Cpk) Capability(int count, double mean, double sd, double? low, double? high)
    {
        double? cp = null, cpk = null;
        if (count >= 2 && sd > 0)
        {
            if (low is { } l && high is { } h) cp = (h - l) / (6 * sd);
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
            foreach (var e in run.Events.Where(e => e.Kind is ResultEventKind.Step or ResultEventKind.Diagnostic && e.Rating is ResultRating.Fail or ResultRating.Error))
            {
                var key = e.NodeId ?? "(Ablauf)";
                if (!map.TryGetValue(key, out var m)) m = (e.NodeId is null ? "Ablauf" : NameOf(run, e), 0, 0, [], []);
                if (e.Rating == ResultRating.Fail) m.Fail++;
                else m.Error++;
                m.Runs.Add(run.RunId);
                if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    var msg = e.Message.Length > 120 ? e.Message[..120] : e.Message;
                    m.Messages[msg] = m.Messages.GetValueOrDefault(msg) + 1;
                }
                map[key] = m;
            }
        }
        return map.Select(kv => new FailureItem(kv.Key, kv.Value.Name, kv.Value.Fail, kv.Value.Error, kv.Value.Runs.Count,
                kv.Value.Messages.OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault()))
            .OrderByDescending(f => f.Runs).ThenByDescending(f => f.Total).ToList();
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

    private static string Cell(object? v)
    {
        var s = v switch
        {
            null => "",
            double d => double.IsNaN(d) ? "" : d.ToString("0.######", De),
            float f => ((double)f).ToString("0.######", De),
            long or int => Convert.ToString(v, De)!,
            DateTimeOffset t => t.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", De),
            DateTime t => t.ToString("dd.MM.yyyy HH:mm:ss", De),
            bool b => b ? "ja" : "nein",
            ResultRating r => r.ToDisplay(),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Messwertstatistik, Fehlerhäufigkeit und Tagesübersicht in einer CSV-Datei (Abschnitte durch Leerzeilen getrennt).</summary>
    public static string StatisticsCsv(ResultStatistics s)
    {
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Auswertung", s.Filter.Describe() },
            new object?[] { "Prüfläufe", s.Runs, "PASS", s.Pass, "FAIL", s.Fail, "ERROR", s.Error, "Werkerabbruch", s.OperatorAborts },
            new object?[] { "Prüflinge", s.Duts, "Wiederholprüfungen", s.Retests, "FPY", s.FirstPassYield, "Endausbeute", s.FinalYield },
            Array.Empty<object?>(),
            new object?[] { "Messwert", "Einheit", "Anzahl", "Min", "Max", "Mittelwert", "Standardabweichung", "Untergrenze", "Obergrenze", "außerhalb", "Cp", "Cpk", "Bewertung" },
        };
        rows.AddRange(s.Measurements.Select(m => new object?[] { m.Name, m.Unit, m.Count, m.Min, m.Max, m.Mean, m.StdDev, m.Low, m.High, m.OutOfLimits, m.Cp, m.Cpk, m.Capability }));
        rows.Add(Array.Empty<object?>());
        rows.Add(["Schritt", "FAIL", "ERROR", "betroffene Prüfläufe", "häufigste Meldung"]);
        rows.AddRange(s.Failures.Select(f => new object?[] { f.Name, f.Fail, f.Error, f.Runs, f.TypicalMessage }));
        rows.Add(Array.Empty<object?>());
        rows.Add(["Tag", "PASS", "FAIL", "ERROR", "NRESULT"]);
        rows.AddRange(s.Daily.Select(d => new object?[] { d.Day.ToString("dd.MM.yyyy", De), d.Pass, d.Fail, d.Error, d.Other }));
        return Csv(rows);
    }

    /// <summary>Alle Ereignisse eines Prüflaufs als CSV.</summary>
    public static string RunCsv(ResultRun run)
    {
        var rows = new List<IEnumerable<object?>>
        {
            new object?[] { "Prüfling", run.DutId, "Test", run.TestName, "Version", run.TestVersion, "Prüfplatz", run.Station, "Bewertung", run.Rating, "Start", run.Started, "Ende", run.Ended },
            Array.Empty<object?>(),
            new object?[] { "Nr", "Zeit", "Art", "Schritt", "Pfad", "Iteration", "Wert", "Einheit", "Untergrenze", "Obergrenze", "Bewertung", "Meldung" },
        };
        rows.AddRange(run.Events.Select(e => new object?[]
        {
            e.Sequence, e.Time, KindText(e.Kind), e.NodeId is null ? "" : NameOf(run, e), e.Path, e.Iteration, e.Value, e.Unit, e.LowLimit, e.HighLimit,
            e.Rating?.ToDisplay(), e.Message,
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
