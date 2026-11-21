using System.Globalization;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Argument delimiters are function-specific: text tails are never split indiscriminately.</summary>
public static class CabContentGrammar
{
    private static readonly HashSet<int> TextTails = [67, 81, 82, 85, 86, 87, 90, 91, 93, 100, 101, 102, 103, 104];
    public static IReadOnlyList<string> Arguments(CabContentField field)
    {
        if (field.Argument is null) return [];
        var id = Number(field);
        if (id is 53) return field.Argument.Split(',', 2);
        if (id is 76) return field.Argument.Split(':');
        if (TextTails.Contains(id)) return [field.Argument];
        return field.Argument.Split(',');
    }

    public static IReadOnlyList<string> Errors(CabContentField field, IReadOnlySet<string>? previousNames = null)
    {
        var errors = new List<string>();
        var args = Arguments(field);
        var id = Number(field);
        var (minimum, maximum) = Arity(id);
        if (args.Count < minimum || args.Count > maximum)
            errors.Add($"Erwartete Parameterzahl: {minimum}…{(maximum == int.MaxValue ? "beliebig" : maximum)}.");
        if (args.Any(a => a.Length == 0) && id is not (63 or 65 or 66 or 97))
            errors.Add("Leerer Parameter ist hier nicht dokumentiert.");
        SpecialChecks(id, args, errors);
        BoundChecks(field, previousNames, errors);
        CabContentValueRules.Check(id, args, previousNames, errors);
        if (field.Name.Contains('-', StringComparison.Ordinal) && id is 90 or 91
            && !int.TryParse(field.Name[(field.Name.IndexOf('-') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            errors.Add("Ungültige OPC-UA-Verbindungskennung.");
        return errors.AsReadOnly();
    }

    private static void BoundChecks(CabContentField field, IReadOnlySet<string>? names, List<string> errors)
    {
        foreach (var argument in CabContentArguments.Bind(field))
        {
            var rule = CabDefinitions.ParameterRule(field.Definition.Id, argument.Parameter.Name);
            if (argument.Value.Length == 0) continue;
            if (names?.Contains(argument.Value) == true && (rule.DataType is CabDataType.Number or CabDataType.Integer
                || Number(field) is >= 50 and <= 52)) continue;
            var error = rule.Validate(argument.Value);
            if (error is not null) errors.Add(argument.Parameter.Name + ": " + error);
        }
    }

    internal static int Number(CabContentField field) => int.Parse(field.Definition.Section.AsSpan(2), CultureInfo.InvariantCulture);

    private static readonly IReadOnlyDictionary<int, (int Minimum, int Maximum)> Arities = BuildArities();

    private static (int Minimum, int Maximum) Arity(int id) => Arities.GetValueOrDefault(id, (1, 1));

    private static IReadOnlyDictionary<int, (int Minimum, int Maximum)> BuildArities()
    {
        var result = new Dictionary<int, (int, int)>();
        Set(Enumerable.Range(1, 38), 0, 3);
        Set([23], 1, 1);
        Set(Enumerable.Range(39, 5), 2, int.MaxValue);
        Set(Enumerable.Range(44, 6), 2, 2);
        Set([53, 56, 59, 60, 76, 78], 2, 2);
        Set([57, 58, 61, 84, 88, 105, 106, 107, 108], 0, 0);
        Set([62, 64, 81, 92, 94], 0, 1);
        Set([63], 0, 3);
        Set([65, 97], 1, 3);
        Set([66], 1, 8);
        Set([68, 79], 3, 3);
        Set([69, 70, 71, 72, 73, 74, 75, 80], 1, int.MaxValue);
        Set([77, 89, 98], 1, 2);
        Set([96], 4, 7);
        Set([99], 2, 3);
        return result;
        void Set(IEnumerable<int> ids, int minimum, int maximum)
        {
            foreach (var id in ids) result[id] = (minimum, maximum);
        }
    }

    private static void SpecialChecks(int id, IReadOnlyList<string> args, List<string> errors)
    {
        if (args.Count == 0) return;
        FormatChecks(id, args, errors);
        OptionChecks(id, args, errors);
        if (id is 65 or 97 && args[0].Length == 0) errors.Add("Startparameter darf nicht leer sein.");
    }

    private static void FormatChecks(int id, IReadOnlyList<string> args, List<string> errors)
    {
        if (id == 53 && args.Count > 1 && args[1].Length is not (2 or 3)) errors.Add("Preisformat erwartet zwei Trenner und optional ein Zusatzzeichen.");
        if (id == 82 && args[0].Length < 2) errors.Add("Ausrichtung erwartet l/c/r unmittelbar gefolgt von einer Länge.");
    }

    private static void OptionChecks(int id, IReadOnlyList<string> args, List<string> errors)
    {
        if (id == 96 && args.Skip(4).Any(a => a is not ("D" or "R" or "J"))) errors.Add("SELECT-Option erwartet D, R oder J.");
        if (id == 98 && args.Count > 1 && !args[1].StartsWith("C:", StringComparison.Ordinal)) errors.Add("Zeichensatzoption erwartet C:charset.");
    }
}
