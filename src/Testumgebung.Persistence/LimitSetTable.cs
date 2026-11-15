using Microsoft.VisualBasic.FileIO;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Persistence.Curves;

namespace Testumgebung.Persistence;

/// <summary>Importiert eine Grenzwerttabelle und löst die Variantenwahl in typisierte Projektvariablen auf.</summary>
public static class LimitSetTable
{
    public const string VariantParameter = "Variante";

    public static SortedDictionary<string, SortedDictionary<string, string>> Import(TestProject project, string path)
    {
        var rows = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => Xlsx.ReadGrid(File.ReadAllBytes(path)),
            ".csv" => ReadCsv(path),
            _ => throw new ProjectFormatException("Grenzwerttabellen als .csv oder .xlsx importieren."),
        };
        var grid = rows.Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell))).ToArray();
        if (grid.Length < 2)
            throw new ProjectFormatException("Die Grenzwerttabelle benötigt eine Kopfzeile und mindestens eine Variante.");
        var header = grid[0].Select(cell => cell.Trim()).ToArray();
        if (header.Length < 2 || header[0] != VariantParameter || header.Any(string.IsNullOrEmpty)
            || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            throw new ProjectFormatException("Erste Spalte: Variante. Weitere Spalten: eindeutige Namen der Prüfvariablen.");

        var types = TargetTypes(project);
        var sets = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in grid.Skip(1))
        {
            var name = row[0].Trim();
            if (name.Length == 0 || name.Contains(';') || row.Length != header.Length)
                throw new ProjectFormatException("Jede Zeile benötigt einen Variantennamen ohne Semikolon und einen Wert je Prüfvariable.");
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var column = 1; column < header.Length; column++)
            {
                var value = row[column].Trim();
                ParseValue(types, name, header[column], value);
                values.Add(header[column], value);
            }
            if (!sets.TryAdd(name, values))
                throw new ProjectFormatException($"Variante „{name}“ ist mehrfach vorhanden.");
        }
        return sets;
    }

    public static IReadOnlyDictionary<string, object?> Resolve(TestProject project, string? variant)
    {
        if (project.LimitSets.Count == 0 && variant is null)
            return new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(variant))
            throw new ProjectFormatException("Kein Grenzwertsatz gewählt. Im Prüfparameter Variante=<Name> angeben.");
        if (!project.LimitSets.TryGetValue(variant, out var values))
            throw new ProjectFormatException($"Grenzwertsatz „{variant}“ fehlt im gewählten Projektstand.");
        if (values.Count == 0)
            throw new ProjectFormatException($"Grenzwertsatz „{variant}“ enthält keine Werte.");
        var types = TargetTypes(project);
        return values.ToDictionary(entry => entry.Key, entry => (object?)ParseValue(types, variant, entry.Key, entry.Value),
            StringComparer.Ordinal);
    }

    internal static bool SameBindings(TestProject current, TestProject next)
    {
        if (current.LimitSets.Count == 0) return true;
        var names = current.LimitSets.Values.SelectMany(values => values.Keys).ToHashSet(StringComparer.Ordinal);
        return Bindings(current).SequenceEqual(Bindings(next));

        IEnumerable<(string Group, string Variable, VarType Type)> Bindings(TestProject project)
            => DutGroups(project).Prepend(project.Definition.Root)
                .SelectMany(group => group.Variables.Where(variable => names.Contains(variable.Name))
                    .Select(variable => (Group: group.Id, Variable: variable.Name, variable.Type)))
                .OrderBy(binding => binding.Group, StringComparer.Ordinal)
                .ThenBy(binding => binding.Variable, StringComparer.Ordinal);
    }

    private static IEnumerable<GroupDefinition> DutGroups(TestProject project)
        => project.Definition.Loop.Descendants().OfType<GroupDefinition>().Where(group => group.IsDutRun);

    private static Dictionary<string, VarType[]> TargetTypes(TestProject project)
    {
        var duts = DutGroups(project).ToArray();
        var reserved = duts.Select(group => group.DutIdVariable).ToHashSet(StringComparer.Ordinal);
        reserved.UnionWith([VariantParameter, "Kennung", "Auftrag", "Pruefseite"]);
        return project.Definition.Root.Variables.Concat(duts.SelectMany(group => group.Variables))
            .Where(variable => !reserved.Contains(variable.Name))
            .GroupBy(variable => variable.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(variable => variable.Type).Distinct().ToArray(), StringComparer.Ordinal);
    }

    private static object ParseValue(Dictionary<string, VarType[]> types, string variant, string variable, string value)
    {
        if (!types.TryGetValue(variable, out var targets) || targets.Length != 1)
            throw new ProjectFormatException($"„{variable}“ muss als Prüfvariable im Hauptablauf oder Prüfling mit eindeutigem Typ vorhanden sein.");
        try
        {
            if (string.IsNullOrWhiteSpace(value)) throw new TypeMismatchException("Wert fehlt");
            var parsed = TypedValue.Parse(value, targets[0]);
            if (parsed is double number && !double.IsFinite(number)) throw new TypeMismatchException("Endliche Zahl erforderlich");
            return parsed;
        }
        catch (TypeMismatchException ex)
        {
            throw new ProjectFormatException($"Variante „{variant}“, Variable „{variable}“: {ex.Message}");
        }
    }

    private static List<string[]> ReadCsv(string path)
    {
        using var parser = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true };
        string? header;
        do { header = parser.ReadLine(); } while (header is not null && string.IsNullOrWhiteSpace(header));
        if (header is null) return [];
        var separator = header.Contains(';') ? ";" : header.Contains('\t') ? "\t" : ",";
        using var csv = new TextFieldParser(path) { HasFieldsEnclosedInQuotes = true };
        csv.SetDelimiters(separator);
        var rows = new List<string[]>();
        try
        {
            while (csv.ReadFields() is { } fields) rows.Add(fields);
        }
        catch (MalformedLineException ex)
        {
            throw new ProjectFormatException($"Ungültige CSV-Zeile {ex.LineNumber}: {ex.Message}");
        }
        return rows;
    }
}
