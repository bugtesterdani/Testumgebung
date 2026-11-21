namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Latin-1 is used only for one-character-per-byte framing, never to decode user data.</summary>
internal sealed class CabCommandSlice
{
    internal CabDocument Document { get; }
    private readonly CabMappedText _mapped;
    internal CabSyntaxNode Node { get; private set; }
    internal string Framing { get; }
    internal int CommandStart { get; }
    internal int BodyStart { get; }
    internal CabSourceRange? Name { get; }

    internal CabCommandSlice(CabDocument document, CabSyntaxNode node)
        : this(document, node, new CabMappedText(document, [new(node.Range, node.CodePage)]), false) { }

    internal CabCommandSlice(CabDocument document, CabSyntaxNode node, IReadOnlyList<CabBoundPart> parts)
        : this(document, node, new CabMappedText(document, parts), true) { }

    private CabCommandSlice(CabDocument document, CabSyntaxNode node, CabMappedText mapped, bool logicalText)
    {
        Document = document; _mapped = mapped; Framing = mapped.Framing;
        CommandStart = SkipWhite(0);
        Node = logicalText ? node with { Range = mapped.SourceRange,
            Command = CommandStart < Framing.Length ? Framing[CommandStart].ToString() : "",
            CodePage = mapped.PageAt(mapped.Range(CommandStart, 0).Offset), Children = [] } : node;
        var body = Math.Min(CommandStart + 1, Framing.Length);
        if (body < Framing.Length && Framing[body] == ':' && "TBGIW".Contains(Node.Command, StringComparison.Ordinal))
        {
            var separator = Framing.IndexOf(';', body);
            if (separator >= 0) { Name = Range(body + 1, separator - body - 1); body = separator + 1; }
        }
        BodyStart = Node.Command == ";" ? body : SkipWhite(body);
    }

    internal void SetUnit(CabUnit unit) => Node = Node with { Unit = unit };
    internal CabSourceRange Range(int start, int length) => _mapped.Range(start, length);
    internal CabSourceRange Body => Range(BodyStart, Framing.Length - BodyStart);
    internal string? Decode(CabSourceRange range) => _mapped.Decode(range);
    internal CabSyntaxNode Syntax(CabSourceRange range) => new(CabNodeKind.Parameter, range, "", _mapped.PageAt(range.Offset), Node.Unit, []);
    internal string Protocol(CabSourceRange range) => _mapped.Protocol(range);
    internal IReadOnlyList<CabBoundPart> Parts(CabSourceRange range) => _mapped.Parts(range);
    internal CabSourceRange Subrange(CabSourceRange range, int start, int length) => Range(_mapped.VirtualOffset(range.Offset) + start, length);
    internal CabSourceRange TailAfter(CabSourceRange range, int separatorLength)
    {
        var start = _mapped.VirtualOffset(range.Offset) + Protocol(range).Length + separatorLength;
        return Range(start, Framing.Length - start);
    }

    internal IReadOnlyList<CabSourceRange> Split(CabSourceRange range, char delimiter)
    {
        var parts = new List<CabSourceRange>();
        var start = _mapped.VirtualOffset(range.Offset);
        var end = _mapped.VirtualOffset(range.End);
        var field = start;
        var depth = 0;
        for (var at = start; at < end; at++)
        {
            if (Framing[at] == '[') depth++;
            else if (Framing[at] == ']' && depth > 0) depth--;
            else if (Framing[at] == delimiter && depth == 0)
            { parts.Add(Range(field, at - field)); field = at + 1; }
        }
        parts.Add(Range(field, end - field));
        return parts.AsReadOnly();
    }

    internal int SkipWhite(int start)
    {
        while (start < Framing.Length && Framing[start] is ' ' or '\t') start++;
        return start;
    }
}

internal sealed class CabBindingBuilder(CabCommandDefinition definition, CabCommandSlice slice, int jobIndex, string baseId)
{
    private readonly List<CabBoundParameter> _parameters = [];
    private readonly List<CabBindingIssue> _issues = [];
    internal CabCommandDefinition Definition => definition;
    internal CabCommandSlice Slice => slice;
    internal bool Has(string name) => definition.Parameters.Any(p => p.Parameter.Name == name);
    internal void Issue(string code, string message, CabSourceRange range) => _issues.Add(new(code, message, slice.Syntax(range)));

    internal void Add(string name, CabSourceRange range)
    {
        var rule = definition.Parameters.FirstOrDefault(p => p.Parameter.Name == name);
        if (rule is not null) _parameters.Add(new(rule, slice.Syntax(range), slice.Decode(range)) { Parts = slice.Parts(range) });
    }

    internal void AddAt(string name, IReadOnlyList<CabSourceRange> fields, int index)
    {
        if (index < fields.Count) Add(name, fields[index]);
    }

    internal void AddBinary(string name, CabSourceRange range, string codePage)
    {
        var rule = definition.Parameters.FirstOrDefault(p => p.Parameter.Name == name);
        if (rule is not null) _parameters.Add(new(rule,
            new(CabNodeKind.Parameter, range, name, codePage, slice.Node.Unit, []), null)
            { Parts = [new(range, codePage)] });
    }

    internal CabBoundCommand Build(bool requireInputs = true)
    {
        if (requireInputs)
        foreach (var rule in definition.Parameters.Where(p => p.Required && !p.Parameter.IsAnswer))
            if (_parameters.All(p => p.Rule.Parameter.Name != rule.Parameter.Name)) _parameters.Add(new(rule, null, null));
        return new(definition, slice.Node, jobIndex, _parameters.AsReadOnly(), baseId, _issues.AsReadOnly());
    }
}
