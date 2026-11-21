using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabBarcodeRules
{
    private const string Digits = "^[0-9]+$";
    private static readonly HashSet<int> Numeric = [3, 4, 5, 13, 15, 16, 22, 23, 24, 25, 26, 32, 36, 39, 40, 43, 44, 45];
    private static readonly HashSet<int> Composite = [20, 21, 22, 23, 24, 25, 26];
    private static readonly HashSet<int> Ascii = [8, 9, 10, 11, 17, 19, 31, 34, 35];
    private static readonly IReadOnlyDictionary<int, string> CharacterPatterns = new Dictionary<int, string>
    {
        [7] = @"^[0-9ABCD\-$:/\.+]+$", [9] = @"^[A-Z0-9 $/+%.\-]+$",
        [18] = "^[ABCD]$", [30] = @"^[A-Z0-9 $/+%.\-]+$", [38] = "^[A-F0-9]+$",
    };
    private static readonly IReadOnlyDictionary<int, int[]> Lengths = new Dictionary<int, int[]>
    { [4] = [2], [5] = [5], [13] = [11, 13], [15] = [7], [16] = [12], [18] = [1], [22] = [14], [23] = [14], [24] = [14], [25] = [14], [26] = [14], [32] = [13], [40] = [7], [43] = [11], [44] = [7] };

    internal static void ValidateJob(IEnumerable<CabBoundCommand> commands, CabDiagnosticCollector diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            Validate(command, names, diagnostics);
            if (CabDocumentRules.Declaration(command)?.Value is { Length: > 0 } name) names.Add(name);
        }
    }

    private static void Validate(CabBoundCommand command, IReadOnlySet<string> names, CabDiagnosticCollector diagnostics)
    {
        var entry = command.Definition.Catalog;
        if (entry.Kind != "barcode") return;
        var text = command.Parameter("text");
        if (text?.Value is not { } value) return;
        if (Dynamic(value, names))
        {
            diagnostics.Add("barcode-dynamic", CabDiagnosticSeverity.Information,
                "Dynamischer Barcodeinhalt benötigt die Inhaltsfeld-/Beispielwertauswertung.", entry, text.Syntax!);
            return;
        }
        var section = int.Parse(entry.Section.Split('.')[^1], System.Globalization.CultureInfo.InvariantCulture);
        var type = command.Parameter("type")?.Value ?? "";
        var linear = value.Split('|')[0];
        CheckCharacters(section, Composite.Contains(section) ? linear : value, type, entry, text.Syntax!, diagnostics);
        CheckLength(section, Composite.Contains(section) ? linear : value, entry, text.Syntax!, diagnostics);
        DataBar(section, linear, entry, text.Syntax!, diagnostics);
        Combinations(command, diagnostics);
        Capacity(command, value, diagnostics);
    }

    private static void DataBar(int section, string linear, CabCatalogEntry entry, CabSyntaxNode source, CabDiagnosticCollector diagnostics)
    {
        if (section is >= 22 and <= 26 && Regex.IsMatch(linear, "^[0-9]{14}$", RegexOptions.CultureInvariant) && !CheckDigit(linear))
            Error("barcode-checksum", "GTIN-Prüfziffer stimmt nicht.", entry, source, diagnostics);
        if (section == 22 && linear.Length > 0 && linear[0] is not ('0' or '1'))
            Error("barcode-limited", "GS1 DataBar Limited benötigt führende 0 oder 1.", entry, source, diagnostics);
    }

    private static bool Dynamic(string value, IReadOnlySet<string> names)
    {
        if (Regex.IsMatch(value.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal), @"\{[^{}]+\}", RegexOptions.CultureInvariant)) return true;
        foreach (Match field in Regex.Matches(value, @"\[(?<body>[^\]]+)\]", RegexOptions.CultureInvariant))
        {
            var body = field.Groups["body"].Value;
            if (names.Contains(body.Split(',')[0])) return true;
            if (KnownContent(body)) return true;
        }
        return false;
    }

    private static bool KnownContent(string body)
    {
        foreach (var entry in CabCatalog.Default.Entries.Where(e => e.Kind == "inhaltsfeld" && e.Id is not ("JS-5.88" or "JS-5.89")))
        {
            var name = entry.Command?.TrimStart('[').Split(':', '.', ']', '{', ',')[0];
            if (!string.IsNullOrEmpty(name) && MatchesContentName(body, name)) return true;
        }
        return false;
    }

    private static bool MatchesContentName(string body, string name)
        => body == name || body.StartsWith(name + ":", StringComparison.Ordinal)
            || body.StartsWith(name + ".", StringComparison.Ordinal) || body.StartsWith(name + ",", StringComparison.Ordinal);

    private static void CheckCharacters(int section, string value, string type, CabCatalogEntry entry, CabSyntaxNode source, CabDiagnosticCollector diagnostics)
    {
        var pattern = CharacterPatterns.GetValueOrDefault(section);
        if (Numeric.Contains(section)) pattern = Digits;
        if (section == 9 && type.Contains("FULL", StringComparison.OrdinalIgnoreCase)) pattern = null;
        if (pattern is not null && !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant))
            Error("barcode-characters", "Barcodeinhalt enthält nicht zulässige Zeichen.", entry, source, diagnostics);
        if (Ascii.Contains(section) && value.Any(c => c > 127))
            Error("barcode-ascii", "Diese Symbologie beschreibt einen ASCII-Zeichenvorrat.", entry, source, diagnostics);
    }

    private static void CheckLength(int section, string value, CabCatalogEntry entry, CabSyntaxNode source, CabDiagnosticCollector diagnostics)
    {
        if (Lengths.TryGetValue(section, out var lengths) && !lengths.Contains(value.Length))
            Error("barcode-length", "Eingabelänge passt nicht zur Symbologie (" + string.Join("/", lengths) + ").", entry, source, diagnostics);
        if (section == 45 && (value.Length is < 7 or > 12 || value[0] != '0'))
            Error("barcode-length", "UPC-E0 benötigt führende Null und sieben bis zwölf Eingabeziffern.", entry, source, diagnostics);
        if (section is 20 or 21)
        {
            var maximum = value.All(char.IsAsciiDigit) ? 74 : 41;
            if (value.Length > maximum) Error("barcode-length", "GS1 DataBar Expanded überschreitet die dokumentierte Kapazität.", entry, source, diagnostics);
        }
        if (section == 8 && value.Length > 2725) Error("barcode-length", "Codablock F überschreitet 2725 Zeichen.", entry, source, diagnostics);
    }

    internal static bool CheckDigit(string value)
    {
        if (value.Length < 2 || value.Any(c => !char.IsAsciiDigit(c))) return false;
        var sum = 0;
        var weight = 3;
        for (var i = value.Length - 2; i >= 0; i--) { sum += (value[i] - '0') * weight; weight = weight == 3 ? 1 : 3; }
        return (10 - sum % 10) % 10 == value[^1] - '0';
    }

    private static void Combinations(CabBoundCommand command, CabDiagnosticCollector diagnostics)
    {
        if (command.Definition.Catalog.Id == "JS-4.2.41" && CabDocumentRules.Number(command, "MODEL") == 1 && command.Parameter("VERSION") is not null)
            Error("qr-model", "Versionswahl ist nur für QR Model 2 dokumentiert.", command.Definition.Catalog, command.Parameter("VERSION")!.Syntax!, diagnostics);
        if (command.Definition.Catalog.Id == "JS-4.2.13" && CabDocumentRules.Number(command, "height") is { } height)
        {
            if (CabDocumentRules.Millimetres(height, command.Syntax.Unit) < 30)
                diagnostics.Add("dbp-height", CabDiagnosticSeverity.Warning, "Der Drucker hebt DBP-Höhen unter 30 mm an.", command.Definition.Catalog, command.Syntax);
        }
    }

    private static void Capacity(CabBoundCommand command, string value, CabDiagnosticCollector diagnostics)
    {
        var entry = command.Definition.Catalog;
        var maximum = entry.Id switch
        {
            "JS-4.2.12" or "JS-4.2.27" => value.Any(char.IsAsciiDigit) ? 3116 : 2335,
            "JS-4.2.34" => value.Any(char.IsAsciiDigit) ? 366 : 250,
            "JS-4.2.28" or "JS-4.2.37" or "JS-4.2.41" => 2000,
            _ => int.MaxValue
        };
        var length = value.Length;
        try { length = CabEncoding.Get(command.Syntax.CodePage).GetByteCount(value); }
        catch (ArgumentException) { return; }
        if (length > maximum) Error("barcode-capacity", "Barcode überschreitet die dokumentierte Datenkapazität.", entry, command.Parameter("text")!.Syntax!, diagnostics);
        CabMatrixRules.Validate(command, value, length, diagnostics);
    }

    private static void Error(string code, string message, CabCatalogEntry entry, CabSyntaxNode source, CabDiagnosticCollector diagnostics)
        => diagnostics.Add(code, CabDiagnosticSeverity.Error, message, entry, source);
}
