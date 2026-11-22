using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Decoded character spans retain the actual disjoint source bytes, including multibyte characters.</summary>
public sealed class CabContentSource
{
    private readonly CabDocument _document;
    private readonly List<IReadOnlyList<CabSourceRange>> _characters = [];
    private readonly List<string> _codePages = [];
    private CabBoundPart _empty = new(new(0, 0), "UTF-8");
    public string Text { get; private set; } = "";
    private CabContentSource(CabDocument document) => _document = document;
    public string CodePageAt(int offset) => _codePages[offset];
    public byte[] Bytes(int offset, int length) => Ranges(offset, length).SelectMany(_document.Bytes).ToArray();

    public static CabContentSource Read(CabDocument document, CabBoundParameter parameter)
    {
        var result = new CabContentSource(document);
        result._empty = parameter.Parts.FirstOrDefault() ?? new(new(0, 0), "UTF-8");
        var text = new StringBuilder();
        foreach (var group in CabSegmentedText.Groups(parameter.Parts)) result.ReadGroup(document, group, text);
        result.Text = text.ToString();
        return result;
    }

    public IReadOnlyList<CabSourceRange> Ranges(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > _characters.Count - length) throw new ArgumentOutOfRangeException(nameof(offset));
        var result = new List<CabSourceRange>();
        foreach (var range in _characters.Skip(offset).Take(length).SelectMany(r => r))
        {
            if (result.Count > 0 && result[^1].End >= range.Offset)
                result[^1] = new(result[^1].Offset, Math.Max(result[^1].End, range.End) - result[^1].Offset);
            else result.Add(range);
        }
        return result.AsReadOnly();
    }

    public IReadOnlyList<CabBoundPart> Parts(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > _characters.Count - length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length == 0)
        {
            if (_characters.Count == 0) return [new(new(_empty.Range.Offset, 0), _empty.CodePage)];
            var at = offset < _characters.Count ? _characters[offset][0].Offset : _characters[^1][^1].End;
            var page = offset < _codePages.Count ? _codePages[offset] : _codePages[^1];
            return [new(new(at, 0), page)];
        }
        var result = new List<CabBoundPart>();
        for (var index = offset; index < offset + length; index++)
            foreach (var range in _characters[index]) AppendPart(result, range, _codePages[index]);
        return result.AsReadOnly();
    }

    private static void AppendPart(List<CabBoundPart> parts, CabSourceRange range, string page)
    {
        if (parts.Count > 0 && parts[^1].CodePage == page && parts[^1].Range.End >= range.Offset)
        {
            var previous = parts[^1].Range;
            parts[^1] = new(new(previous.Offset, Math.Max(previous.End, range.End) - previous.Offset), page);
        }
        else parts.Add(new(range, page));
    }

    private void ReadGroup(CabDocument document, IReadOnlyList<CabBoundPart> group, StringBuilder text)
    {
        var decoder = CabEncoding.Get(group[0].CodePage).GetDecoder();
        var positions = group.SelectMany(p => Enumerable.Range(p.Range.Offset, p.Range.Length)).ToArray();
        var bytes = CabSegmentedText.Bytes(document, group);
        var pending = 0;
        Span<char> chars = stackalloc char[2];
        for (var index = 0; index < bytes.Length; index++)
        {
            var count = decoder.GetChars(bytes.AsSpan(index, 1), chars, index == bytes.Length - 1);
            if (count == 0) continue;
            // A character can cross ESC-separated byte parts. Keep every byte as a separate
            // range until Ranges merges adjacent bytes, without swallowing the control.
            for (var c = 0; c < count; c++)
            {
                text.Append(chars[c]);
                _characters.Add(positions[pending..(index + 1)].Select(p => new CabSourceRange(p, 1)).ToArray());
                _codePages.Add(group[0].CodePage);
            }
            pending = index + 1;
        }
    }
}
