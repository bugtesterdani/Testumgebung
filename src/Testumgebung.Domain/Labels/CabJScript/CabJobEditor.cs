namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabJobItem(CabSourceRange Range, CabSyntaxNode Syntax, CabBoundCommand? Command, bool IsEmbedded, string Title)
{
    public override string ToString() => (IsEmbedded ? "↳ " : "") + Title;
}
public sealed record CabJobOrderIssue(string Code, string Message, CabSourceRange Range);

public static class CabJobEditor
{
    public static IReadOnlyList<CabJobItem> Items(CabSourceEditor editor)
    {
        var result = new List<CabJobItem>();
        var nodes = CabSourceEditor.Physical(editor.Document.Root).ToArray();
        foreach (var node in nodes)
        {
            if (result.Any(r => node.Range.Offset >= r.Range.Offset && node.Range.End <= r.Range.End)) continue;
            if (node.Command.Length == 0 && editor.Document.Bytes(node.Range).All(b => b is 10 or 13)) continue;
            var command = editor.Validation.Commands.FirstOrDefault(c => c.Syntax.Range.Offset == node.Range.Offset);
            var range = command?.Syntax.Range ?? node.Range;
            if (command?.Syntax.Command == "d") range = DownloadRange(command, range, nodes);
            range = WithLineEnding(editor.Document, range);
            var title = command is null ? node.Kind + " · " + node.Command : command.Definition.Catalog.Id + " · " + command.Definition.Catalog.Title;
            result.Add(new(range, node, command, false, title));
        }
        var embedded = editor.Validation.Commands.Where(c => !result.Any(r => ReferenceEquals(r.Command, c)))
            .Where(c => result.Any(r => Contains(r.Range, c.Syntax.Range)))
            .Select(c => new CabJobItem(c.Syntax.Range, c.Syntax, c, true, c.Definition.Catalog.Id + " · " + c.Definition.Catalog.Title)).ToArray();
        result.AddRange(embedded);
        return result.OrderBy(r => r.Range.Offset).ThenBy(r => r.IsEmbedded).ToArray();
    }

    private static bool Contains(CabSourceRange outer, CabSourceRange inner)
        => inner.Offset >= outer.Offset && inner.Offset < outer.End && inner.End <= outer.End;

    private static CabSourceRange DownloadRange(CabBoundCommand command, CabSourceRange range, CabSyntaxNode[] nodes)
    {
        var data = command.Parameter("data")?.Syntax;
        var payload = data is null ? null : nodes.FirstOrDefault(n => n.Kind == CabNodeKind.BinaryBlock && n.Range.Offset <= data.Range.Offset && n.Range.End >= data.Range.End);
        return payload is null ? range : new(range.Offset, payload.Range.End - range.Offset);
    }

    private static CabSourceRange WithLineEnding(CabDocument document, CabSourceRange range)
    {
        var end = range.End;
        if (end < document.Length && document.Bytes(new(end, 1))[0] == 13) end++;
        if (end < document.Length && document.Bytes(new(end, 1))[0] == 10) end++;
        return new(range.Offset, end - range.Offset);
    }

    public static CabSourceEditor Remove(CabSourceEditor editor, CabJobItem item)
    {
        Standalone(item);
        return Changed(editor, [new(item.Range, [])]);
    }

    public static CabSourceEditor MoveBefore(CabSourceEditor editor, CabJobItem item, CabJobItem? target)
    {
        Standalone(item); if (target is not null) Standalone(target);
        var offset = target?.Range.Offset ?? editor.Document.Length;
        if (offset >= item.Range.Offset && offset <= item.Range.End) return editor;
        return Changed(editor, [new(item.Range, []), new(new(offset, 0), InsertionBytes(editor, editor.Document.Bytes(item.Range), offset))]);
    }

    public static CabSourceEditor Insert(CabSourceEditor editor, byte[] bytes, CabJobItem? target = null)
    {
        if (target is not null) Standalone(target);
        var at = target?.Range.Offset ?? editor.Document.Length;
        return Changed(editor, [new(new(at, 0), InsertionBytes(editor, bytes, at))]);
    }

    private static byte[] InsertionBytes(CabSourceEditor editor, byte[] bytes, int at)
    {
        using var output = new MemoryStream();
        if (at > 0 && editor.Document.Bytes(new(at - 1, 1))[0] is not (10 or 13)) output.Write(System.Text.Encoding.ASCII.GetBytes(editor.Document.LineEnding));
        output.Write(bytes);
        if (bytes.Length > 0 && bytes[^1] is not (10 or 13)) output.Write(System.Text.Encoding.ASCII.GetBytes(editor.Document.LineEnding));
        return output.ToArray();
    }

    public static CabSourceEditor AddCommand(CabSourceEditor editor, string id, CabJobItem? target = null)
        => Insert(editor, CabPropertyTemplates.Invocation(id), target);

    private static void Standalone(CabJobItem item)
    {
        if (item.IsEmbedded) throw new InvalidOperationException("Eingebettete Steuerungen gehören zu ihrem übergeordneten Befehl. Dort oder im Quelltext bearbeiten.");
    }

    private static CabSourceEditor Changed(CabSourceEditor before, IReadOnlyList<CabTextChange> changes)
    {
        var next = new CabSourceEditor(before.Document.Write(changes), before.SetupCodePage, before.Settings);
        var old = OrderIssues(before).GroupBy(i => i.Code).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var introduced = OrderIssues(next).GroupBy(i => i.Code).FirstOrDefault(g => g.Count() > old.GetValueOrDefault(g.Key));
        if (introduced is not null) throw new InvalidOperationException(introduced.First().Message);
        return next;
    }

    public static IReadOnlyList<CabJobOrderIssue> OrderIssues(CabSourceEditor editor)
    {
        var result = new List<CabJobOrderIssue>(); var open = false;
        foreach (var command in editor.Validation.Commands)
        {
            if (command.Syntax.Command == "J") { open = true; continue; }
            if (LoadsLabel(command)) { open = true; continue; }
            if (command.Syntax.Command == "A")
            {
                if (!open) result.Add(new("amount-without-job", "A benötigt einen begonnenen oder geladenen Etikettenauftrag und steht an dessen Ende.", command.Syntax.Range));
                open = false; continue;
            }
            if (!open && command.Syntax.Command is "S" or "T" or "B" or "W" or "G" or "I")
                result.Add(new("label-without-job", "J muss vor Etikettenformat und Objekten stehen; nach A beginnt ein neuer Auftrag mit J.", command.Syntax.Range));
        }
        return result.AsReadOnly();
    }

    private static bool LoadsLabel(CabBoundCommand command)
        => command.Definition.Catalog.Id == "JS-4.11.4" && string.Equals(command.Parameter("type")?.Value, "LBL", StringComparison.OrdinalIgnoreCase);
}
