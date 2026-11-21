using System.Globalization;

namespace Testumgebung.Domain.Labels.CabJScript;

internal sealed partial class CabPreviewContext
{
    private string Math(CabContentField field, int id, IReadOnlyList<string> args)
    {
        if (id == 54) return "";
        if (id is >= 50 and <= 52) return CheckDigit(id, Resolve(args[0]).Text);
        if (id == 53) return Price(field, args);
        if (id == 49)
        {
            RequireText(Resolve(args[0])); RequireText(Resolve(args[1]));
            Note(field, "Vorschau vergleicht Zeichenfolgen ordinal und mit Groß-/Kleinschreibung.");
            return Resolve(args[0]).Text == Resolve(args[1]).Text ? "1" : "0";
        }
        var numbers = args.Select(Number).ToArray();
        if (id is >= 44 and <= 48) return Compare(field, id, numbers[0], numbers[1]) ? "1" : "0";
        if (numbers.Length > 2) Note(field, "Vorschau wertet mehr als zwei Operanden von links nach rechts aus.");
        var value = numbers[0];
        foreach (var number in numbers.Skip(1)) value = id switch
        {
            39 => value + number, 40 => value - number, 41 => value * number,
            42 => value / number, 43 => value % number, _ => throw new InvalidOperationException()
        };
        return Format(value);
    }

    private bool Compare(CabContentField field, int id, decimal left, decimal right)
    {
        if (id == 45) Note(field, "Handbuchwiderspruch: Vorschau verwendet logisches AND gemäß Überschrift, nicht Gleichheit.");
        return id switch { 44 => left != 0 || right != 0, 45 => left != 0 && right != 0,
            46 => left < right, 47 => left > right, 48 => left == right, _ => false };
    }

    private static string CheckDigit(int id, string value)
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
        var allowed = id == 50 ? alphabet[..10] : id == 51 ? alphabet[..36] : alphabet;
        if (value.Length == 0 || value.Any(c => !allowed.Contains(c))) throw new FormatException("Zeichen außerhalb des Prüfziffernalphabets.");
        var total = 0;
        for (var i = 0; i < value.Length; i++)
            total += allowed.IndexOf(value[value.Length - 1 - i]) * (id == 50 && i % 2 == 0 ? 3 : 1);
        return id == 50 ? ((10 - total % 10) % 10).ToString(CultureInfo.InvariantCulture) : allowed[total % allowed.Length].ToString();
    }

    private string Price(CabContentField field, IReadOnlyList<string> args)
    {
        var value = Quantize(Number(args[0]), 2);
        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.NumberGroupSeparator = args[1][..1]; format.NumberDecimalSeparator = args[1].Substring(1, 1);
        var result = value.ToString("N2", format);
        if (args[1].Length == 3 && value == decimal.Truncate(value)) result = result[..^2] + args[1][2];
        Note(field, "Preisvorschau verwendet zwei Nachkommastellen; Zusatzzeichen ersetzt bei ganzen Preisen die Nachkommastellen.");
        return result;
    }
}
