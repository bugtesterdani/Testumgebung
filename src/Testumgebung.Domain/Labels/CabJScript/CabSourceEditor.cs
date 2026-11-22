using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabSourceSection(CabSourceRange Range, CabSourceText? Editable, string Summary)
{
    public bool IsProtected => Editable is null;
}

/// <summary>Protected source sections never expose opaque payload as editable text.</summary>
public sealed class CabSourceEditor
{
    public CabDocument Document { get; }
    public CabValidationResult Validation { get; }
    public IReadOnlyList<CabSourceSection> Sections { get; }
    public CabDocumentSettings Settings { get; }
    public string SetupCodePage { get; }

    public CabSourceEditor(byte[] bytes, string setupCodePage = "UTF-8", CabDocumentSettings? settings = null)
    {
        SetupCodePage = setupCodePage; Settings = settings ?? new();
        Document = CabParser.Parse(bytes, new(setupCodePage));
        Validation = CabValidator.Validate(Document, Settings);
        Sections = CreateSections();
    }

    public CabSourceEditor Edit(int section, string text)
    {
        var editable = Sections[section].Editable ?? throw new InvalidOperationException("Geschützter Block ist nicht als Text bearbeitbar.");
        var change = editable.Change(text, Document.LineEnding);
        return new(Document.Write([change]), SetupCodePage, Settings);
    }

    private IReadOnlyList<CabSourceSection> CreateSections()
    {
        var result = new List<CabSourceSection>();
        foreach (var node in Physical(Document.Root))
        {
            var section = ReadSection(node);
            if (CanMerge(result.LastOrDefault(), section))
            {
                var previous = result[^1];
                var range = new CabSourceRange(previous.Range.Offset, section.Range.End - previous.Range.Offset);
                result[^1] = new(range, new(Document, range, node.CodePage), "");
            }
            else result.Add(section);
        }
        if (result.Count == 0) result.Add(new(new(0, 0), new(Document, new(0, 0), SetupCodePage), ""));
        return result.AsReadOnly();
    }

    private static bool CanMerge(CabSourceSection? previous, CabSourceSection next)
        => previous?.Editable is not null && next.Editable is not null
            && previous.Range.End == next.Range.Offset && previous.Editable.CodePage == next.Editable.CodePage;

    private CabSourceSection ReadSection(CabSyntaxNode node)
    {
        var opaque = node.Kind == CabNodeKind.BinaryBlock || node.Command.StartsWith("<ENCRYPTED", StringComparison.Ordinal);
        if (!opaque)
        {
            try
            {
                var text = new CabSourceText(Document, node.Range, node.CodePage);
                if (!text.Text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
                    return new(node.Range, text, "");
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or DecoderFallbackException) { }
        }
        var preceding = Validation.Commands.LastOrDefault(c => c.Syntax.Range.End <= node.Range.Offset);
        var name = BlockName(node, preceding, opaque);
        return new(node.Range, null, $"{name} · {node.Range.Length:N0} Bytes · geschützt");
    }

    private static string BlockName(CabSyntaxNode node, CabBoundCommand? preceding, bool opaque)
    {
        if (!opaque) return "Nicht als Text darstellbar (" + node.CodePage + ")";
        if (node.Kind == CabNodeKind.BinaryBlock && preceding?.Syntax.Command == "d")
            return preceding.Parameter("type")?.Value + " " + preceding.Parameter("name")?.Value;
        return node.Command;
    }

    internal static IEnumerable<CabSyntaxNode> Physical(CabSyntaxNode node)
        => node.Children.SelectMany(n => n.Kind == CabNodeKind.Job ? Physical(n) : new[] { n });
}
