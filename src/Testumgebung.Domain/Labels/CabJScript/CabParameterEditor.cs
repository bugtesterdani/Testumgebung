namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Replaces only bound text spans, preserving intervening printer controls.</summary>
public static class CabParameterEditor
{
    public static IReadOnlyList<CabTextChange> Replace(CabDocument document, CabBoundParameter parameter, string text)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(text);
        if (!parameter.IsPresent || parameter.Parts.Count == 0 || Opaque(parameter.Rule))
            throw new ArgumentException("Der Parameter besitzt keinen bearbeitbaren Quellbereich.", nameof(parameter));
        var groups = CabSegmentedText.Groups(parameter.Parts);
        var changes = new List<CabTextChange>();
        var offset = 0;
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var encoding = CabEncoding.Get(group[0].CodePage);
            var original = encoding.GetString(CabSegmentedText.Bytes(document, group));
            var count = index == groups.Count - 1 ? text.Length - offset : Math.Min(original.Length, text.Length - offset);
            // Never divide a surrogate pair between code pages.
            if (count > 0 && offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            var bytes = encoding.GetBytes(text.Substring(offset, count));
            AddParts(changes, group, bytes);
            offset += count;
        }
        return changes.AsReadOnly();
    }

    private static bool Opaque(CabParameterRule rule) => rule.DataType == CabDataType.Binary && rule.Pattern is null;

    private static void AddParts(List<CabTextChange> changes, IReadOnlyList<CabBoundPart> parts, byte[] bytes)
    {
        var offset = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var count = index == parts.Count - 1 ? bytes.Length - offset : Math.Min(part.Range.Length, bytes.Length - offset);
            changes.Add(new(part.Range, bytes.AsSpan(offset, count).ToArray()));
            offset += count;
        }
    }
}
