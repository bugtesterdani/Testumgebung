/**
 * @file
 * @brief Ordnet importierte Spalten Kanälen und Zeitbasis zu und meldet ungültige Samples ausdrücklich.
 */

using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Persistence.Curves;

public sealed class CurveImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Spalte einer Importquelle (Datei oder Messung).</summary>
public sealed record ImportColumn(int Index, string Name, string? Unit, bool IsNumeric, double Min, double Max);

/// <summary>Gelesene Rohdaten: Spalten mit Werten (NaN = fehlend), Vorschau, vom Format vorgegebene Abtastrate.</summary>
public sealed class ImportTable
{
    public required string Format { get; init; }
    public required string SourceName { get; init; }
    public required string Sha256 { get; init; }
    public required IReadOnlyList<ImportColumn> Columns { get; init; }
    public required IReadOnlyList<double[]> Data { get; init; }
    public required IReadOnlyList<string[]> PreviewRows { get; init; }
    public int Rows => Data.Count == 0 ? 0 : Data[0].Length;

    /// <summary>Vom Format vorgegebene feste Abtastrate (COMTRADE, Ergebnisdatei); null = aus Zeitspalte.</summary>
    public double? SampleRate { get; init; }

    /// <summary>Vermutete Zeitspalte und ihr Faktor in Sekunden (s = 1, ms = 0.001, µs = 1e-6).</summary>
    public string? TimeColumn { get; init; }
    public double TimeFactor { get; init; } = 1;

    public string? QualityColumn { get; init; }
    public List<string> Warnings { get; } = [];

    public double[] Column(string name)
        => Data[(Columns.FirstOrDefault(c => c.Name == name) ?? throw new CurveImportException($"Spalte '{name}' nicht vorhanden.")).Index];
}

public enum TimeBaseMode
{
    /// <summary>Explizite Zeitpunkte aus einer Zeitspalte (gemeinsam für alle Signale).</summary>
    ExplicitTimes,
    /// <summary>Feste Abtastrate (vom Format vorgegeben oder ausdrücklich angegeben).</summary>
    FixedRate,
}

public sealed record ChannelMapping(string SourceColumn, string TargetChannel, string Unit);

public sealed record ImportSettings(
    TimeBaseMode Mode,
    string? TimeColumn,
    double TimeFactor,
    double? SampleRate,
    IReadOnlyList<ChannelMapping> Channels,
    string? QualityColumn = null);

/// <summary>Zusammenfassung je Zielkanal (Kanalinformationen der Importvorschau).</summary>
public sealed record ChannelSummary(string Channel, string Unit, double Min, double Max, string Quality);

public static class CurveImport
{
    public static readonly string[] SupportedExtensions = [".csv", ".txt", ".tsv", ".cfg", ".xlsx", ".tres"];

