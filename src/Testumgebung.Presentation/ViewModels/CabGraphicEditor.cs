using System.Globalization;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Graphic gestures use bound parameter edits; they never regenerate a whole label.</summary>
public static class CabGraphicEditor
{
    public static CabSourceEditor Add(CabSourceEditor editor, string id, int jobIndex)
    {
        if (!CabGraphicCatalog.Definitions.Any(e => e.Id == id)) throw new ArgumentException("Keine grafische Katalogdefinition.", nameof(id));
        var target = CabJobEditor.Items(editor).FirstOrDefault(i => i.Command?.JobIndex == jobIndex && i.Command.Syntax.Command == "A");
        if (target is null) throw new InvalidOperationException("Der gewählte Auftrag benötigt einen A-Abschluss als eindeutige Einfügeposition.");
        var invocation = UniqueName(editor, CabPropertyTemplates.Invocation(id), jobIndex);
        var next = CabJobEditor.Insert(editor, invocation, target);
        if (target.Command!.Syntax.Unit == CabUnit.Inches) next = ConvertInsertedLengths(next, target.Range.Offset);
        return NoNewErrors(editor, next);
    }

    private static CabSourceEditor ConvertInsertedLengths(CabSourceEditor editor, int offset)
    {
        var command = editor.Validation.Commands.Single(c => c.Syntax.Range.Offset == offset);
        var lengths = CabPropertyBindings.ForCommand(editor, command)
            .Where(p => p.IsPresent && p.Definition.Rule.Unit == CabParameterUnit.CurrentLength
                && double.TryParse(p.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            .Select(p => (p.Definition.Name, Value: CabGraphicValues.Number(command, p.Definition.Name) / 25.4)).ToArray();
        foreach (var (name, value) in lengths)
        {
            command = editor.Validation.Commands.Single(c => c.Syntax.Range.Offset == offset);
            var property = CabPropertyBindings.ForCommand(editor, command).Single(p => p.Definition.Name == name);
            editor = CabPropertyBindings.Replace(editor, property, value.ToString("0.############", CultureInfo.InvariantCulture));
        }
        return editor;
    }

    private static byte[] UniqueName(CabSourceEditor editor, byte[] bytes, int jobIndex)
    {
        var snippet = new CabSourceEditor(bytes, editor.SetupCodePage, editor.Settings);
        var command = snippet.Validation.Commands.First();
        var parameterName = command.Syntax.Command == "I" ? "fieldname" : "name";
        var property = CabPropertyBindings.ForCommand(snippet, command).FirstOrDefault(p => p.Definition.Name == parameterName && p.IsPresent);
        if (property?.Value is not { Length: > 0 } name) return bytes;
        var names = editor.Validation.Commands.Where(c => c.JobIndex == jobIndex)
            .Select(c => c.Parameter(c.Syntax.Command == "I" ? "fieldname" : "name")?.Value).ToHashSet(StringComparer.Ordinal);
        var candidate = name; var suffix = 2;
        while (names.Contains(candidate))
        {
            var number = suffix++.ToString(CultureInfo.InvariantCulture);
            candidate = name[..Math.Min(name.Length, 32 - number.Length)] + number;
        }
        return candidate == name ? bytes : CabPropertyBindings.Replace(snippet, property, candidate).Document.Write();
    }

    public static CabSourceEditor Remove(CabSourceEditor editor, CabGraphicItem item)
    {
        var row = CabJobEditor.Items(editor).Single(r => !r.IsEmbedded && r.Command?.Syntax.Range.Offset == item.Command.Syntax.Range.Offset);
        return NoNewErrors(editor, CabJobEditor.Remove(editor, row));
    }

    public static CabSourceEditor Move(CabSourceEditor editor, CabGraphicItem item, double x, double y)
        => Edit(editor, item, [("x", x), ("y", y)]);
    public static CabSourceEditor Rotate(CabSourceEditor editor, CabGraphicItem item, double degrees)
        => Edit(editor, item, [("r", (degrees % 360 + 360) % 360)]);

    public static CabSourceEditor MoveBy(CabSourceEditor editor, CabGraphicScene scene, CabGraphicItem item, double dx, double dy)
    {
        var delta = scene.LabelTransform.InverseVector(new(dx, dy));
        var factor = item.Command.Syntax.Unit == CabUnit.Inches ? scene.Dpi : scene.Dpi / 25.4;
        return Move(editor, item, CabGraphicValues.Number(item.Command, "x") + delta.X / factor,
            CabGraphicValues.Number(item.Command, "y") + delta.Y / factor);
    }

    public static CabSourceEditor Resize(CabSourceEditor editor, CabGraphicItem item, double width, double height, int dpi)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) throw new ArgumentException("Breite und Höhe müssen positive endliche Werte sein.");
        var factor = item.Command.Syntax.Unit == CabUnit.Inches ? dpi : dpi / 25.4;
        var values = SizeChanges(item, width, height, factor);
        return Edit(editor, item, values);
    }

