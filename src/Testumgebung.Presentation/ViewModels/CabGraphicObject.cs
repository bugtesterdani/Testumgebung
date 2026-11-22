using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

internal static class CabGraphicObject
{
    internal static CabGraphicItem Create(CabSourceEditor editor, CabBoundCommand command, int dpi, CabGraphicContentValue? content)
    {
        var text = content?.Text ?? CabGraphicValues.Text(command, "text");
        var notes = content?.Notes.ToList() ?? [];
        return command.Syntax.Command switch
        {
            "T" => Text(command, dpi, text, notes), "W" => RichText(command, dpi, text, notes),
            "B" => Barcode(command, dpi, content ?? new(text, new(text), notes)),
            "I" => Image(editor, command, notes), _ => Shape(editor, command, dpi, notes),
        };
    }

    private static CabGraphicItem Text(CabBoundCommand command, int dpi, string text, List<string> notes)
    {
        var font = CabGraphicValues.Number(command, "font", 3);
        var bitmap = font < 0 || CabGraphicValues.Present(command, "size.x");
        var height = bitmap ? BitmapCell(font).Height * CabGraphicValues.Number(command, "size.y", 1) : CabGraphicValues.FontSize(command, dpi);
        var cell = bitmap ? BitmapCell(font).Width * CabGraphicValues.Number(command, "size.x", 1) : height * (font == 596 ? .6 : .58);
        var stretch = CabGraphicValues.Number(command, "effects.q", 100) / 100;
        var spacing = CabGraphicValues.Length(command, "effects.m", dpi);
        if (CabGraphicValues.Present(command, "effects.h")) cell = CabGraphicValues.Length(command, "effects.h", dpi);
        var count = Math.Max(1, text.EnumerateRunes().Count());
        var vertical = CabGraphicValues.Effects(command).Contains("v");
        var width = (vertical ? cell : cell * count + spacing * (count - 1)) * stretch;
        var totalHeight = vertical ? height * count : height;
        var bounds = new CabGraphicRect(0, -height * .8, Math.Max(1, width), Math.Max(1, totalHeight));
        notes.Add("Schriftnäherung: Windows-Schrift statt druckerinterner Bitmap-/Vektor-/TrueType-Metrik; Grundlinienbezug bleibt erhalten.");
        return Framed(command, CabGraphicKind.Text, bounds, text, notes, dpi) with { FontHeight = height };
    }

    private static (double Width, double Height) BitmapCell(double font) => font switch
    {
        -1 => (12, 12), -2 => (16, 16), -3 => (16, 32), -4 => (12, 20), -5 => (13, 20), _ => (12, 12),
    };

    private static CabGraphicItem RichText(CabBoundCommand command, int dpi, string text, List<string> notes)
    {
        notes.Add("Rich-Text-Schriftnäherung; das Rechteck begrenzt den dargestellten Inhalt.");
        var bounds = new CabGraphicRect(0, 0, Math.Max(1, CabGraphicValues.Length(command, "width", dpi)), Math.Max(1, CabGraphicValues.Length(command, "height", dpi)));
        return Framed(command, CabGraphicKind.RichText, bounds, text, notes, dpi) with { FontHeight = CabGraphicValues.FontSize(command, dpi) };
    }

    private static CabGraphicItem Barcode(CabBoundCommand command, int dpi, CabGraphicContentValue content)
    {
        var symbol = CabBarcodePreview.Create(command, content);
        var module = Math.Max(.01, CabBarcodePreview.ModuleWidth(command, dpi));
        var height = CabGraphicValues.Present(command, "height") ? CabGraphicValues.Length(command, "height", dpi) : symbol.Rows * module;
        if (symbol.Rows == 1 && !CabGraphicValues.Present(command, "height")) height = 20 * dpi / 25.4;
        var bounds = new CabGraphicRect(0, 0, symbol.Columns * module, Math.Max(1, height));
        var notes = content.Notes.Append(symbol.Note).ToList();
        var type = CabGraphicValues.Text(command, "type");
        var humanHeight = type.Any(char.IsUpper) && !type.Any(char.IsLower) ? dpi * 3 / 25.4 : 0;
        if (humanHeight > 0) notes.Add("Klarschrift mit angenäherter Windows-Schrift und 3-mm-Zeile.");
        var display = bounds with { Height = bounds.Height + humanHeight };
        return Framed(command, CabGraphicKind.Barcode, display, content.Text, notes, dpi) with { Barcode = symbol, ContentBounds = bounds, HumanReadableHeight = humanHeight };
    }

