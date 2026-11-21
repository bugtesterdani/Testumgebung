using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Logical command bytes with a map back to disjoint text spans around ESC controls.</summary>
internal sealed class CabMappedText
{
    private readonly CabDocument _document;
    private readonly IReadOnlyList<CabBoundPart> _parts;
    private readonly int[] _positions;
    internal string Framing { get; }
    internal CabSourceRange SourceRange { get; }

    internal CabMappedText(CabDocument document, IReadOnlyList<CabBoundPart> parts)
    {
        _document = document; _parts = parts;
        using var bytes = new MemoryStream();
        var positions = new List<int>();
        foreach (var part in parts)
        {
            bytes.Write(document.Bytes(part.Range));
            positions.AddRange(Enumerable.Range(part.Range.Offset, part.Range.Length));
        }
        _positions = positions.ToArray();
        Framing = Encoding.Latin1.GetString(bytes.ToArray());
        SourceRange = parts.Count == 0 ? new(0, 0) : new(parts[0].Range.Offset, parts[^1].Range.End - parts[0].Range.Offset);
    }

    internal int VirtualOffset(int physicalOffset)
    {
        var found = Array.BinarySearch(_positions, physicalOffset);
        return found >= 0 ? found : ~found;
    }

    internal CabSourceRange Range(int start, int length)
    {
        if (length == 0) return new(start < _positions.Length ? _positions[start] : SourceRange.End, 0);
        return new(_positions[start], _positions[start + length - 1] - _positions[start] + 1);
    }

    internal string Protocol(CabSourceRange range)
    {
        var start = VirtualOffset(range.Offset);
        return Framing[start..VirtualOffset(range.End)];
    }

    internal IReadOnlyList<CabBoundPart> Parts(CabSourceRange range)
    {
        if (range.Length == 0) return [new(range, PageAt(range.Offset))];
        return _parts.Where(p => p.Range.Offset < range.End && p.Range.End > range.Offset)
            .Select(p => new CabBoundPart(new(Math.Max(p.Range.Offset, range.Offset),
                Math.Min(p.Range.End, range.End) - Math.Max(p.Range.Offset, range.Offset)), p.CodePage)).ToArray();
    }

    internal string PageAt(int offset)
        => _parts.FirstOrDefault(p => p.Range.End > offset)?.CodePage ?? _parts.LastOrDefault()?.CodePage ?? "UTF-8";

    internal string? Decode(CabSourceRange range) => CabSegmentedText.Decode(_document, Parts(range));
}

internal static class CabSegmentedText
{
    internal static string? Decode(CabDocument document, IReadOnlyList<CabBoundPart> parts)
    {
        try
        {
            var text = new StringBuilder();
            foreach (var group in Groups(parts))
                text.Append(CabEncoding.Get(group[0].CodePage).GetString(Bytes(document, group)));
            return text.ToString();
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or DecoderFallbackException) { return null; }
    }

    internal static byte[] Bytes(CabDocument document, IReadOnlyList<CabBoundPart> parts)
    {
        using var bytes = new MemoryStream();
        foreach (var part in parts) bytes.Write(document.Bytes(part.Range));
        return bytes.ToArray();
    }

    internal static IReadOnlyList<IReadOnlyList<CabBoundPart>> Groups(IReadOnlyList<CabBoundPart> parts)
    {
        var groups = new List<IReadOnlyList<CabBoundPart>>();
        List<CabBoundPart>? group = null;
        foreach (var part in parts)
        {
            if (group is null || !string.Equals(group[0].CodePage, part.CodePage, StringComparison.OrdinalIgnoreCase))
            { group = []; groups.Add(group); }
            group.Add(part);
        }
        return groups;
    }
}
