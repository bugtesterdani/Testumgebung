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
        => Data[(Columns.FirstOrDefault(importColumn => importColumn.Name == name) ?? throw new CurveImportException($"Spalte '{name}' nicht vorhanden.")).Index];
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
        var channels = t.Columns.Where(importColumn => importColumn.IsNumeric && importColumn.Name != t.TimeColumn && importColumn.Name != t.QualityColumn)
            .Select(importColumn => new ChannelMapping(importColumn.Name, SuggestTarget(importColumn.Name), importColumn.Unit ?? "")).ToList();
        var mode = t.TimeColumn is not null ? TimeBaseMode.ExplicitTimes : TimeBaseMode.FixedRate;
        return new ImportSettings(mode, t.TimeColumn, t.TimeFactor, t.SampleRate, channels, t.QualityColumn);
    }

    private static string SuggestTarget(string name)
    {
        var match = Regex.Match(name, @"L\s*([123])", RegexOptions.IgnoreCase);
        return match.Success ? "L" + match.Groups[1].Value : name;
    }

    /// <summary>Gemeinsame Zeitbasis prüfen und Kurve bilden. Keine automatische Skalierung oder Abtastratenänderung.</summary>
    public static CurveDefinition ToCurve(ImportTable t, ImportSettings s, string id, string name, string version, string createdBy, DateTimeOffset createdAt)
    {
        if (s.Channels.Count == 0) throw new CurveImportException("Mindestens einen Kanal zuordnen.");
        if (s.Channels.Select(channelMapping => channelMapping.TargetChannel).Distinct().Count() != s.Channels.Count) throw new CurveImportException("Zielkanäle müssen eindeutig sein.");
        var (rows, times, rate) = ResolveTimeBase(t, s);

        var channels = new List<CurveChannel>();
        foreach (var channelMapping in s.Channels)
        {
            var col = t.Column(channelMapping.SourceColumn);
            var data = rows.Select(row => col[row]).ToArray();
            var missing = data.Count(value => !double.IsFinite(value));
            if (missing > 0) throw new CurveImportException($"Spalte {channelMapping.SourceColumn}: {missing} fehlende/ungültige Werte – Datei prüfen.");
            channels.Add(new CurveChannel(channelMapping.TargetChannel, channelMapping.Unit, data));
        }

        string? quality = null;
        if (s.QualityColumn is { } qc)
        {
            var samples = t.Column(qc);
            var bad = rows.Count(row => double.IsFinite(samples[row]) && samples[row] != 0);
            if (bad > 0) quality = $"{bad} Zeile(n) mit Qualitätscode ≠ 0";
        }
        if (t.Warnings.Count > 0) quality = string.Join("; ", t.Warnings.Prepend(quality ?? "").Where(prepend => prepend.Length > 0));

        var mapping = string.Join(", ", s.Channels.Select(channelMapping2 => $"{channelMapping2.SourceColumn}→{channelMapping2.TargetChannel} [{channelMapping2.Unit}]"));
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

    private static (List<int> Rows, double[]? Times, double Rate) ResolveTimeBase(ImportTable importTable, ImportSettings importSettings)
    {
        var rows = Enumerable.Range(0, importTable.Rows).ToList();
        double[]? times = null;
        double rate;
        if (importSettings.Mode == TimeBaseMode.ExplicitTimes)
        {
            if (importSettings.TimeColumn is null) throw new CurveImportException("Zeitspalte wählen oder feste Abtastrate angeben.");
            var tc = importTable.Column(importSettings.TimeColumn);
            rows = rows.Where(row => double.IsFinite(tc[row])).ToList();
            times = rows.Select(row => tc[row] * importSettings.TimeFactor).ToArray();
            for (var index = 1; index < times.Length; index++)
                if (times[index] <= times[index - 1]) throw new CurveImportException($"Zeitspalte nicht streng steigend (Datenzeile {rows[index] + 1}).");
            var t0 = times.Length > 0 ? times[0] : 0;
            times = times.Select(time => time - t0).ToArray();
            rate = times.Length > 1 ? (times.Length - 1) / times[^1] : 0;
        }
        else
        {
            rate = importSettings.SampleRate ?? importTable.SampleRate ?? throw new CurveImportException("Feste Abtastrate angeben.");
            if (rate <= 0) throw new CurveImportException("Abtastrate muss > 0 sein.");
        }

        return (rows, times, rate);
    }

    public static IReadOnlyList<ChannelSummary> Summarize(ImportTable t, ImportSettings s)
    {
        double[]? samples = s.QualityColumn is { } qc ? t.Column(qc) : null;
        var bad = samples?.Count(value => double.IsFinite(value) && value != 0) ?? 0;
        return s.Channels.Select(channelMapping =>
        {
            var col = t.Column(channelMapping.SourceColumn).Where(double.IsFinite).ToArray();
            return new ChannelSummary(channelMapping.TargetChannel, channelMapping.Unit, col.Length == 0 ? 0 : col.Min(), col.Length == 0 ? 0 : col.Max(),
                samples is null ? "—" : bad == 0 ? "0 (keine Warnungen)" : $"{bad} Warnung(en)");
        }).ToList();
    }

    // ------------------------------------------------------------------ gemeinsame Tabellenlogik

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Aus Textzeilen (Kopfzeile, optionale Einheitenzeile, Daten) eine Tabelle bilden.</summary>
    internal static ImportTable FromGrid(string format, string name, string sha, List<string[]> grid, CultureInfo culture)
    {
        grid = grid.Where(parts => parts.Any(code => code.Trim().Length > 0)).ToList();
        if (grid.Count == 0) throw new CurveImportException("Datei enthält keine Daten.");
        var width = grid.Max(parts => parts.Length);
        var (header, units, first) = ReadGridHeader(grid, width, culture);

        var rows = grid.Count - first;
        var data = Enumerable.Range(0, width).Select(_ => new double[rows]).ToList();
        for (var rowIndex = 0; rowIndex < rows; rowIndex++)
        {
            var row = grid[first + rowIndex];
            for (var columnIndex = 0; columnIndex < width; columnIndex++)
                data[columnIndex][rowIndex] = columnIndex < row.Length && double.TryParse(row[columnIndex].Trim(), NumberStyles.Float, culture, out var value) ? value : double.NaN;
        }

        var columns = new List<ImportColumn>();
        for (var columnIndex2 = 0; columnIndex2 < width; columnIndex2++)
        {
            var finite = data[columnIndex2].Where(double.IsFinite).ToArray();
            columns.Add(new ImportColumn(columnIndex2, header[columnIndex2], units[columnIndex2], finite.Length > rows / 2, finite.Length == 0 ? 0 : finite.Min(), finite.Length == 0 ? 0 : finite.Max()));
        }

        var (timeCol, factor) = DetectTimeColumn(columns);
        var qualityCol = columns.FirstOrDefault(importColumn => importColumn.IsNumeric && Regex.IsMatch(importColumn.Name, "qual|status|flag", RegexOptions.IgnoreCase))?.Name;

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
        bool IsNum(string text) => double.TryParse(text.Trim(), NumberStyles.Float, culture, out _);
        string[] header;
        string?[] units = new string?[width];
        var first = 0;
        if (!grid[0].Where(code => code.Trim().Length > 0).All(IsNum))
        {
            header = Enumerable.Range(0, width).Select(index => index < grid[0].Length && grid[0][index].Trim().Length > 0 ? grid[0][index].Trim() : $"Spalte {index + 1}").ToArray();
            first = 1;
            if (grid.Count > 1 && grid[1].Where(code => code.Trim().Length > 0).Any() && !grid[1].Where(code => code.Trim().Length > 0).Any(IsNum))
            {
                for (var index = 0; index < width; index++) units[index] = index < grid[1].Length ? grid[1][index].Trim().Trim('[', ']', '(', ')') : null;
                first = 2;
            }
        }
        else header = Enumerable.Range(1, width).Select(index2 => $"Spalte {index2}").ToArray();

        // Einheit im Namen: „U_L1 [V]“, „U_L1 (V)“, „Zeit_s“, „t_ms“
        for (var index2 = 0; index2 < width; index2++)
        {
            var match = Regex.Match(header[index2], @"^(.*?)\s*[\[(]([^\])]+)[\])]\s*$");
            if (match.Success)
            {
                units[index2] ??= match.Groups[2].Value.Trim();
                header[index2] = match.Groups[1].Value.Trim().Length > 0 ? match.Groups[1].Value.Trim() : header[index2];
            }
        }
        if (header.Distinct().Count() != header.Length)
            header = header.Select((hint, index3) => header.Take(index3).Contains(hint) ? $"{hint}_{index3 + 1}" : hint).ToArray();

        return (header, units, first);
    }

    private static (string? Column, double Factor) DetectTimeColumn(List<ImportColumn> columns)
    {
        // Zeitspalte und Einheit erkennen
        string? timeCol = null;
        var factor = 1.0;
        foreach (var importColumn in columns.Where(importColumn2 => importColumn2.IsNumeric))
        {
            var name = importColumn.Name.ToLowerInvariant();
            var unit2 = (importColumn.Unit ?? "").ToLowerInvariant();
            if (IsTimeColumn(name))
            {
                timeCol = importColumn.Name;
                var unit = TimeUnit(unit2, name);
                factor = unit switch { "ms" => 1e-3, "us" or "µs" => 1e-6, "ns" => 1e-9, "min" => 60, _ => 1 };
                if (importColumn.Unit is null) columns[importColumn.Index] = importColumn with { Unit = unit == "us" ? "µs" : unit };
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
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !line.TrimStart().StartsWith('#')).ToList();
        var sample = lines.Where(line => line.Trim().Length > 0).Take(20).ToList();
        var delim = delimiter ?? new[] { ';', '\t', ',', '|' }
            .OrderByDescending(character => sample.Count(sample2 => sample2.Contains(character)) * 100 + (sample.Count > 0 ? sample.Select(sample2 => sample2.Count(ch => ch == character)).Distinct().Count() == 1 ? 10 : 0 : 0))
            .First(character => sample.Any(sample2 => sample2.Contains(character)) || character == '|');
        var dec = decimalSeparator ?? (delim != ',' && sample.Skip(1).Any(skip => Regex.IsMatch(skip, @"\d,\d")) ? ',' : '.');
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = dec.ToString();
        culture.NumberFormat.NumberGroupSeparator = dec == ',' ? "." : ",";
        var grid = lines.Select(line => line.Split(delim).Select(entry => entry.Trim().Trim('"')).ToArray()).ToList();
        var table = CurveImport.FromGrid("CSV/Text", name, CurveImport.Hash(bytes), grid, culture);
        return table;
    }
}
