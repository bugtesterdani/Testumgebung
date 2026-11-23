using System.Net;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Blocks;

/// <summary>Substitutes individual placeholder spans; surrounding bytes and printer controls stay in place.</summary>
internal sealed class CabSyntaxTemplate(CabStoredLabel stored)
{
    internal byte[] Render(IStepContext context)
    {
        if (stored.HasUndecodablePlaceholders) throw new FormatException(CabStoredLabel.PlaceholderEncodingError);
        var changes = new List<CabTextChange>();
        foreach (var content in CabContentValidation.Bind(stored.Document, stored.Validation.Commands).Where(Editable))
            ReplaceContent(content, context, changes);
        var output = stored.Document.Write(changes);
        if (changes.Count > 0) CheckResult(output);
        return output;
    }

    private static bool Editable(CabBoundContent content)
        => content.Parameter.Rule.DataType != CabDataType.Binary || content.Parameter.Rule.Pattern is not null;

    private void ReplaceContent(CabBoundContent content, IStepContext context, List<CabTextChange> changes)
    {
        var text = content.Source.Text;
        if (!text.Contains('{')) return;
        for (var index = 0; index < text.Length; index++)
        {
            var length = TokenLength(text, index);
            if (length == 0) continue;
            var token = text.Substring(index, length);
            var value = token == "{{" ? "{" : token == "}}" ? "}" : CommunicationBlocks.Interpolate(token, context,
                v => FormatValue(v, content.Command.Syntax.Command == "W"));
            var part = content.Parameter with { Parts = content.Source.Parts(index, length) };
            changes.AddRange(CabParameterEditor.Replace(stored.Document, part, value));
            index += length - 1;
        }
    }

    private static int TokenLength(string text, int index)
    {
        if (index + 1 < text.Length && (text[index] == '{' && text[index + 1] == '{' || text[index] == '}' && text[index + 1] == '}')) return 2;
        if (text[index] != '{') return 0;
        var end = text.IndexOf('}', index + 1);
        if (end < 0) throw new FormatException("cab-Platzhalter ohne schließende geschweifte Klammer.");
        return end - index + 1;
    }

    private static string FormatValue(object value, bool html)
    {
        var text = CommunicationBlocks.Text(value);
        if (text.Any(c => char.IsControl(c) || c is '[' or ']'))
            throw new FormatException("cab-Etikettvariable enthält ein Steuerzeichen oder eine eckige Klammer.");
        return html ? WebUtility.HtmlEncode(text) : text;
    }

    private void CheckResult(byte[] output)
    {
        var document = CabParser.Parse(output, new(stored.SetupCodePage));
        var result = CabValidator.Validate(document, stored.Settings);
        if (!result.Commands.Select(Signature).SequenceEqual(stored.Validation.Commands.Select(Signature)))
            throw new FormatException("Ein cab-Platzhalter verändert die Befehlsstruktur.");
        var existing = stored.Validation.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count());
        var error = result.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error).GroupBy(d => d.Code)
            .FirstOrDefault(g => g.Count() > existing.GetValueOrDefault(g.Key));
        if (error is not null) throw new FormatException("cab-Etikettvariable: " + error.First().Message);
        CheckGs1(document, result);
    }

    private static void CheckGs1(CabDocument document, CabValidationResult result)
    {
        foreach (var content in CabContentValidation.Bind(document, result.Commands))
        {
            if (content.Command.Definition.Catalog.Kind != "barcode" || content.Syntax.Fields.Count != 0) continue;
            var type = content.Command.Parameter("type")?.Value ?? "";
            // Parenthesized AI data is the existing designer contract. Raw printer data and
            // native dynamic fields remain governed by JScript rather than the designer's AI subset.
            if (type.StartsWith("GS1", StringComparison.OrdinalIgnoreCase) && content.Source.Text.StartsWith('('))
                _ = Gs1LabelData.Cab(content.Source.Text);
        }
    }

    private static string Signature(CabBoundCommand command) => command.Syntax.Command + "/" + command.Definition.Catalog.Id;
}