    /// <summary>Datei lesen – Format nach Endung (COMTRADE über die .cfg-Datei).</summary>
    public static ImportTable Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".cfg" or ".dat" => Comtrade.Read(Path.ChangeExtension(path, ".cfg")),
                ".xlsx" => Xlsx.Read(path),
                ".tres" => ResultFileSource.ReadFirst(path),
                _ => Csv.Read(path),
            };
        }
        catch (IOException ex)
        {
            throw new CurveImportException($"Datei nicht lesbar: {ex.Message}", ex);
        }
    }

    /// <summary>Standardzuordnung: alle numerischen Nicht-Zeit-/Qualitätsspalten, Zielname aus L1/L2/L3 im Namen, sonst Spaltenname.</summary>
    public static ImportSettings SuggestSettings(ImportTable t)
    {
        var channels = t.Columns.Where(c => c.IsNumeric && c.Name != t.TimeColumn && c.Name != t.QualityColumn)
            .Select(c => new ChannelMapping(c.Name, SuggestTarget(c.Name), c.Unit ?? "")).ToList();
        var mode = t.TimeColumn is not null ? TimeBaseMode.ExplicitTimes : TimeBaseMode.FixedRate;
        return new ImportSettings(mode, t.TimeColumn, t.TimeFactor, t.SampleRate, channels, t.QualityColumn);
    }

    private static string SuggestTarget(string name)
    {
        var m = Regex.Match(name, @"L\s*([123])", RegexOptions.IgnoreCase);
        return m.Success ? "L" + m.Groups[1].Value : name;
    }

    /// <summary>Gemeinsame Zeitbasis prüfen und Kurve bilden. Keine automatische Skalierung oder Abtastratenänderung.</summary>
    public static CurveDefinition ToCurve(ImportTable t, ImportSettings s, string id, string name, string version, string createdBy, DateTimeOffset createdAt)
    {
        if (s.Channels.Count == 0) throw new CurveImportException("Mindestens einen Kanal zuordnen.");
        if (s.Channels.Select(c => c.TargetChannel).Distinct().Count() != s.Channels.Count) throw new CurveImportException("Zielkanäle müssen eindeutig sein.");
        var (rows, times, rate) = ResolveTimeBase(t, s);

        var channels = new List<CurveChannel>();
        foreach (var m in s.Channels)
        {
            var col = t.Column(m.SourceColumn);
            var data = rows.Select(r => col[r]).ToArray();
            var missing = data.Count(v => !double.IsFinite(v));
            if (missing > 0) throw new CurveImportException($"Spalte {m.SourceColumn}: {missing} fehlende/ungültige Werte – Datei prüfen.");
            channels.Add(new CurveChannel(m.TargetChannel, m.Unit, data));
        }

        string? quality = null;
        if (s.QualityColumn is { } qc)
        {
            var q = t.Column(qc);
            var bad = rows.Count(r => double.IsFinite(q[r]) && q[r] != 0);
            if (bad > 0) quality = $"{bad} Zeile(n) mit Qualitätscode ≠ 0";
        }
        if (t.Warnings.Count > 0) quality = string.Join("; ", t.Warnings.Prepend(quality ?? "").Where(x => x.Length > 0));

        var mapping = string.Join(", ", s.Channels.Select(c => $"{c.SourceColumn}→{c.TargetChannel} [{c.Unit}]"));
        var timeText = s.Mode == TimeBaseMode.ExplicitTimes
            ? $"explizite Zeitpunkte aus '{s.TimeColumn}' (×{s.TimeFactor.ToString("0.######", CultureInfo.InvariantCulture)} s)"
            : $"feste Abtastrate {rate.ToString("0.######", CultureInfo.InvariantCulture)} Hz";
        var curve = new CurveDefinition
        {
            Id = id,
            Name = name,
            Version = version,
            SampleRate = rate,
            Times = times,
            Channels = channels,
            Origin = t.Format == "Ergebnisdatei" ? CurveOrigin.Measurement : CurveOrigin.FileImport,
            SourceText = $"{t.Format} '{t.SourceName}' (SHA-256 {t.Sha256[..16]}…), {timeText}, Zuordnung {mapping}",
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            Quality = quality,
        };
        var problems = CurveOps.Validate(curve);
        if (problems.Count > 0) throw new CurveImportException(string.Join(" ", problems));
        return curve;
    }

    private static (List<int> Rows, double[]? Times, double Rate) ResolveTimeBase(ImportTable t, ImportSettings s)
    {
        var rows = Enumerable.Range(0, t.Rows).ToList();
        double[]? times = null;
        double rate;
        if (s.Mode == TimeBaseMode.ExplicitTimes)
        {
            if (s.TimeColumn is null) throw new CurveImportException("Zeitspalte wählen oder feste Abtastrate angeben.");
            var tc = t.Column(s.TimeColumn);
            rows = rows.Where(r => double.IsFinite(tc[r])).ToList();
            times = rows.Select(r => tc[r] * s.TimeFactor).ToArray();
            for (var i = 1; i < times.Length; i++)
                if (times[i] <= times[i - 1]) throw new CurveImportException($"Zeitspalte nicht streng steigend (Datenzeile {rows[i] + 1}).");
            var t0 = times.Length > 0 ? times[0] : 0;
            times = times.Select(x => x - t0).ToArray();
            rate = times.Length > 1 ? (times.Length - 1) / times[^1] : 0;
        }
        else
        {
            rate = s.SampleRate ?? t.SampleRate ?? throw new CurveImportException("Feste Abtastrate angeben.");
            if (rate <= 0) throw new CurveImportException("Abtastrate muss > 0 sein.");
        }

        return (rows, times, rate);
    }

    public static IReadOnlyList<ChannelSummary> Summarize(ImportTable t, ImportSettings s)
    {
        double[]? q = s.QualityColumn is { } qc ? t.Column(qc) : null;
        var bad = q?.Count(v => double.IsFinite(v) && v != 0) ?? 0;
        return s.Channels.Select(m =>
        {
            var col = t.Column(m.SourceColumn).Where(double.IsFinite).ToArray();
            return new ChannelSummary(m.TargetChannel, m.Unit, col.Length == 0 ? 0 : col.Min(), col.Length == 0 ? 0 : col.Max(),
                q is null ? "—" : bad == 0 ? "0 (keine Warnungen)" : $"{bad} Warnung(en)");
        }).ToList();
    }

    // ------------------------------------------------------------------ gemeinsame Tabellenlogik

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Aus Textzeilen (Kopfzeile, optionale Einheitenzeile, Daten) eine Tabelle bilden.</summary>
    internal static ImportTable FromGrid(string format, string name, string sha, List<string[]> grid, CultureInfo culture)
    {
        grid = grid.Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (grid.Count == 0) throw new CurveImportException("Datei enthält keine Daten.");
        var width = grid.Max(r => r.Length);
        var (header, units, first) = ReadGridHeader(grid, width, culture);

        var rows = grid.Count - first;
        var data = Enumerable.Range(0, width).Select(_ => new double[rows]).ToList();
        for (var r = 0; r < rows; r++)
        {
            var row = grid[first + r];
            for (var c = 0; c < width; c++)
                data[c][r] = c < row.Length && double.TryParse(row[c].Trim(), NumberStyles.Float, culture, out var v) ? v : double.NaN;
        }

        var columns = new List<ImportColumn>();
        for (var c = 0; c < width; c++)
        {
            var finite = data[c].Where(double.IsFinite).ToArray();
            columns.Add(new ImportColumn(c, header[c], units[c], finite.Length > rows / 2, finite.Length == 0 ? 0 : finite.Min(), finite.Length == 0 ? 0 : finite.Max()));
        }

        var (timeCol, factor) = DetectTimeColumn(columns);
        var qualityCol = columns.FirstOrDefault(c => c.IsNumeric && Regex.IsMatch(c.Name, "qual|status|flag", RegexOptions.IgnoreCase))?.Name;

        return new ImportTable
        {
            Format = format,
            SourceName = name,
            Sha256 = sha,
            Columns = columns,
            Data = data,
            PreviewRows = grid.Skip(first).Take(10).ToList(),
            TimeColumn = timeCol,
            TimeFactor = factor,
            QualityColumn = qualityCol,
        };
    }
    private static (string[] Header, string?[] Units, int FirstDataRow) ReadGridHeader(List<string[]> grid, int width, CultureInfo culture)
    {
        bool IsNum(string s) => double.TryParse(s.Trim(), NumberStyles.Float, culture, out _);
        string[] header;
        string?[] units = new string?[width];
        var first = 0;
        if (!grid[0].Where(c => c.Trim().Length > 0).All(IsNum))
        {
            header = Enumerable.Range(0, width).Select(i => i < grid[0].Length && grid[0][i].Trim().Length > 0 ? grid[0][i].Trim() : $"Spalte {i + 1}").ToArray();
            first = 1;
            if (grid.Count > 1 && grid[1].Where(c => c.Trim().Length > 0).Any() && !grid[1].Where(c => c.Trim().Length > 0).Any(IsNum))
            {
                for (var i = 0; i < width; i++) units[i] = i < grid[1].Length ? grid[1][i].Trim().Trim('[', ']', '(', ')') : null;
                first = 2;
            }
        }
        else header = Enumerable.Range(1, width).Select(i => $"Spalte {i}").ToArray();

        // Einheit im Namen: „U_L1 [V]“, „U_L1 (V)“, „Zeit_s“, „t_ms“
        for (var i = 0; i < width; i++)
        {
            var m = Regex.Match(header[i], @"^(.*?)\s*[\[(]([^\])]+)[\])]\s*$");
            if (m.Success)
            {
                units[i] ??= m.Groups[2].Value.Trim();
                header[i] = m.Groups[1].Value.Trim().Length > 0 ? m.Groups[1].Value.Trim() : header[i];
            }
        }
        if (header.Distinct().Count() != header.Length)
            header = header.Select((h, i) => header.Take(i).Contains(h) ? $"{h}_{i + 1}" : h).ToArray();

        return (header, units, first);
    }

    private static (string? Column, double Factor) DetectTimeColumn(List<ImportColumn> columns)
    {
        // Zeitspalte und Einheit erkennen
        string? timeCol = null;
        var factor = 1.0;
        foreach (var c in columns.Where(c => c.IsNumeric))
        {
            var n = c.Name.ToLowerInvariant();
            var u = (c.Unit ?? "").ToLowerInvariant();
            if (IsTimeColumn(n))
            {
                timeCol = c.Name;
                var unit = TimeUnit(u, n);
                factor = unit switch { "ms" => 1e-3, "us" or "µs" => 1e-6, "ns" => 1e-9, "min" => 60, _ => 1 };
                if (c.Unit is null) columns[c.Index] = c with { Unit = unit == "us" ? "µs" : unit };
                break;
            }
        }
        return (timeCol, factor);
    }

    private static bool IsTimeColumn(string name)
        => name is "t" or "time" or "zeit" || name.StartsWith("zeit", StringComparison.Ordinal)
            || name.StartsWith("time", StringComparison.Ordinal) || name.StartsWith("t_", StringComparison.Ordinal);

    private static string TimeUnit(string unit, string name)
    {
        if (unit.Length > 0) return unit;
        if (name.Contains("_ms")) return "ms";
        if (name.Contains("_us") || name.Contains("_µs")) return "us";
        return "s";
    }

}

