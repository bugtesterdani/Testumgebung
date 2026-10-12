using System.Globalization;

namespace Testumgebung.Contracts;

/// <summary>Unterstützte Variablentypen. Weitere Typen (Waveform, Arrays) folgen in späteren Etappen.</summary>
public enum VarType
{
    Bool,
    Int,
    Double,
    String,
    DateTime,
}

/// <summary>Fehler bei Typprüfung oder Umwandlung eines Werts.</summary>
public sealed class TypeMismatchException(string message) : Exception(message);

/// <summary>
/// Hilfsfunktionen für typisierte Werte. Intern werden Werte als
/// bool, long, double, string und DateTime gehalten.
/// </summary>
public static class TypedValue
{
    public static object Default(VarType type) => type switch
    {
        VarType.Bool => false,
        VarType.Int => 0L,
        VarType.Double => 0.0,
        VarType.String => string.Empty,
        VarType.DateTime => DateTime.MinValue,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static VarType? TypeOf(object? value) => value switch
    {
        bool => VarType.Bool,
        int or long => VarType.Int,
        double or float => VarType.Double,
        string => VarType.String,
        DateTime => VarType.DateTime,
        _ => null,
    };

    /// <summary>
    /// Wandelt einen Wert in den Zieltyp um. Erlaubt ist nur die verlustfreie Erweiterung Int → Double.
    /// Alles andere erzeugt eine <see cref="TypeMismatchException"/> (keine stillschweigende Umwandlung).
    /// </summary>
    public static object Coerce(object? value, VarType target)
    {
        switch (target)
        {
            case VarType.Bool when value is bool b: return b;
            case VarType.Int when value is long l: return l;
            case VarType.Int when value is int i: return (long)i;
            case VarType.Double when value is double d: return d;
            case VarType.Double when value is float f: return (double)f;
            case VarType.Double when value is long l: return (double)l;
            case VarType.Double when value is int i: return (double)i;
            case VarType.String when value is string s: return s;
            case VarType.DateTime when value is DateTime dt: return dt;
        }
        throw new TypeMismatchException(
            $"Wert '{Format(value)}' ({TypeOf(value)?.ToString() ?? value?.GetType().Name ?? "null"}) passt nicht zu Typ {target}.");
    }

    /// <summary>Text aus einem Eingabefeld (z. B. Debug-Variablenansicht) typgeprüft parsen.</summary>
    public static object Parse(string text, VarType target)
    {
        var inv = CultureInfo.InvariantCulture;
        var de = CultureInfo.GetCultureInfo("de-DE");
        switch (target)
        {
            case VarType.Bool:
                if (bool.TryParse(text, out var b)) return b;
                break;
            case VarType.Int:
                if (long.TryParse(text, NumberStyles.Integer, inv, out var l)) return l;
                break;
            case VarType.Double:
                if (double.TryParse(text, NumberStyles.Float, inv, out var d)) return d;
                if (double.TryParse(text, NumberStyles.Float, de, out d)) return d;
                break;
            case VarType.String:
                return text;
            case VarType.DateTime:
                if (DateTime.TryParse(text, inv, DateTimeStyles.None, out var dt)) return dt;
                if (DateTime.TryParse(text, de, DateTimeStyles.None, out dt)) return dt;
                break;
        }
        throw new TypeMismatchException(target switch
        {
            VarType.Int or VarType.Double => "Wert muss Zahl sein",
            VarType.Bool => "Wert muss true oder false sein",
            VarType.DateTime => "Wert muss Datum/Uhrzeit sein",
            _ => $"Ungültiger Wert für {target}",
        });
    }

    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        double d => d.ToString("G", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
