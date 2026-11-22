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
            while (offset < text.Length && text[offset] is ' ' or '\t') offset++;
            if (offset >= text.Length || char.ToUpperInvariant(text[offset]) != char.ToUpperInvariant(expected)) return -1;
            offset++;
        }
        while (offset < text.Length && text[offset] is ' ' or '\t') offset++;
        return offset == text.Length || text[offset] == '+' ? offset : -1;
    }
}
