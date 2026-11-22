namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Protocol whitespace and spelling case do not create new symbology definitions.</summary>
internal static class CabBarcodeNames
{
    internal static bool Equal(string left, string right)
        => Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string text) => string.Concat(text.Where(c => c is not (' ' or '\t')));

    internal static int PrefixLength(string text, string declared)
    {
        var offset = 0;
        foreach (var expected in declared.Where(c => c is not (' ' or '\t')))
        {
            offset = SkipWhitespace(text, offset);
            if (offset >= text.Length || char.ToUpperInvariant(text[offset]) != char.ToUpperInvariant(expected)) return -1;
            offset++;
        }
        offset = SkipWhitespace(text, offset);
        return offset == text.Length || text[offset] == '+' ? offset : -1;
    }

    private static int SkipWhitespace(string text, int offset)
    {
        while (offset < text.Length && text[offset] is ' ' or '\t') offset++;
        return offset;
    }
}