/// <summary>CSV/Text: Trennzeichen (; , Tab |) und Dezimalzeichen werden erkannt; Kommentarzeilen mit # werden übersprungen.</summary>
public static class Csv
{
    public static ImportTable Read(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    public static ImportTable Parse(byte[] bytes, string name, char? delimiter = null, char? decimalSeparator = null)
    {
        var text = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.TrimStart().StartsWith('#')).ToList();
        var sample = lines.Where(l => l.Trim().Length > 0).Take(20).ToList();
        var delim = delimiter ?? new[] { ';', '\t', ',', '|' }
            .OrderByDescending(d => sample.Count(l => l.Contains(d)) * 100 + (sample.Count > 0 ? sample.Select(l => l.Count(ch => ch == d)).Distinct().Count() == 1 ? 10 : 0 : 0))
            .First(d => sample.Any(l => l.Contains(d)) || d == '|');
        var dec = decimalSeparator ?? (delim != ',' && sample.Skip(1).Any(l => Regex.IsMatch(l, @"\d,\d")) ? ',' : '.');
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = dec.ToString();
        culture.NumberFormat.NumberGroupSeparator = dec == ',' ? "." : ",";
        var grid = lines.Select(l => l.Split(delim).Select(c => c.Trim().Trim('"')).ToArray()).ToList();
        var table = CurveImport.FromGrid("CSV/Text", name, CurveImport.Hash(bytes), grid, culture);
        return table;
    }
}
