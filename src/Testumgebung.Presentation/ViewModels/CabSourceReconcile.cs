using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Rebases projected bindings while preserving every unchanged object instance.</summary>
public static class CabSourceReconcile
{
    public static void Apply(LabelLayout layout, CabSourceEditor before, CabSourceEditor after)
    {
        var imported = CabLabelImport.Read(after.Document.Write(), layout.Dpi, after.SetupCodePage);
        var old = layout.Elements.Where(e => e.CabSourceOffset is not null).ToDictionary(e => e.CabSourceOffset!.Value);
        var mapping = UnchangedCommands(before, after);
        var elements = imported.Elements.Select(next => Reuse(next, old, mapping)).ToList();
        layout.WidthMm = imported.WidthMm; layout.HeightMm = imported.HeightMm; layout.GapMm = imported.GapMm;
        layout.CabSource = imported.CabSource;
        layout.Elements.Clear(); layout.Elements.AddRange(elements);
    }

    private static LabelElement Reuse(LabelElement next, Dictionary<int, LabelElement> old, Dictionary<int, int> offsets)
    {
        var previous = offsets.Where(p => p.Value == next.CabSourceOffset).Select(p => old.GetValueOrDefault(p.Key)).FirstOrDefault();
        if (previous is null) return next;
        previous.CabSourceOffset = next.CabSourceOffset;
        return previous;
    }

    private static Dictionary<int, int> UnchangedCommands(CabSourceEditor before, CabSourceEditor after)
    {
        // A minimal text diff can start inside the common prefix of two adjacent
        // commands. Match complete unchanged commands instead of treating that
        // incidental prefix as an object identity boundary.
        var available = before.Validation.Commands.GroupBy(c => Signature(before.Document, c))
            .ToDictionary(g => g.Key, g => new Queue<int>(g.Select(c => c.Syntax.Range.Offset)), StringComparer.Ordinal);
        var result = new Dictionary<int, int>();
        foreach (var command in after.Validation.Commands)
            if (available.TryGetValue(Signature(after.Document, command), out var queue) && queue.TryDequeue(out var old))
                result.Add(old, command.Syntax.Range.Offset);
        return result;
    }

    private static string Signature(CabDocument document, CabBoundCommand command)
        => command.Syntax.CodePage + "\0" + command.Syntax.Unit + "\0" + Convert.ToBase64String(document.Bytes(command.Syntax.Range));
}
