using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabBoundContent(CabBoundCommand Command, CabBoundParameter Parameter, CabContentSource Source,
    CabContentSyntax Syntax, IReadOnlySet<string> PreviousNames);

public static class CabContentValidation
{
    public static IReadOnlyList<CabBoundContent> Bind(CabDocument document, IReadOnlyList<CabBoundCommand> commands)
    {
        var result = new List<CabBoundContent>();
        foreach (var job in commands.GroupBy(c => c.JobIndex))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in job)
            {
                foreach (var parameter in command.Parameters.Where(IsContent))
                {
                    try
                    {
                        var source = CabContentSource.Read(document, parameter);
                        result.Add(new(command, parameter, source, CabContentParser.Parse(source.Text, names), names.ToHashSet(StringComparer.Ordinal)));
                    }
                    catch (Exception error) when (error is ArgumentException or NotSupportedException or DecoderFallbackException)
                    { /* The existing parameter-encoding diagnostic covers undecodable source bytes. */ }
                }
                if (command.Syntax.Command is "T" or "B" or "W" && CabDocumentRules.Declaration(command)?.Value is { Length: > 0 } name) names.Add(name);
            }
        }
        return result.AsReadOnly();
    }

    internal static bool IsContent(CabBoundParameter parameter) => parameter.IsPresent && parameter.Parts.Count > 0
        && (parameter.Rule.DataType != CabDataType.Binary || parameter.Rule.Pattern is not null)
        && (parameter.Rule.Parameter.Name is "text" or "data"
            || parameter.Rule.Entry.Id == "JS-4.14" && parameter.Rule.Parameter.Name == "value");

    internal static void Validate(CabDocument document, IReadOnlyList<CabBoundCommand> commands,
        CabDocumentSettings settings, CabDiagnosticCollector diagnostics)
    {
        foreach (var content in Bind(document, commands)) Check(content, settings, diagnostics);
    }

    private static void Check(CabBoundContent content, CabDocumentSettings settings, CabDiagnosticCollector diagnostics)
    {
        var generation = settings.GenerationForJob(content.Command.JobIndex);
        foreach (var issue in content.Syntax.Issues)
        {
            var entry = issue.CatalogId is { } id ? CabCatalog.Default[id] : content.Command.Definition.Catalog;
            Add(content, issue.Offset, issue.Length, issue.Code, issue.Message, entry,
                issue.Code == "content-literal" ? CabDiagnosticSeverity.Information : CabDiagnosticSeverity.Error, diagnostics);
        }
        foreach (var field in content.Syntax.Fields)
        {
            var ranges = content.Source.Ranges(field.Offset, field.Length);
            foreach (var range in ranges)
                CabValidator.Unavailable(field.Definition.Availability(generation), field.Name, field.Definition,
                    content.Parameter.Syntax! with { Range = range }, diagnostics);
            foreach (var error in CabContentGrammar.Errors(field, content.PreviousNames, content.Command.Definition.Catalog.Kind == "barcode"))
                Add(content, field.Offset, field.Length, "content-parameter", error, field.Definition, CabDiagnosticSeverity.Error, diagnostics);
            if (field.Definition.Id == "JS-5.99" && CabContentGrammar.Arguments(field).Count == 3 && generation != CabPrinterGeneration.X4)
                Add(content, field.Offset, field.Length, "generation", "Eigener SPLIT-Trenner ist nur auf X4 verfügbar.", field.Definition, CabDiagnosticSeverity.Warning, diagnostics);
        }
    }

    private static void Add(CabBoundContent content, int offset, int length, string code, string message,
        CabCatalogEntry entry, CabDiagnosticSeverity severity, CabDiagnosticCollector diagnostics)
    {
        foreach (var range in content.Source.Ranges(offset, length))
            diagnostics.Add(code, severity, message, entry, content.Parameter.Syntax! with { Range = range });
    }
}