    private static (string, double)[] SizeChanges(CabGraphicItem item, double width, double height, double factor)
        => item.Kind switch
        {
            CabGraphicKind.Circle => [("radius1", width / factor / 2), ("radius2", height / factor / 2)],
            CabGraphicKind.Rectangle or CabGraphicKind.RichText => [("width", width / factor), ("height", height / factor)],
            CabGraphicKind.Line => LineSize(width, height, factor),
            CabGraphicKind.Image => ImageSize(item, width, height),
            CabGraphicKind.Text => TextSize(item, width, height),
            _ => BarcodeSize(item, width, height),
        };

    private static (string, double)[] LineSize(double width, double height, double factor)
    {
        if (width < height) throw new ArgumentException("Die Begrenzungsbreite darf nicht kleiner als die Strichstärke sein.");
        return [("length", (width - height) / factor), ("width", height / factor)];
    }

    private static (string, double)[] ImageSize(CabGraphicItem item, double width, double height)
    {
        if (item.Image is not { PixelWidth: > 0, PixelHeight: > 0 } image) throw new InvalidOperationException("Für absolute Bildmaße fehlen die Originalabmessungen; mx/my bleiben im Eigenschaftenformular bearbeitbar.");
        return [("mx", width / image.PixelWidth), ("my", height / image.PixelHeight)];
    }

    private static (string, double)[] TextSize(CabGraphicItem item, double width, double height)
    {
        var command = item.Command;
        var sx = width / item.ContentBounds.Width; var sy = height / item.ContentBounds.Height;
        if (CabGraphicValues.Present(command, "size.x"))
            return [("size.x", Math.Max(1, Math.Round(CabGraphicValues.Number(command, "size.x", 1) * sx))),
                ("size.y", Math.Max(1, Math.Round(CabGraphicValues.Number(command, "size.y", 1) * sy)))];
        var size = CabGraphicValues.Present(command, "size.pt") ? "size.pt" : "size";
        return [(size, CabGraphicValues.Number(command, size) * sy), ("effects.q", CabGraphicValues.Number(command, "effects.q", 100) * sx / sy)];
    }

    private static (string, double)[] BarcodeSize(CabGraphicItem item, double width, double height)
    {
        var command = item.Command;
        if (CabGraphicValues.Present(command, "dotsize"))
        {
            var sx = width / item.ContentBounds.Width; var sy = height / item.ContentBounds.Height;
            if (Math.Abs(sx - sy) > .01) throw new InvalidOperationException("Matrixcodes werden proportional über ihre Modulgröße skaliert.");
            return [("dotsize", CabGraphicValues.Number(command, "dotsize") * sx)];
        }
        if (!CabGraphicValues.Present(command, "ne")) throw new InvalidOperationException("Diese Standardgröße im Eigenschaftenformular über SC oder die zugehörige Größenoption wählen.");
        return [("ne", CabGraphicValues.Number(command, "ne") * width / item.ContentBounds.Width),
            ("height", CabGraphicValues.Number(command, "height") * height / item.ContentBounds.Height)];
    }

    private static CabSourceEditor Edit(CabSourceEditor editor, CabGraphicItem item, IEnumerable<(string Name, double Value)> values)
    {
        var next = editor;
        foreach (var (name, value) in values)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Endlicher Parameterwert erwartet.");
            var command = next.Validation.Commands.Single(c => c.Syntax.Range.Offset == item.Command.Syntax.Range.Offset);
            var property = CabPropertyBindings.ForCommand(next, command).FirstOrDefault(p => p.Definition.Name == name)
                ?? throw new InvalidOperationException("Dieser Parameter gehört zu einer anderen Syntaxvariante: " + name);
            var text = (property.Definition.Rule.NumericPrefix ?? "") + value.ToString("0.########", CultureInfo.InvariantCulture);
            next = property.IsPresent ? CabPropertyBindings.Replace(next, property, text) : CabOptionalParameter.Add(next, command, property.Definition, text);
        }
        return NoNewErrors(editor, next);
    }

    private static CabSourceEditor NoNewErrors(CabSourceEditor before, CabSourceEditor after)
    {
        var known = before.Validation.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var introduced = after.Validation.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).GroupBy(d => d.Code).FirstOrDefault(g => g.Count() > known.GetValueOrDefault(g.Key));
        if (introduced is not null) throw new InvalidOperationException(introduced.First().Message);
        return after;
    }
}
