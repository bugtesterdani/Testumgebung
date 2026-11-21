using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabNodeKind { Document, Job, EscapeCommand, ImmediateCommand, LabelCommand, Parameter, ContentField, BinaryBlock, Block, Comment, RawLine }
public enum CabUnit { Millimetres, Inches }
public readonly record struct CabSourceRange(int Offset, int Length)
{
    public int End => checked(Offset + Length);
}

/// <summary>Byte ranges always refer to the original document, including for child nodes.</summary>
public sealed record CabSyntaxNode(CabNodeKind Kind, CabSourceRange Range, string Command,
    string CodePage, CabUnit Unit, IReadOnlyList<CabSyntaxNode> Children);

public sealed record CabParseDiagnostic(string Message, CabSourceRange Range);
public sealed record CabParseOptions(string SetupCodePage = "UTF-8");
public sealed record CabTextChange(CabSourceRange Range, byte[] Replacement);

/// <summary>An immutable source snapshot; parsing and writing do not normalize its bytes.</summary>
public sealed class CabDocument
{
    private readonly byte[] _source;
    public CabSyntaxNode Root { get; }
    public IReadOnlyList<CabParseDiagnostic> Diagnostics { get; }
    public string LineEnding { get; }
    public int Length => _source.Length;

    internal CabDocument(byte[] source, IReadOnlyList<CabSyntaxNode> nodes,
        IReadOnlyList<CabParseDiagnostic> diagnostics, string lineEnding)
    {
        _source = source;
        Root = new(CabNodeKind.Document, new(0, source.Length), "", "", CabUnit.Millimetres, nodes);
        Diagnostics = diagnostics;
        LineEnding = lineEnding;
    }

    public byte[] Bytes(CabSourceRange range)
    {
        Validate(range);
        return _source.AsSpan(range.Offset, range.Length).ToArray();
    }

    public string? Decode(CabSyntaxNode node)
    {
        Validate(node.Range);
        try { return CabEncoding.Get(node.CodePage).GetString(_source, node.Range.Offset, node.Range.Length); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or DecoderFallbackException)
        { return null; }
    }

    public byte[] Write(IEnumerable<CabTextChange>? changes = null)
    {
        if (changes is null) return _source.ToArray();
        using var output = new MemoryStream();
        var offset = 0;
        foreach (var change in changes.OrderBy(c => c.Range.Offset))
        {
            Validate(change.Range);
            if (change.Range.Offset < offset) throw new ArgumentException("Änderungen überlappen sich.", nameof(changes));
            output.Write(_source.AsSpan(offset, change.Range.Offset - offset));
            output.Write(change.Replacement);
            offset = change.Range.End;
        }
        output.Write(_source.AsSpan(offset));
        return output.ToArray();
    }

    public CabTextChange Replace(CabSyntaxNode parameter, string text)
    {
        Validate(parameter.Range);
        return new(parameter.Range, CabEncoding.Get(parameter.CodePage).GetBytes(text));
    }

    public byte[] AppendLine(string text, string codePage = "UTF-8")
    {
        var prefix = _source.Length > 0 && _source[^1] is not (10 or 13) ? LineEnding : "";
        return Write([new(new(Length, 0), CabEncoding.Get(codePage).GetBytes(prefix + text + LineEnding))]);
    }

    private void Validate(CabSourceRange range)
    {
        if (range.Offset < 0 || range.Length < 0 || range.Offset > _source.Length - range.Length)
            throw new ArgumentOutOfRangeException(nameof(range));
    }
}

internal static class CabEncoding
{
    static CabEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal static Encoding Get(string name)
    {
        // The manual uses numeric codepage identifiers as well as UTF-8.
        return int.TryParse(name, out var number)
            ? Encoding.GetEncoding(number, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            : Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
