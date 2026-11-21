using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Decoded character spans retain the actual disjoint source bytes, including multibyte characters.</summary>
public sealed class CabContentSource
{
    private readonly CabDocument _document;
    private readonly List<IReadOnlyList<CabSourceRange>> _characters = [];
    private readonly List<string> _codePages = [];
    public string Text { get; private set; } = "";
    private CabContentSource(CabDocument document) => _document = document;
    public string CodePageAt(int offset) => _codePages[offset];
    public byte[] Bytes(int offset, int length) => Ranges(offset, length).SelectMany(_document.Bytes).ToArray();

    public static CabContentSource Read(CabDocument document, CabBoundParameter parameter)
    {
        var result = new CabContentSource(document);
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
