using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabDataBarRules
{
    internal static void Validate(int section, string input, CabBoundCommand command, CabSyntaxNode source, CabDiagnosticCollector diagnostics)
    {
        var entry = command.Definition.Catalog;
        var value = input;
        if (section == 23)
        {
            if (value.StartsWith("(01)", StringComparison.Ordinal)) value = value[4..];
            value = Regex.Replace(value, "[^0-9]", "", RegexOptions.CultureInvariant);
        }
        else if (!Regex.IsMatch(value, "^[0-9]+$", RegexOptions.CultureInvariant))
        { Error("barcode-characters", "DataBar benötigt numerische GTIN-Daten."); return; }
        if (!Length(section, value.Length))
        { Error("barcode-length", "DataBar-Eingabe passt nicht zur dokumentierten GTIN-Länge."); return; }
        var effective = value.PadLeft(14, '0');
        if (effective != input)
            diagnostics.Add("barcode-normalization", CabDiagnosticSeverity.Information,
                "GTIN-Prüfung berücksichtigt die dokumentierte Eingabeform und links ergänzte Nullen; die Quelldaten bleiben unverändert.", entry, source);
        if (section == 22 && effective[0] is not ('0' or '1')) Error("barcode-limited", "GS1 DataBar Limited benötigt führende 0 oder 1.");
        if (!CabBarcodeRules.CheckDigit(effective)) Error("barcode-checksum", "GTIN-Prüfziffer stimmt nicht.");

        void Error(string code, string message) => diagnostics.Add(code, CabDiagnosticSeverity.Error, message, entry, source);
    }

    private static bool Length(int section, int length) => section == 23 ? length is >= 1 and <= 14 : length is 8 or 12 or 13 or 14;

}
