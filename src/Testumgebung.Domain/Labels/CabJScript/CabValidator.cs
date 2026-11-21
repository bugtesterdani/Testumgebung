namespace Testumgebung.Domain.Labels.CabJScript;

public static class CabValidator
{
    public static CabValidationResult Validate(CabDocument document, CabDocumentSettings? settings = null)
    {
        settings ??= new();
        var diagnostics = new CabDiagnosticCollector(document);
        var commands = CabSemanticBinder.Bind(document, diagnostics);
        var dynamicContents = CabBarcodeRules.DynamicContents(commands);
        foreach (var command in commands) CheckCommand(command, settings, dynamicContents.Contains(command.Syntax.Range), diagnostics);
        ParseDiagnostics(document, commands, diagnostics);
        CabDocumentRules.Validate(commands, settings, dynamicContents, diagnostics);
        return new(commands, diagnostics.Items);
    }

    private static void CheckCommand(CabBoundCommand command, CabDocumentSettings settings, bool dynamicContent, CabDiagnosticCollector diagnostics)
    {
        var generation = settings.GenerationForJob(command.JobIndex);
        var entry = command.Definition.Catalog;
        Unavailable(entry.Availability(generation), entry.Title, entry, command.Syntax, diagnostics);
        foreach (var parameter in command.Parameters) CheckParameter(parameter, command, generation, dynamicContent, diagnostics);
        foreach (var issue in command.Issues)
            diagnostics.Add(issue.Code, CabDiagnosticSeverity.Error, issue.Message, entry, issue.Syntax);
    }

    private static void CheckParameter(CabBoundParameter parameter, CabBoundCommand command,
        CabPrinterGeneration generation, bool dynamicContent, CabDiagnosticCollector diagnostics)
    {
        var rule = parameter.Rule;
        var source = parameter.Syntax ?? command.Syntax;
        var availability = rule.Parameter.Availability(generation, rule.Entry.Availability(generation));
        if (parameter.IsPresent) Unavailable(availability, rule.Parameter.Name, rule.Entry, source, diagnostics);
        if (parameter.IsPresent && rule.DataType == CabDataType.Binary && rule.Pattern is null) return;
        if (parameter.IsPresent && parameter.Value is null)
        {
            diagnostics.Add("parameter-encoding", CabDiagnosticSeverity.Warning, "Parameterbytes sind nicht vollständig dekodierbar und bleiben erhalten.", rule.Entry, source);
            return;
        }
        if (dynamicContent && rule.Parameter.Name == "text") return;
        var error = rule.Validate(parameter.Value, generation);
        if (error is not null) diagnostics.Add("parameter-value", CabDiagnosticSeverity.Error, rule.Parameter.Name + ": " + error, rule.Entry, source);
        foreach (var value in CabSelectedValues.Find(rule, parameter.Value))
            Unavailable(value.Availability(generation, availability), value.Value, rule.Entry, source, diagnostics);
    }

    internal static void Unavailable(CabAvailability availability, string value, CabCatalogEntry entry,
        CabSyntaxNode source, CabDiagnosticCollector diagnostics)
    {
        if (availability == CabAvailability.Unsupported)
            diagnostics.Add("generation", CabDiagnosticSeverity.Warning, value + " ist für diese Druckergeneration nicht verfügbar.", entry, source);
    }

    private static void ParseDiagnostics(CabDocument document, IReadOnlyList<CabBoundCommand> commands, CabDiagnosticCollector diagnostics)
    {
        foreach (var diagnostic in document.Diagnostics)
        {
            if (diagnostic.Message.StartsWith("Unbekann", StringComparison.Ordinal)
                || diagnostic.Message.StartsWith("Bytes sind", StringComparison.Ordinal)) continue;
            var owner = commands.Where(c => c.Syntax.Range.Offset <= diagnostic.Range.Offset && c.Syntax.Range.End >= diagnostic.Range.End)
                .MinBy(c => c.Syntax.Range.Length);
            var source = new CabSyntaxNode(CabNodeKind.RawLine, diagnostic.Range, "", owner?.Syntax.CodePage ?? "UTF-8", CabUnit.Millimetres, []);
            var severity = diagnostic.Message.StartsWith("Verschlüsselter Etikettinhalt", StringComparison.Ordinal)
                ? CabDiagnosticSeverity.Information : CabDiagnosticSeverity.Error;
            diagnostics.Add("syntax", severity, diagnostic.Message, owner?.Definition.Catalog ?? CabCatalog.Default["JS-1.3"], source);
        }
    }
}

internal static class CabSelectedValues
{
    internal static IEnumerable<CabCatalogValue> Find(CabParameterRule rule, string? input)
    {
        if (input is null) yield break;
        foreach (var value in rule.Parameter.Values)
        {
            var single = rule with { Parameter = rule.Parameter with { Values = [value] }, AdditionalValues = [] };
            if (Matches(single, value, input) || Tokens(input).Any(t => Matches(single, value, t)))
                yield return value;
        }
    }

    private static bool Matches(CabParameterRule rule, CabCatalogValue value, string token)
    {
        if (rule.Entry.Id == "JS-4.1" && value.Value == "[<VAR>]" && CabAmountOptions.IsBuiltinToken(token)) return false;
        return CabChoiceLexicon.Matches(rule, token, CabChoiceMode.Single);
    }

    private static IEnumerable<string> Tokens(string input)
    {
        foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries)) yield return part;
        foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(input, @"\[[^\]]+\]|\+[^+]+", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            yield return part.Value;
    }
}
