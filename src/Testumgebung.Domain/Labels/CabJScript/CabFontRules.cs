namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabFontRules
{
    internal static void Validate(CabBoundCommand command, CabDiagnosticCollector diagnostics)
    {
        if (command.Syntax.Command is not ("T" or "W")) return;
        var font = CabDocumentRules.Number(command, "font");
        if (font is null) return;
        var bitmap = font is >= -5 and <= -1;
        Rotation(command, bitmap, diagnostics);
        if (command.Syntax.Command == "W" && bitmap) Error("rich-font", "Rich Text unterstützt keine Bitmapschriften.", command, diagnostics);
        if (!bitmap && command.Parameter("size.x") is not null) Error("font-size", "Bitmap-Vergrößerung passt nicht zur Vektor-/TrueType-Schrift.", command, diagnostics);
        Effects(command, bitmap, font.Value, diagnostics);
    }

    private static void Rotation(CabBoundCommand command, bool bitmap, CabDiagnosticCollector diagnostics)
    {
        var angle = CabDocumentRules.Number(command, "r");
        if (bitmap && angle is not (null or 0 or 90 or 180 or 270))
            Error("font-rotation", "Bitmapschriften können nur um 0, 90, 180 oder 270 Grad gedreht werden.", command, diagnostics);
        if (!bitmap && angle is { } degrees && degrees != decimal.Truncate(degrees))
            Error("font-rotation", "Vektor-/TrueType-Schriften benötigen ganzzahlige Drehwinkel.", command, diagnostics);
    }

    private static void Effects(CabBoundCommand command, bool bitmap, decimal font, CabDiagnosticCollector diagnostics)
    {
        var effects = command.Parameter("effects")?.Value?.Split(',') ?? [];
        foreach (var effect in effects)
        {
            if (bitmap && new[] { "fr", "fl", "fu", "fd" }.Any(p => effect.StartsWith(p, StringComparison.Ordinal)))
                Error("font-effect", "Rahmeneffekte sind nur für Vektor-/TrueType-Schriften dokumentiert.", command, diagnostics);
            if (!bitmap && effect is "o" or "g") Error("font-effect", "Umriss/Grau sind nur für Bitmapschriften dokumentiert.", command, diagnostics);
            OcrEffect(command, font, effect, diagnostics);
        }
    }

    private static void OcrEffect(CabBoundCommand command, decimal font, string effect, CabDiagnosticCollector diagnostics)
    {
        if (font is -4 or -5 && effect is "o" or "g")
            diagnostics.Add("ocr-effect", CabDiagnosticSeverity.Warning,
                "Tabelle verbietet Umriss/Grau für OCR; ein Handbuchbeispiel widerspricht dieser Angabe.", command.Definition.Catalog, command.Syntax);
    }

    private static void Error(string code, string message, CabBoundCommand command, CabDiagnosticCollector diagnostics)
        => diagnostics.Add(code, CabDiagnosticSeverity.Error, message, command.Definition.Catalog, command.Syntax);
}
