namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabLogicalReader
{
    internal static IReadOnlyList<CabCommandSlice> Read(CabDocument document)
    {
        var slices = new List<CabCommandSlice>();
        var pending = new List<CabSyntaxNode>();
        foreach (var node in PhysicalNodes(document.Root))
        {
            if (IsLineEnding(document, node)) { Flush(document, pending, slices); continue; }
            if (node.Kind is CabNodeKind.EscapeCommand or CabNodeKind.BinaryBlock)
            { AddControl(document, node, slices); continue; }
            if (node.Kind == CabNodeKind.Block)
            { Flush(document, pending, slices); AddControl(document, node, slices); continue; }
            pending.Add(node);
        }
        Flush(document, pending, slices);
        slices.Sort((left, right) => left.Node.Range.Offset.CompareTo(right.Node.Range.Offset));
        AssignUnits(document.Root.Unit, slices);
        return slices.AsReadOnly();
    }

    private static IEnumerable<CabSyntaxNode> PhysicalNodes(CabSyntaxNode root)
        => root.Children.SelectMany(n => n.Kind == CabNodeKind.Job ? PhysicalNodes(n) : new[] { n });

    private static bool IsLineEnding(CabDocument document, CabSyntaxNode node)
        => node.Kind == CabNodeKind.RawLine && node.Command.Length == 0 && node.Range.Length is 1 or 2
            && document.Bytes(node.Range).All(b => b is 10 or 13);

    private static void Flush(CabDocument document, List<CabSyntaxNode> pending, List<CabCommandSlice> slices)
    {
        var first = pending.FirstOrDefault(n => document.Bytes(n.Range).Any(b => b is not (32 or 9)));
        if (first is not null)
        {
            var parts = pending.Select(n => new CabBoundPart(n.Range, n.CodePage)).ToArray();
            slices.Add(new(document, first, parts));
        }
        pending.Clear();
    }

    private static void AddControl(CabDocument document, CabSyntaxNode node, List<CabCommandSlice> slices)
    {
        slices.Add(new(document, node));
        foreach (var control in node.Children.Where(n => n.Kind == CabNodeKind.EscapeCommand)) AddControl(document, control, slices);
    }

    private static void AssignUnits(CabUnit initial, List<CabCommandSlice> slices)
    {
        var changes = slices.Where(s => s.Node.Command == "m")
            .Select(s => (End: s.Node.Range.End, Value: s.Protocol(s.Body).Trim()))
            .Where(c => c.Value is "m" or "i").OrderBy(c => c.End).ToArray();
        var next = 0;
        var unit = initial;
        foreach (var slice in slices)
        {
            while (next < changes.Length && changes[next].End <= slice.Node.Range.Offset)
                unit = changes[next++].Value == "m" ? CabUnit.Millimetres : CabUnit.Inches;
            slice.SetUnit(unit);
        }
    }
}
