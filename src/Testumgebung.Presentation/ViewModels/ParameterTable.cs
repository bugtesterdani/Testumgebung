using System.Globalization;
using System.Text.Json;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Presentation.ViewModels;

public sealed record ParameterTable(string[] Columns, IReadOnlyList<string?[]> Rows)
{
    public static ParameterTable Read(ParameterRowViewModel field) => field.Descriptor.Name switch
    {
        "Kanaele" => ReadChannels(field.Owner),
        "Argumente" => new(["Argument"], ReadArguments(field.Value)),
        "Umgebung" => new(["Name", "Wert"], ReadEnvironment(field.Value)),
        "Textregeln" => new(["Text", "Regex", "Prozent", "Status"], ReadRules(field.Value)),
        _ => throw new ArgumentException("Unbekannter Tabellenparameter.")
    };

    public static IReadOnlyDictionary<string, string> Write(ParameterRowViewModel field, IReadOnlyList<string?[]> rows)
    {
        if (field.Descriptor.Name == "Kanaele") return WriteChannels(field.Owner, rows);
        var json = field.Descriptor.Name switch
        {
            "Argumente" => JsonSerializer.Serialize(rows.Select(row => row[0] ?? "")),
            "Umgebung" => JsonSerializer.Serialize(rows.ToDictionary(row => Required(row[0], "Name"), row => row[1] ?? "")),
            "Textregeln" => WriteRules(rows),
            _ => throw new ArgumentException("Unbekannter Tabellenparameter.")
        };
        return new Dictionary<string, string> { [field.Descriptor.Name] = json };
    }

    private static string[][] ReadArguments(string value) =>
        (JsonSerializer.Deserialize<string[]>(value) ?? throw new ArgumentException("Argumentliste fehlt."))
        .Select(argument => new[] { argument ?? throw new ArgumentException("Ein Argument darf nicht null sein.") }).ToArray();

    private static string[][] ReadEnvironment(string value) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? throw new ArgumentException("Umgebungsvariablen fehlen."))
        .Select(pair => new[] { pair.Key, pair.Value ?? throw new ArgumentException("Ein Umgebungswert darf nicht null sein.") }).ToArray();

    private static string?[][] ReadRules(string value)
    {
        var rules = JsonSerializer.Deserialize<LiveProgressParser.Rule[]>(value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("Fortschrittsregeln fehlen.");
        return rules.Select(rule => rule is null ? throw new ArgumentException("Eine Regel darf nicht null sein.")
            : new[] { rule.Text ?? "", rule.Regex ?? "", rule.Percent.ToString(CultureInfo.CurrentCulture), rule.Status }).ToArray();
    }

    private static string WriteRules(IReadOnlyList<string?[]> rows)
    {
        var rules = rows.Select(row => new LiveProgressParser.Rule(
            EmptyToNull(row[0]), EmptyToNull(row[1]), ParsePercent(row[2]), row[3]));
        var json = JsonSerializer.Serialize(rules);
        _ = new LiveProgressParser(json);
        return json;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static double ParsePercent(string? value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var percent) ||
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
        {
            if (double.IsFinite(percent) && percent is >= 0 and <= 100) return percent;
        }
        throw new ArgumentException("Prozent muss eine Zahl zwischen 0 und 100 sein.");
    }

    private static readonly string[] ChannelFields = ["Kanaele", "Einheiten", "Grenzkurven", TraceBlocks.AbortCurvesParameter];

    private static bool UsesCurves(StepPropertiesViewModel owner) =>
        owner.Parameters.Single(row => row.Descriptor.Name == TraceBlocks.EvaluationParameter).Value == TraceBlocks.ValueBand;

    private static ParameterTable ReadChannels(StepPropertiesViewModel owner)
    {
        string[] columns = ["Kanalname", "Einheit", "Grenzkurve (ID@Version)", "Abbruchgrenzkurve (optional)"];
        var values = ChannelFields.Take(columns.Length).Select(name =>
            owner.Parameters.Single(row => row.Descriptor.Name == name).Value.Split(',', StringSplitOptions.TrimEntries)).ToArray();
        return new(columns, Enumerable.Range(0, values.Max(column => column.Length))
            .Select(index => values.Select(column => column.ElementAtOrDefault(index) ?? "").ToArray()).ToArray());
    }

    private static Dictionary<string, string> WriteChannels(StepPropertiesViewModel owner, IReadOnlyList<string?[]> rows)
    {
        if (rows.Count is < 1 or > 8) throw new ArgumentException("1 bis 8 Kanäle angeben.");
        var requireCurves = UsesCurves(owner);
        foreach (var row in rows)
        {
            Required(row[0], "Kanalname");
            if (requireCurves) Required(row[2], "Grenzkurve");
            if (row.Any(cell => cell?.Contains(',') == true)) throw new ArgumentException("Kanalspalten dürfen kein Komma enthalten.");
        }
        if (rows.Select(row => row[0]?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new ArgumentException("Kanalnamen müssen eindeutig sein.");
        return Enumerable.Range(0, ChannelFields.Length).ToDictionary(index => ChannelFields[index],
            index => string.Join(",", rows.Select(row => row[index]?.Trim())));
    }

    private static string Required(string? value, string label) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{label} ist erforderlich.") : value;
}
