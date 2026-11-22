using System.Collections.Frozen;

namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabContentIssue(string Code, string Message, int Offset, int Length, string? CatalogId);
public sealed record CabContentField(CabCatalogEntry Definition, string Name, string? Argument,
    int Offset, int Length, bool Nested);
public sealed record CabContentSyntax(string Text, IReadOnlyList<CabContentField> Fields,
    IReadOnlyList<CabContentIssue> Issues);

/// <summary>Case-sensitive chapter-5 syntax. Unknown brackets remain ordinary text.</summary>
public static class CabContentParser
{
    private static readonly FrozenDictionary<string, CabCatalogEntry> Functions = CabCatalog.Default.Entries
        .Where(e => e.Kind == "inhaltsfeld" && e.Id is not ("JS-5.88" or "JS-5.89"))
        .ToFrozenDictionary(e => FunctionName(e.Syntax[0]), StringComparer.Ordinal);
    public static IReadOnlyList<CabCatalogEntry> Definitions { get; } = Array.AsReadOnly(
        CabCatalog.Default.Entries.Where(e => e.Kind == "inhaltsfeld").ToArray());

    public static CabContentSyntax Parse(string text, IReadOnlySet<string>? previousNames = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var fields = new List<CabContentField>();
        var issues = new List<CabContentIssue>();
        var stack = new Stack<int>();
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '[') stack.Push(index);
            else if (text[index] == ']' && stack.TryPop(out var start))
                Read(text, start, index, previousNames, fields, issues);
        }
        foreach (var start in stack)
        {
            var tail = text[(start + 1)..];
            if (Resolve(tail, previousNames).Entry is { } entry)
                issues.Add(new("content-unclosed", "Inhaltsfeld ist nicht geschlossen.", start, text.Length - start, entry.Id));
        }
        return new(text, fields.OrderBy(f => f.Offset).ToArray(), issues.OrderBy(i => i.Offset).ToArray());
    }

    private static void Read(string text, int start, int end, IReadOnlySet<string>? names,
        List<CabContentField> fields, List<CabContentIssue> issues)
    {
        var body = text[(start + 1)..end];
        var (entry, name, argument) = Resolve(body, names);
        if (entry is null)
        {
            issues.Add(new("content-literal", "Unbekannter Klammerinhalt bleibt wörtlicher Text.", start, end - start + 1, null));
            return;
        }
        var nested = fields.Any(f => f.Offset > start && f.Offset + f.Length <= end);
        fields.Add(new(entry, name, argument, start, end - start + 1, nested));
        if (nested) issues.Add(new("content-nesting", "Inhaltsfelder dürfen nicht ineinander verschachtelt werden.", start, end - start + 1, entry.Id));
    }

    private static (CabCatalogEntry? Entry, string Name, string? Argument) Resolve(string body, IReadOnlySet<string>? names)
    {
        var colon = body.IndexOf(':');
        var name = colon < 0 ? body : body[..colon];
        var argument = colon < 0 ? null : body[(colon + 1)..];
        var lookup = name;
        if (name.StartsWith("OPCUA-", StringComparison.Ordinal)) lookup = "OPCUA";
        if (name.StartsWith("OPCUALOG-", StringComparison.Ordinal)) lookup = "OPCUALOG";
        if (Functions.TryGetValue(lookup, out var function))
        {
            // A bare SER, for example, can name a previous field; the actual SER
            // function requires a colon and arguments (manual example p. 345).
            if (colon < 0 && names?.Contains(name) == true && !CabContentGrammar.AcceptsBare(function))
                return (CabCatalog.Default["JS-5.88"], name, null);
            return (function, name, argument);
        }
        var comma = body.IndexOf(',');
        var reference = comma < 0 ? body : body[..comma];
        if (names?.Contains(reference) == true)
            return (CabCatalog.Default[comma < 0 ? "JS-5.88" : "JS-5.89"], reference, comma < 0 ? null : body[(comma + 1)..]);
        return (null, name, argument);
    }

    private static string FunctionName(string syntax)
    {
        var end = syntax.IndexOfAny([':', '{', ']', '['], 1);
        return syntax[1..end];
    }
}
