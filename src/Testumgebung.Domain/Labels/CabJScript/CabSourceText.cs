using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>A text projection with a boundary map back to the unchanged source bytes.</summary>
public sealed class CabSourceText
{
    private readonly int[] _offsets;
    public CabSourceRange Range { get; }
    public string CodePage { get; }
    public string Text { get; }

    internal CabSourceText(CabDocument document, CabSourceRange range, string codePage)
    {
        Range = range; CodePage = codePage;
        var encoding = CabEncoding.Get(codePage);
        var decoded = encoding.GetString(document.Bytes(range));
        var text = new StringBuilder(); var offsets = new List<int> { range.Offset };
        var position = range.Offset;
        for (var i = 0; i < decoded.Length;)
        {
            var count = char.IsSurrogatePair(decoded, i) ? 2 : 1;
            if (decoded[i] == '\r' && i + 1 < decoded.Length && decoded[i + 1] == '\n') count = 2;
            var token = decoded.Substring(i, count);
            var display = token is "\r" or "\r\n" ? "\n" : token.Replace('\u001b', '␛');
            var length = encoding.GetByteCount(token);
            text.Append(display);
            for (var j = 1; j <= display.Length; j++) offsets.Add(j == display.Length ? position + length : position);
            position += length; i += count;
        }
        Text = text.ToString(); _offsets = offsets.ToArray();
    }

    public int ByteOffset(int character) => _offsets[Math.Clamp(character, 0, Text.Length)];
    public int CharacterOffset(int byteOffset)
    {
        var index = Array.FindLastIndex(_offsets, p => p <= byteOffset);
        return Math.Max(0, index);
    }

    public CabTextChange Change(string edited, string lineEnding)
    {
        edited = edited.ReplaceLineEndings("\n");
        var first = 0;
        while (first < Text.Length && first < edited.Length && Text[first] == edited[first]) first++;
        if (first > 0 && first < Text.Length && char.IsLowSurrogate(Text[first])) first--;
        var oldEnd = Text.Length; var newEnd = edited.Length;
        while (oldEnd > first && newEnd > first && Text[oldEnd - 1] == edited[newEnd - 1]) { oldEnd--; newEnd--; }
        if (oldEnd < Text.Length && char.IsLowSurrogate(Text[oldEnd])) { oldEnd++; newEnd++; }
        var replacement = edited[first..newEnd].Replace("\n", lineEnding, StringComparison.Ordinal).Replace('␛', '\u001b');
        return new(new(ByteOffset(first), ByteOffset(oldEnd) - ByteOffset(first)), CabEncoding.Get(CodePage).GetBytes(replacement));
    }
}