    private static CabGraphicItem Image(CabSourceEditor editor, CabBoundCommand command, List<string> notes)
    {
        var image = CabGraphicImages.Find(editor, command);
        var width = (image?.PixelWidth ?? 0) * CabGraphicValues.Number(command, "mx", 1);
        var height = (image?.PixelHeight ?? 0) * CabGraphicValues.Number(command, "my", 1);
        if (width <= 0 || height <= 0) notes.Add("Bilddaten oder Bildmaße fehlen: beschriftete Vorschaufläche, keine behauptete Originalgröße.");
        var bounds = new CabGraphicRect(0, 0, Math.Max(1, width), Math.Max(1, height));
        return new(command, CabGraphicKind.Image, bounds, bounds, CabGraphicTransform.Identity, CabGraphicValues.Text(command, "name"), notes) { Image = image };
    }

    private static CabGraphicItem Shape(CabSourceEditor editor, CabBoundCommand command, int dpi, List<string> notes)
    {
        var kind = command.Definition.Catalog.Id switch { "JS-4.7.1" => CabGraphicKind.Circle, "JS-4.7.2" => CabGraphicKind.Line, _ => CabGraphicKind.Rectangle };
        var bounds = ShapeBounds(command, dpi, kind);
        var strokeX = kind == CabGraphicKind.Rectangle ? CabGraphicValues.Length(command, "vt", dpi) : CabGraphicValues.Length(command, "width", dpi);
        var strokeY = kind == CabGraphicKind.Rectangle ? CabGraphicValues.Length(command, "ht", dpi) : strokeX;
        var fill = CabGraphicValues.Text(command, "F.value");
        var pattern = fill.StartsWith("user", StringComparison.Ordinal) ? CabGraphicImages.Find(editor, command, fill) : null;
        if (fill.StartsWith("user", StringComparison.Ordinal) && pattern is not { PixelWidth: 32, PixelHeight: 32 })
            notes.Add("Benutzermuster " + fill + " fehlt oder ist nicht 32 × 32 Punkte groß: graue Ersatzfüllung.");
        return new(command, kind, bounds, bounds, CabGraphicTransform.Identity, "", notes) { StrokeX = strokeX, StrokeY = strokeY, FillPattern = pattern };
    }

    private static CabGraphicRect ShapeBounds(CabBoundCommand command, int dpi, CabGraphicKind kind)
    {
        if (kind == CabGraphicKind.Circle)
        {
            var rx = CabGraphicValues.Length(command, "radius1", dpi); var ry = CabGraphicValues.Length(command, "radius2", dpi, rx);
            return new(-rx, -ry, rx * 2, ry * 2);
        }
        if (kind == CabGraphicKind.Line)
        {
            var width = CabGraphicValues.Length(command, "width", dpi);
            return new(-width / 2, -width / 2, CabGraphicValues.Length(command, "length", dpi) + width, width);
        }
        return new(0, 0, CabGraphicValues.Length(command, "width", dpi), CabGraphicValues.Length(command, "height", dpi));
    }

    private static CabGraphicItem Framed(CabBoundCommand command, CabGraphicKind kind, CabGraphicRect bounds, string text, List<string> notes, int dpi)
    {
        var prefix = kind == CabGraphicKind.Barcode ? "fx." : "effects.";
        var expanded = bounds.Inflate(CabGraphicValues.Length(command, prefix + "fl", dpi), CabGraphicValues.Length(command, prefix + "fu", dpi),
            CabGraphicValues.Length(command, prefix + "fr", dpi), CabGraphicValues.Length(command, prefix + "fd", dpi));
        return new(command, kind, bounds, expanded, CabGraphicTransform.Identity, text, notes);
    }
}
