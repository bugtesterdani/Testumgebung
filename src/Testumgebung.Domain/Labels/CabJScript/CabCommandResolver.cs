using System.Collections.Frozen;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabCommandResolver
{
    private static readonly FrozenDictionary<string, string> Roots = CabCatalog.Default.Entries
        .Where(e => e.Kind is "esc" or "sofortbefehl" or "etikettbefehl")
        .ToFrozenDictionary(e => e.Kind == "esc" ? e.Command!.Replace("ESC", "\u001b", StringComparison.Ordinal) : e.Command!, e => e.Id, StringComparer.Ordinal);
    private static readonly IReadOnlyList<(string Type, string Id)> BarcodeTypes = CabCatalog.Default.Entries.Where(e => e.Kind == "barcode")
        .SelectMany(e => e.Parameters.Where(p => p.Name == "type").SelectMany(p => p.Values).Select(v => (v.Value, e.Id)))
        .OrderByDescending(v => v.Value.Length).Select(v => (v.Value, v.Id)).ToArray();

    internal static (string Id, string BaseId)? Resolve(CabCommandSlice slice)
    {
        var special = Block(slice.Node);
        if (special is not null) return (special, special);
        if (!Roots.TryGetValue(slice.Node.Command, out var root)) return null;
        var id = slice.Node.Command switch
        {
            "B" => Barcode(slice) ?? root,
            "E" => Extension(slice) ?? root,
            "G" => Graphic(slice) ?? root,
            "M" => Memory(slice) ?? root,
            _ => root
        };
        return (id, root);
    }

    private static string? Block(CabSyntaxNode node) => node.Command switch
    {
        "ESC." => "JS-2.3", "ESC:" => "JS-2.4", "<BASE64>" => "JS-3.3",
        "<ABC>" => "JS-6.2", "<ENCRYPTED LABEL:" => "JS-6.3", "<ENCRYPTED JOB>" => "JS-6.4", ";" => "JS-6.1",
        _ => null
    };

    internal static (CabSourceRange Header, CabSourceRange? Data) HeaderData(CabCommandSlice slice)
    {
        var parts = slice.Split(slice.Body, ';');
        var data = parts.Count > 1 ? slice.TailAfter(parts[0], 1) : (CabSourceRange?)null;
        return (parts[0], data);
    }

    private static string? Barcode(CabCommandSlice slice)
    {
        var fields = slice.Split(HeaderData(slice).Header, ',');
        if (fields.Count < 4) return null;
        var type = slice.Protocol(fields[3]).Trim();
        foreach (var candidate in BarcodeTypes)
            if (type == candidate.Type || type.StartsWith(candidate.Type + "+", StringComparison.Ordinal)) return candidate.Id;
        return null;
    }

    private static string? Extension(CabCommandSlice slice)
    {
        var type = slice.Protocol(HeaderData(slice).Header).Trim();
        if (type.StartsWith("OPCUA-", StringComparison.Ordinal)) return "JS-4.5.3";
        return type switch { "DBF" => "JS-4.5.1", "LOG" => "JS-4.5.2", "OPCUA" => "JS-4.5.3", "RFID" => "JS-4.5.4", "SQL" => "JS-4.5.5", "SQLITE" => "JS-4.5.6", "TMP" => "JS-4.5.7", _ => null };
    }

    private static string? Graphic(CabCommandSlice slice)
    {
        var data = HeaderData(slice).Data;
        if (data is null) return null;
        var text = slice.Protocol(data.Value).TrimStart();
        if (text.Length < 2 || text[1] != ':') return null;
        return text[0] switch { 'C' => "JS-4.7.1", 'L' => "JS-4.7.2", 'R' => "JS-4.7.3", _ => null };
    }

    private static string? Memory(CabCommandSlice slice)
    {
        var body = slice.Protocol(slice.Body);
        if (body.Length == 0) return null;
        return body[0] switch { 'c' => "JS-4.11.1", 'd' => "JS-4.11.2", 'f' => "JS-4.11.3", 'l' => "JS-4.11.4", 'r' => "JS-4.11.5", 's' => "JS-4.11.6", 'u' => "JS-4.11.7", _ => null };
    }
}
