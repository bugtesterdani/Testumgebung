using System.Globalization;
using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

internal sealed partial class CabPreviewContext
{
    internal void Configure(IReadOnlyList<CabContentField> fields)
    {
        foreach (var field in fields)
        {
            if (field.Nested || CabContentGrammar.Errors(field, Names).Count > 0) continue;
            try { ConfigureOne(field); }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException or InvalidOperationException)
            { _invalidModifiers.Add(field.Offset); Note(field, error.Message, "preview-value"); }
        }
    }

    private void ConfigureOne(CabContentField field)
    {
        var args = CabContentGrammar.Arguments(field);
        switch (CabContentGrammar.Number(field))
        {
            case 54: _rounding = args[0]; Note(field, "Vorschau: u/d verwenden Auf-/Abrunden gegen ±∞; m rundet halbe Werte von null weg."); break;
            case 77:
                if (args[0].Length != 1) throw new FormatException("Vorschau benötigt genau ein Füllzeichen; Mehrzeichenverhalten ist nicht spezifiziert.");
                _fill = args[0][0];
                if (args.Count > 1) _base = Integer(args[1]);
                break;
            case 78:
                _width = Integer(args[0]); _digits = Integer(args[1]);
                if (_digits is < 0 or > 28 || _width is < 0 or > 4096) throw new FormatException("Stellenzahl überschreitet die lokale Vorschaugrenze.");
                Note(field, "Vorschau: m zählt Gesamtziffern; Überbreite wird erhalten, fehlende Ziffern werden links aufgefüllt.");
                break;
            case 81: Visibility(field, args); break;
            case 82:
                Alignment = args[0][..1]; AlignmentLength = decimal.Parse(args[0].AsSpan(1), CultureInfo.InvariantCulture); break;
            case 95: _style = args[0]; break;
        }
    }

    private void Visibility(CabContentField field, IReadOnlyList<string> args)
    {
        if (args.Count == 0) { Hidden = true; return; }
        var inverted = args[0].StartsWith('!');
        var truth = Number(inverted ? args[0][1..] : args[0]) != 0;
        if (inverted) truth = !truth;
        Hidden = settings.VisibilityPolicy == CabPreviewVisibilityPolicy.ExampleHidesWhenTrue ? truth : !truth;
        Note(field, "Handbuchwiderspruch bei I: Vorschau verwendet die ausdrücklich gewählte Sichtbarkeitsregel.");
    }

    private string Format(decimal value)
    {
        var scale = Pow10(_digits);
        var scaled = value * scale;
        var rounded = _rounding switch
        {
            "u" => decimal.Ceiling(scaled), "d" => decimal.Floor(scaled),
            "m" => decimal.Round(scaled, 0, MidpointRounding.AwayFromZero), _ => decimal.Truncate(scaled)
        } / scale;
        var result = rounded.ToString("F" + _digits, Culture);
        var integerWidth = System.Math.Max(0, _width - _digits);
        var separator = result.IndexOf(Culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);
        var actual = separator < 0 ? result.Length : separator;
        if (actual < integerWidth) result = new string(_fill, integerWidth - actual) + result;
        return result;
    }

    private static decimal Pow10(int digits)
    {
        var result = 1m;
        for (var i = 0; i < digits; i++) result *= 10;
        return result;
    }

    private string FillLeadingZeros(string text)
    {
        if (_fill == '0') return text;
        var count = 0;
        // Keep the final zero for a value consisting entirely of zeroes.
        while (count < text.Length - 1 && text[count] == '0') count++;
        return new string(_fill, count) + text[count..];
    }

    internal string Digits(string text)
    {
        if (barcode || _style == "LATIN") return text;
        var alphabet = _style == "THAI" ? "๐๑๒๓๔๕๖๗๘๙" : "٠١٢٣٤٥٦٧٨٩";
        var result = new StringBuilder(text.Length);
        foreach (var c in text) result.Append(c is >= '0' and <= '9' ? alphabet[c - '0'] : c);
        return result.ToString();
    }
}
