using System.Text.Json.Serialization;

namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabDiagnosticSeverity { Information, Warning, Error }
public sealed record CabDiagnostic(string Code, CabDiagnosticSeverity Severity, string Message,
    CabSourceRange Range, int Line, int Column, string CatalogId, int FirstPage, int LastPage)
{
    public string ManualReference => CatalogId + ", Handbuch S. " + FirstPage
        + (LastPage == FirstPage ? "" : "–" + LastPage);
}

/// <summary>Saved editor choices; a choice never inserts or executes a printer command.</summary>
public sealed class CabDocumentSettings
{
    public CabPreviewSettings? Preview { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<CabPrinterGeneration>))]
    public CabPrinterGeneration DefaultGeneration { get; set; } = CabPrinterGeneration.X4;
    public Dictionary<int, CabPrinterGeneration> JobGenerations { get; set; } = [];
    public CabPrinterGeneration GenerationForJob(int index) => JobGenerations.GetValueOrDefault(index, DefaultGeneration);
}

public sealed record CabBoundPart(CabSourceRange Range, string CodePage);

public sealed record CabBoundParameter(CabParameterRule Rule, CabSyntaxNode? Syntax, string? Value)
{
    public bool IsPresent => Syntax is not null;
    public IReadOnlyList<CabBoundPart> Parts { get; init; } = [];
}

public sealed record CabBindingIssue(string Code, string Message, CabSyntaxNode Syntax);

public sealed record CabBoundCommand(CabCommandDefinition Definition, CabSyntaxNode Syntax, int JobIndex,
    IReadOnlyList<CabBoundParameter> Parameters, string BaseCatalogId, IReadOnlyList<CabBindingIssue> Issues)
{
    public CabBoundParameter? Parameter(string name) => Parameters.FirstOrDefault(p => p.Rule.Parameter.Name == name);
}

public sealed record CabValidationResult(IReadOnlyList<CabBoundCommand> Commands, IReadOnlyList<CabDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == CabDiagnosticSeverity.Error);
}

internal sealed class CabDiagnosticCollector
{
    private readonly CabDocument _document;
    private readonly int[] _lineStarts;
    private readonly List<CabDiagnostic> _items = [];
    internal IReadOnlyList<CabDiagnostic> Items => _items.AsReadOnly();

    internal CabDiagnosticCollector(CabDocument document)
    {
        _document = document;
        var bytes = document.Write();
        var starts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 13 && i + 1 < bytes.Length && bytes[i + 1] == 10) i++;
            if (bytes[i] is 10 or 13) starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
    }

    internal void Add(string code, CabDiagnosticSeverity severity, string message, CabCatalogEntry entry, CabSyntaxNode source)
    {
        var found = Array.BinarySearch(_lineStarts, source.Range.Offset);
        var lineIndex = found >= 0 ? found : ~found - 1;
        var lineStart = _lineStarts[lineIndex];
        var prefix = source with { Range = new(lineStart, source.Range.Offset - lineStart) };
        var column = (_document.Decode(prefix)?.Length ?? prefix.Range.Length) + 1;
        _items.Add(new(code, severity, message, source.Range, lineIndex + 1, column, entry.Id, entry.FirstPage, entry.LastPage));
    }
}
