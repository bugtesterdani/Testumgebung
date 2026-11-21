namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabParameterReader
{
    internal static IReadOnlyList<CabSyntaxNode> Read(byte[] data, int first, int end, string page, CabUnit unit)
    {
        var result = new List<CabSyntaxNode>();
        var command = (char)data[first];
        var start = first + 1;
        if (start < end && data[start] == ':' && "TBW".Contains(command))
        {
            var separator = Array.IndexOf(data, (byte)';', start, end - start);
            if (separator < 0) return result.AsReadOnly();
            result.Add(Parameter(start + 1, separator - start - 1, "name", data, page, unit));
            start = separator + 1;
        }
        while (start < end && data[start] is 32 or 9) start++;
        if (start == end) return result.AsReadOnly();
        var contentSeparator = "TBWR".Contains(command)
            ? Array.IndexOf(data, (byte)';', start, end - start) : -1;
        var headerEnd = contentSeparator < 0 ? end : contentSeparator;
        var depth = 0;
        var field = start;
        var index = 0;
        for (var at = start; at < headerEnd; at++)
        {
            if (data[at] == '[') depth++;
            else if (data[at] == ']' && depth > 0) depth--;
            else if (depth == 0 && data[at] is (byte)',' or (byte)';')
            {
                result.Add(Parameter(field, at - field, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture), data, page, unit));
                field = at + 1;
            }
        }
        result.Add(Parameter(field, headerEnd - field, index.ToString(System.Globalization.CultureInfo.InvariantCulture), data, page, unit));
        if (contentSeparator >= 0)
            result.Add(Parameter(contentSeparator + 1, end - contentSeparator - 1, "data", data, page, unit));
        return result.AsReadOnly();
    }

    private static CabSyntaxNode Parameter(int start, int length, string name, byte[] data, string page, CabUnit unit)
    {
        var children = new List<CabSyntaxNode>();
        var end = start + length;
        var open = -1;
        var depth = 0;
        for (var at = start; at < end; at++)
        {
            if (data[at] == '[')
            {
                if (depth++ == 0) open = at;
            }
            else if (data[at] == ']' && depth > 0 && --depth == 0)
                children.Add(new(CabNodeKind.ContentField, new(open, at - open + 1), "[]", page, unit, []));
        }
        if (depth > 0) children.Add(new(CabNodeKind.ContentField, new(open, end - open), "[", page, unit, []));
        return new(CabNodeKind.Parameter, new(start, length), name, page, unit, children.AsReadOnly());
    }
}
