/**
 * @file
 * @brief Liest tabellarische Excel-Daten für den Kurvenimport ohne eine laufende Excel-Anwendung.
 */

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Curves;

/// <summary>
/// Excel-Arbeitsmappe (.xlsx, Office Open XML) ohne Zusatzbibliothek: erstes bzw. benanntes Tabellenblatt,
/// gemeinsame Zeichenfolgen, Inline-Text, Zahlen (invariant gespeichert). Formeln werden mit ihrem gespeicherten Wert gelesen.
/// </summary>
public static class Xlsx
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static ImportTable Read(string path, string? sheet = null) => Parse(File.ReadAllBytes(path), Path.GetFileName(path), sheet);

    public static IReadOnlyList<string> SheetNames(byte[] bytes)
    {
        using var zip = Open(bytes);
        return Load(zip, "xl/workbook.xml").Descendants(S + "sheet").Select(element => (string?)element.Attribute("name") ?? "").ToList();
    }

    public static ImportTable Parse(byte[] bytes, string name, string? sheetName = null)
    {
        using var zip = Open(bytes);
        var workbook = Load(zip, "xl/workbook.xml");
        var sheets = workbook.Descendants(S + "sheet").ToList();
        if (sheets.Count == 0) throw new CurveImportException("Arbeitsmappe enthält kein Tabellenblatt.");
        var sheet = sheetName is null ? sheets[0] : sheets.FirstOrDefault(element => (string?)element.Attribute("name") == sheetName)
            ?? throw new CurveImportException($"Tabellenblatt '{sheetName}' nicht gefunden.");
        var relId = (string?)sheet.Attribute(R + "id");
        var rels = Load(zip, "xl/_rels/workbook.xml.rels");
        var target = rels.Descendants(Rel + "Relationship").FirstOrDefault(element => (string?)element.Attribute("Id") == relId)?.Attribute("Target")?.Value
            ?? throw new CurveImportException("Tabellenblatt-Verweis fehlt.");
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] :
            Load(zip, "xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(element => element.Value))).ToList();

        var rows = ReadRows(zip, sheetPath, shared);
        if (rows.Count == 0) throw new CurveImportException("Tabellenblatt ist leer.");
        var width = rows.Values.Where(entries => entries.Count > 0).Select(entries => entries.Keys.Max() + 1).DefaultIfEmpty(0).Max();
        var grid = rows.Values.Select(entries => Enumerable.Range(0, width).Select(index => entries.TryGetValue(index, out var text) ? text : "").ToArray()).ToList();
        var table = CurveImport.FromGrid("Excel (.xlsx)", name + (sheetName is null ? "" : $" [{sheetName}]"), CurveImport.Hash(bytes), grid, CultureInfo.InvariantCulture);
        return table;
    }

    private static SortedDictionary<int, Dictionary<int, string>> ReadRows(ZipArchive zip, string sheetPath, List<string> shared)
    {
        var rows = new SortedDictionary<int, Dictionary<int, string>>();
        foreach (var row in Load(zip, sheetPath).Descendants(S + "row"))
        {
            var rowIndex = (int?)row.Attribute("r") ?? (rows.Count == 0 ? 1 : rows.Keys.Max() + 1);
            var cells = new Dictionary<int, string>();
            var next = 0;
            foreach (var element in row.Elements(S + "c"))
            {
                var col = (string?)element.Attribute("r") is { } resource ? ColumnIndex(resource) : next;
                next = col + 1;
                cells[col] = CellText(element, shared);
            }
            rows[rowIndex] = cells;
        }
        return rows;
    }

    private static string CellText(XElement element2, List<string> shared)
    {
        var type = (string?)element2.Attribute("t");
        var value = element2.Element(S + "v")?.Value;
        return type switch
        {
            "s" => value is not null && int.TryParse(value, out var si) && si >= 0 && si < shared.Count ? shared[si] : "",
            "inlineStr" => string.Concat(element2.Descendants(S + "t").Select(element => element.Value)),
            "b" => value == "1" ? "1" : "0",
            _ => value ?? "",
        };
    }

    private static ZipArchive Open(byte[] bytes)
    {
        try { return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
        catch (InvalidDataException ex) { throw new CurveImportException("Keine gültige .xlsx-Datei.", ex); }
    }

    private static XDocument Load(ZipArchive zip, string path)
    {
        var zipArchiveEntry = zip.GetEntry(path) ?? throw new CurveImportException($"'{path}' fehlt in der Arbeitsmappe.");
        using var stream = zipArchiveEntry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>„C12“ → 2 (nullbasiert).</summary>
    private static int ColumnIndex(string reference)
    {
        var letters = Regex.Match(reference, "^[A-Za-z]+").Value.ToUpperInvariant();
        var count = 0;
        foreach (var ch in letters) count = count * 26 + (ch - 'A' + 1);
        return count - 1;
    }
}

/// <summary>Kurven aus Ergebnisdateien (.tres): je Kurvengruppe (ein Schritt, gemeinsame Zeitbasis) eine Importquelle.</summary>
public static class ResultFileSource
{
    public sealed record WaveformSource(string Title, long EventSequence, string Path, string DutId, string RunId, IReadOnlyList<ResultWaveform> Waveforms);

    public static IReadOnlyList<WaveformSource> List(ResultFileContent content)
    {
        var run = content.Run;
        return run.Waveforms.GroupBy(waveform => waveform.EventSequence).Select(group =>
        {
            var ev = run.Events.FirstOrDefault(resultEvent => resultEvent.Sequence == group.Key);
            var path = ev?.Path ?? $"Ereignis {group.Key}";
            var step = path.Split(" > ").Last();
            return new WaveformSource($"{step} – {string.Join(", ", group.Select(waveform => waveform.Data.Channel))}", group.Key, path, run.DutId, run.RunId, group.ToList());
        }).ToList();
    }

    public static ImportTable ReadFirst(string path)
    {
        var content = ResultFile.Read(path);
        var first = List(content).FirstOrDefault() ?? throw new CurveImportException("Ergebnisdatei enthält keine Kurven.");
        return ToTable(content, first, Path.GetFileName(path));
    }

    /// <summary>Kurvengruppe als Importtabelle (feste Abtastrate der Aufzeichnung). Die Ergebnisdatei bleibt unverändert.</summary>
    public static ImportTable ToTable(ResultFileContent content, WaveformSource source, string fileName)
    {
        var waves = source.Waveforms.Select(waveform => waveform.Data).ToList();
        var rates = waves.Select(waveform => waveform.SampleRate).Distinct().ToList();
        if (rates.Count != 1 || waves.Select(waveform => waveform.Count).Distinct().Count() != 1)
            throw new CurveImportException("Kurven der Gruppe haben keine gemeinsame Zeitbasis.");
        var columns = waves.Select((waveform, index) => new ImportColumn(index, waveform.Channel, waveform.Unit, true, waveform.Samples.DefaultIfEmpty().Min(), waveform.Samples.DefaultIfEmpty().Max())).ToList();
        var table = new ImportTable
        {
            Format = "Ergebnisdatei",
            SourceName = $"{fileName} · {source.DutId} · {source.Path}",
            Sha256 = CurveImport.Hash(System.Text.Encoding.UTF8.GetBytes(content.Run.RunId + "|" + source.EventSequence + "|" +
                string.Join("|", waves.Select(waveform => waveform.Channel + ":" + string.Join(",", waveform.Samples.Select(sample => sample.ToString("R", CultureInfo.InvariantCulture))))))),
            Columns = columns,
            Data = waves.Select(waveform => waveform.Samples).ToList(),
            PreviewRows = Enumerable.Range(0, Math.Min(10, waves[0].Count)).Select(sampleIndex => waves.Select(waveform => waveform.Samples[sampleIndex].ToString("0.###", CultureInfo.InvariantCulture)).ToArray()).ToList(),
            SampleRate = rates[0],
        };
        if (waves.Any(waveform => waveform.Quality is { Length: > 0 } text && text != "simuliert")) table.Warnings.Add("Qualitätshinweise der Aufzeichnung: " + string.Join(", ", waves.Select(waveform => waveform.Quality).Distinct()));
        return table;
    }
}
