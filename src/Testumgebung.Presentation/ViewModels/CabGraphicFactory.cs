using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

public static class CabGraphicFactory
{
    public static CabGraphicScene Build(CabSourceEditor editor, LabelLayout layout, int jobIndex)
    {
        if (layout.Dpi is < 50 or > 1200) throw new InvalidDataException("Druckauflösung muss zwischen 50 und 1200 dpi liegen.");
        var commands = editor.Validation.Commands.Where(c => c.JobIndex == jobIndex).ToArray();
        if (commands.Length == 0) throw new ArgumentOutOfRangeException(nameof(jobIndex));
        var size = commands.LastOrDefault(c => c.Syntax.Command == "S");
        var (width, height, gap, columns, pitch) = Sheet(size, layout);
        var options = commands.Where(c => c.Syntax.Command == "O").ToArray();
        var global = LabelTransform(width, height, options);
        var (x, y) = Offset(commands, size, layout.Dpi);
        var contents = CabGraphicContent.Evaluate(editor, layout);
        var items = new List<CabGraphicItem>();
        foreach (var command in commands.Where(c => c.Syntax.Command is "T" or "W" or "B" or "G" or "I"))
        {
            contents.TryGetValue(command.Syntax.Range.Offset, out var content);
            var item = CabGraphicObject.Create(editor, command, layout.Dpi, content);
            var placement = CabGraphicTransform.Placement(CabGraphicValues.Length(command, "x", layout.Dpi) + x,
                CabGraphicValues.Length(command, "y", layout.Dpi) + y, CabGraphicValues.Number(command, "r")).Then(global);
            for (var column = 0; column < columns; column++)
                items.Add(item with { Column = column, Transform = placement.Then(new(1, 0, 0, 1, column * pitch, 0)) });
        }
        return new(jobIndex, layout.Dpi, width + (columns - 1) * pitch, height, gap,
            size is null ? "nicht angegeben" : CabGraphicValues.Text(size, "ptype"), columns, pitch, global, items, Notes(size, columns));
    }

    private static IReadOnlyList<string> Notes(CabBoundCommand? size, int columns)
        => columns > 1 && size is not null && !CabGraphicValues.Present(size, "dx")
            ? ["Spaltenabstand fehlt; die Vorschau verwendet ausdrücklich die Etikettenbreite als Näherung."] : [];

    private static (double Width, double Height, double Gap, int Columns, double Pitch) Sheet(CabBoundCommand? size, LabelLayout layout)
    {
        if (size is null) return (layout.WidthDots, layout.HeightDots, (double)layout.GapMm * layout.Dpi / 25.4, 1, layout.WidthDots);
        var width = CabGraphicValues.Length(size, "wd", layout.Dpi, layout.WidthDots);
        var height = CabGraphicValues.Length(size, "ho", layout.Dpi, layout.HeightDots);
        return (width, height, Math.Max(0, CabGraphicValues.Length(size, "dy", layout.Dpi, height) - height),
            Math.Clamp((int)CabGraphicValues.Number(size, "col", 1), 1, 128), CabGraphicValues.Length(size, "dx", layout.Dpi, width));
    }

    private static (double X, double Y) Offset(CabBoundCommand[] commands, CabBoundCommand? size, int dpi)
    {
        var offset = commands.LastOrDefault(c => c.Syntax.Command == "D");
        return ((size is null ? 0 : CabGraphicValues.Length(size, "xo", dpi)) + (offset is null ? 0 : CabGraphicValues.Length(offset, "x", dpi)),
            (size is null ? 0 : CabGraphicValues.Length(size, "yo", dpi)) + (offset is null ? 0 : CabGraphicValues.Length(offset, "y", dpi)));
    }

    private static CabGraphicTransform LabelTransform(double width, double height, IEnumerable<CabBoundCommand> options)
    {
        var result = CabGraphicTransform.Identity;
        foreach (var command in options)
        {
            if (CabGraphicValues.Present(command, "M")) result = result.Then(new(-1, 0, 0, 1, width, 0));
            if (CabGraphicValues.Present(command, "R")) result = result.Then(new(-1, 0, 0, -1, width, height));
        }
        return result;
    }
}
