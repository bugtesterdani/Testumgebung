namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabPositionRules
{
    internal static void Validate(IReadOnlyList<CabBoundCommand> commands, CabDiagnosticCollector diagnostics)
    {
        var size = commands.LastOrDefault(c => c.Syntax.Command == "S");
        if (size is null) return;
        var width = Length(size, "wd"); var height = Length(size, "ho");
        if (width is null || height is null || width <= 0 || height <= 0) return;
        var originX = Length(size, "xo") ?? 0;
        var originY = Length(size, "yo") ?? 0;
        var shift = commands.LastOrDefault(c => c.Syntax.Command == "D");
        if (shift is not null) { originX += Length(shift, "x") ?? 0; originY += Length(shift, "y") ?? 0; }
        var mirror = commands.Any(c => c.Syntax.Command == "O" && c.Parameter("M") is not null);
        var rotate = commands.Any(c => c.Syntax.Command == "O" && c.Parameter("R") is not null);
        foreach (var field in commands.Where(c => c.Syntax.Command is "T" or "B" or "G" or "I" or "W"))
            Check(field, originX, originY, width.Value, height.Value, mirror, rotate, diagnostics);
    }

    private static void Check(CabBoundCommand command, decimal originX, decimal originY, decimal width, decimal height,
        bool mirror, bool rotate, CabDiagnosticCollector diagnostics)
    {
        var x = Length(command, "x"); var y = Length(command, "y");
        if (x is null || y is null) return;
        var actualX = x.Value + originX; var actualY = y.Value + originY;
        if (mirror) actualX = width - actualX;
        if (rotate) { actualX = width - actualX; actualY = height - actualY; }
        if (actualX < 0 || actualY < 0 || actualX > width || actualY > height)
            diagnostics.Add("position", CabDiagnosticSeverity.Warning, "Position liegt nach S/D/O außerhalb des Etiketts.", command.Definition.Catalog, command.Syntax);
    }

    private static decimal? Length(CabBoundCommand command, string name)
        => CabDocumentRules.Number(command, name) is { } value ? CabDocumentRules.Millimetres(value, command.Syntax.Unit) : null;
}
