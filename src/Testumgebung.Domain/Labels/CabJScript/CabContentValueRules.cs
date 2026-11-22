using System.Globalization;
using System.Numerics;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabContentValueRules
{
    private static readonly IReadOnlyDictionary<int, int> BinaryWidths = new Dictionary<int, int>
    { [69]=8, [70]=16, [71]=16, [72]=32, [73]=32, [74]=64, [75]=64 };
    internal static void Check(int id, IReadOnlyList<string> args, IReadOnlySet<string>? names, List<string> errors, bool barcode)
    {
        if (BinaryWidths.TryGetValue(id, out var bits))
        {
            foreach (var arg in args.Where(a => names?.Contains(a) != true)) CheckUnsigned(arg, bits, errors);
        }
        if (id == 68)
            foreach (var arg in args.Take(2)) CheckAlphabet(arg, errors);
        if (id == 76 && args.Count == 2) CheckBits(args, names, errors);
        if (id == 103 && args.Count > 0) CheckUnicode(args[0], errors, barcode);
        if (id == 66) CheckPrompt(args, errors);
    }

    private static void CheckUnsigned(string value, int bits, List<string> errors)
    {
        value = value.Trim(' ', '\t');
        var hex = value.StartsWith('$');
        var success = BigInteger.TryParse(hex ? "0" + value[1..] : value,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var number);
        if (hex && value.Length == 1 || !success || number < 0 || number >= BigInteger.One << bits)
            errors.Add($"Binärwert muss eine vorzeichenlose {bits}-Bit-Zahl sein (dezimal oder $hex).");
    }

    private static void CheckAlphabet(string arg, List<string> errors)
    {
        if (arg is "B" or "O" or "D" or "H" or "A") return;
        if (!arg.StartsWith("U:", StringComparison.Ordinal) || arg.Length < 4 || arg[2..].Distinct().Count() != arg.Length - 2)
            errors.Add("Basis erwartet B/O/D/H/A oder U: mit mindestens zwei unterschiedlichen Zeichen.");
    }

    private static void CheckBits(IReadOnlyList<string> args, IReadOnlySet<string>? names, List<string> errors)
    {
        var widths = args[0].Split(','); var values = args[1].Split(',');
        if (widths.Length != values.Length) errors.Add("BITFIELD benötigt gleich viele Breiten und Werte.");
        for (var index = 0; index < System.Math.Min(widths.Length, values.Length); index++)
        {
            if (!int.TryParse(widths[index], NumberStyles.None, CultureInfo.InvariantCulture, out var width) || width is < 1 or > 32)
            { errors.Add("BITFIELD-Breite erwartet 1…32."); continue; }
            if (names?.Contains(values[index]) != true) CheckUnsigned(values[index], width, errors);
        }
    }

    private static void CheckUnicode(string value, List<string> errors, bool barcode)
    {
        var parameter = barcode ? CabCatalog.Default["JS-4.2"].Parameters.Single(p => p.Name == "U.xxxx") : CabCatalog.Default["JS-5.103"].Parameters[0];
        if (parameter.Values.Any(v => v.Value == value)) return;
        if (!CabUnicodeScalar.TryParse(value, barcode, out _)) errors.Add("Unicode erwartet einen gültigen Skalarwert oder dokumentierten Steuerzeichennamen.");
    }

    private static void CheckPrompt(IReadOnlyList<string> args, List<string> errors)
    {
        foreach (var option in args.Skip(3))
        {
            if (option.Length == 0 || option is "D" or "R" or "J" || option.StartsWith('L') || option.StartsWith('M')) continue;
            errors.Add("Unbekannte Display-Abfrageoption.");
        }
    }
}
