using System.Globalization;
using System.Text.Json;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabMatrixRules
{
    private const string QrAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    internal static void Validate(CabBoundCommand command, string? text, int byteLength, CabDiagnosticCollector diagnostics)
    {
        var sourceId = command.Definition.Catalog.Id switch
        {
            "JS-4.2.12" or "JS-4.2.27" => "JS-4.2.12",
            "JS-4.2.29" or "JS-4.2.42" => "JS-4.2.42",
            "JS-4.2.35" => "JS-4.2.35",
            _ => null,
        };
        if (sourceId is null) return;
        var entry = CabCatalog.Default[sourceId];
        var rows = entry.Metadata.GetProperty("kapazitaeten").EnumerateArray().Where(row => Matches(command, entry.Id, row)).ToArray();
        if (rows.Length == 0)
        {
            diagnostics.Add("matrix-size", CabDiagnosticSeverity.Error, "Die gewählte Matrixgröße ist nicht in der Kapazitätstabelle enthalten.", entry, command.Syntax);
            return;
        }
        if (text is not null) CheckCapacity(command, entry, rows, text, byteLength, diagnostics);
    }

    private static bool Matches(CabBoundCommand command, string id, JsonElement row)
    {
        if (id == "JS-4.2.12")
        {
            var size = row.GetProperty("groesse").GetString()!.Split('x');
            return Dimension(command, "ROWS", int.Parse(size[0].Trim(), CultureInfo.InvariantCulture))
                && Dimension(command, "COLS", int.Parse(size[1].Trim(), CultureInfo.InvariantCulture));
        }
        var version = CabDocumentRules.Number(command, "VERSION");
        // Micro QR VERSION1 grows automatically; fixed versions may reduce the error level.
        if (id == "JS-4.2.35") return version is null or 1 || version == row.GetProperty("version").GetInt32();
        if (version is not null && version != row.GetProperty("version").GetInt32()) return false;
        var level = command.Parameter("EL")?.Value;
        return level is null || level == row.GetProperty("level").GetString();
    }

    private static bool Dimension(CabBoundCommand command, string name, int actual)
        => CabDocumentRules.Number(command, name) is not { } selected || selected == actual;

    private static void CheckCapacity(CabBoundCommand command, CabCatalogEntry entry, JsonElement[] rows,
        string text, int byteLength, CabDiagnosticCollector diagnostics)
    {
        // Non-ASCII QR data can use Kanji or mixed modes; a byte-only bound would reject valid data.
        if (entry.Id != "JS-4.2.12" && text.Any(c => c > 127)) return;
        var mode = Mode(entry.Id, text);
        var limits = rows.Select(row => row.GetProperty(mode)).Where(value => value.ValueKind == JsonValueKind.Number)
            .Select(value => value.GetInt32()).ToArray();
        var maximum = limits.Length == 0 ? 0 : limits.Max();
        var length = entry.Id == "JS-4.2.12" ? byteLength : text.Length;
        if (length <= maximum) return;
        var uncertain = entry.Id == "JS-4.2.42" && CabDocumentRules.Number(command, "VERSION") == 13
            && command.Parameter("EL")?.Value == "M";
        diagnostics.Add("matrix-capacity", uncertain ? CabDiagnosticSeverity.Warning : CabDiagnosticSeverity.Error,
            uncertain ? "Die Kapazitätszeile für Version 13/M ist widersprüchlich; eine sichere Grenze ist nicht ableitbar."
                : "Der Inhalt überschreitet die dokumentierte Kapazität der gewählten Matrixgröße.", entry, command.Parameter("text")!.Syntax!);
    }

    private static string Mode(string id, string text)
    {
        // Mixed numeric segments can exceed a pure text/byte capacity. Use a conservative upper bound.
        if (text.Any(char.IsAsciiDigit)) return "numerisch";
        if (id == "JS-4.2.12" || text.All(c => QrAlphabet.Contains(c))) return "alphanumerisch";
        return text.All(c => !QrAlphabet.Contains(c)) ? "binaer" : "numerisch";
    }
}
