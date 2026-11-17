using System.Text.RegularExpressions;

namespace Testumgebung.Engine.Blocks;

public sealed record Gs1ApplicationIdentifier(string Id, string Meaning, int Length, bool Numeric, bool Variable = false)
{
    public string Display => $"({Id}) {Meaning} · {(Variable ? $"1–{Length}" : Length.ToString(System.Globalization.CultureInfo.InvariantCulture))} Zeichen";
}

/// <summary>Common GS1 Application Identifiers and barcode data syntax shared by the designers.</summary>
public static class Gs1LabelData
{
    public static IReadOnlyList<Gs1ApplicationIdentifier> Identifiers { get; } =
    [
        new("00", "SSCC / Nummer der Versandeinheit", 18, true),
        new("01", "GTIN / Artikelnummer", 14, true),
        new("02", "GTIN des enthaltenen Artikels", 14, true),
        new("10", "Charge / Los", 20, false, true),
        new("11", "Produktionsdatum (JJMMTT)", 6, true),
        new("13", "Verpackungsdatum (JJMMTT)", 6, true),
        new("15", "Mindesthaltbarkeitsdatum (JJMMTT)", 6, true),
        new("17", "Verfallsdatum (JJMMTT)", 6, true),
        new("20", "Interne Produktvariante", 2, true),
        new("21", "Seriennummer", 20, false, true),
        new("22", "Produktvariante", 20, false, true),
        new("30", "Variable Stückzahl", 8, true, true),
        new("37", "Anzahl enthaltener Artikel", 8, true, true),
        new("240", "Zusätzliche Artikelidentifikation des Herstellers", 30, false, true),
        new("241", "Artikelnummer des Kunden", 30, false, true),
        new("400", "Auftragsnummer des Kunden", 30, false, true),
        new("410", "GLN des Warenempfängers", 13, true),
        new("414", "GLN eines physischen Standorts", 13, true),
        new("420", "Postleitzahl des Lieferorts", 20, false, true),
    ];

    public const string ZplLiteralMatrixMarker = "@TU-DM@";
    public const string ZplMatrixMarker = "@TU-GS1-DM@";
    public const string ZplQrMarker = "@TU-GS1-QR@";
    private static readonly Regex Ai = new(@"\((\d{2,4})\)", RegexOptions.CultureInvariant);
    private static readonly Regex ZplFields = new(@"\^FD@TU-(DM|GS1-DM|GS1-QR)@([^\^]*)\^FS", RegexOptions.CultureInvariant);
    private const string SpecialCharacters = "!\"%&'()*+,-./:;<=>?_";

    private static List<(Gs1ApplicationIdentifier Identifier, string Value)> Parse(string input)
    {
        var matches = Ai.Matches(input);
        if (matches.Count == 0 || matches[0].Index != 0)
            throw new FormatException("GS1 erwartet einen Inhalt wie (01)09506000134376(21)SN-123.");

        var fields = new List<(Gs1ApplicationIdentifier, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var identifier = Identifiers.FirstOrDefault(entry => entry.Id == match.Groups[1].Value)
                ?? throw new FormatException($"GS1-Kennung {match.Value} wird in diesem Designer nicht unterstützt.");
            var end = i + 1 < matches.Count ? matches[i + 1].Index : input.Length;
            var value = input[(match.Index + match.Length)..end];
            ValidateValue(identifier, value);
            fields.Add((identifier, value));
        }
        return fields;
    }

    private static void ValidateValue(Gs1ApplicationIdentifier identifier, string value)
    {
        if (value.Length == 0 || value.Length > identifier.Length || (!identifier.Variable && value.Length != identifier.Length))
            throw new FormatException($"GS1-Feld ({identifier.Id}) hat die falsche Länge. {identifier.Display}.");
        if (!value.All(c => Allowed(c, identifier.Numeric)))
            throw new FormatException($"GS1-Feld ({identifier.Id}) enthält ein unzulässiges Zeichen.");
    }

    private static bool Allowed(char c, bool numeric) => numeric ? char.IsAsciiDigit(c)
        : char.IsAsciiLetterOrDigit(c) || SpecialCharacters.Contains(c);

    public static string Cab(string input)
    {
        _ = Parse(input);
        return input;
    }

    public static string RenderZpl(string zpl) => ZplFields.Replace(zpl, match =>
    {
        var format = match.Groups[1].Value;
        var value = match.Groups[2].Value;
        var field = format == "DM" ? EscapeMatrixValue(value) : ZplData(value, format == "GS1-QR");
        return "^FD" + field + "^FS";
    });

    private static string ZplData(string input, bool qr)
    {
        var fields = Parse(input);
        var separator = qr ? ">8" : "_1";
        var result = new System.Text.StringBuilder(qr ? ">;>8" : separator);
        for (var i = 0; i < fields.Count; i++)
        {
            var (identifier, value) = fields[i];
            result.Append(identifier.Id).Append(EscapeZplValue(value, qr));
            if (identifier.Variable && i + 1 < fields.Count)
                result.Append(separator);
        }
        return result.ToString();
    }

    private static string EscapeMatrixValue(string value) => value.Replace("_", "__", StringComparison.Ordinal);

    private static string EscapeZplValue(string value, bool qr)
    {
        if (!qr) return EscapeMatrixValue(value);
        if (value.Contains('>')) throw new FormatException("Das Zeichen > ist im Zebra GS1-QR ein Steuerzeichen.");
        return value;
    }
}
