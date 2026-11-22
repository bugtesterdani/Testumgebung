using System.Text.RegularExpressions;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

public sealed record CabGraphicContentValue(string Text, CabPreviewValue Value, IReadOnlyList<string> Notes, bool Hidden = false);

/// <summary>Expands examples in a private document; the source document never receives preview substitutions.</summary>
public static class CabGraphicContent
{
    public static IReadOnlyDictionary<int, CabGraphicContentValue> Evaluate(CabSourceEditor editor, LabelLayout layout)
    {
        var changes = new List<CabTextChange>();
        var unresolved = new HashSet<int>();
        foreach (var content in CabContentValidation.Bind(editor.Document, editor.Validation.Commands).Where(Editable))
        {
            var legacy = layout.Elements.FirstOrDefault(e => e.CabSourceOffset == content.Command.Syntax.Range.Offset);
            var examples = legacy?.ExampleValues ?? layout.CabGraphicExampleValues ?? "Kennung=" + (legacy?.Preview ?? "SN-123");
            var text = Expand(content.Source.Text, examples, out var missing);
            if (missing) unresolved.Add(content.Command.Syntax.Range.Offset);
            if (text != content.Source.Text) changes.AddRange(CabParameterEditor.Replace(editor.Document, content.Parameter, text));
        }
        var document = CabParser.Parse(editor.Document.Write(changes), new(editor.SetupCodePage));
        var commands = CabValidator.Validate(document, editor.Settings).Commands;
        if (!commands.Select(Signature).SequenceEqual(editor.Validation.Commands.Select(Signature)))
            throw new InvalidDataException("Ein Beispielwert verändert Befehls- oder HTML-Grenzen. Bitte reine Inhaltswerte verwenden.");
        var offsets = commands.Select((c, index) => (c.Syntax.Range.Offset, Original: editor.Validation.Commands[index].Syntax.Range.Offset)).ToDictionary(p => p.Offset, p => p.Original);
        var previews = CabDocumentPreview.Evaluate(document, editor.Settings.Preview ?? new(), editor.Settings);
        return MapFields(commands, previews, offsets, unresolved);
    }

    private static IReadOnlyDictionary<int, CabGraphicContentValue> MapFields(IReadOnlyList<CabBoundCommand> commands,
        IReadOnlyList<CabPreviewField> previews, IReadOnlyDictionary<int, int> offsets, IReadOnlySet<int> unresolved)
    {
        var result = new Dictionary<int, CabGraphicContentValue>();
        foreach (var preview in previews)
        {
            var command = preview.Content.Command;
            var target = command.Syntax.Command == "R" ? ReplacementTarget(commands, command, preview.Name) : command;
            if (target is null || !offsets.TryGetValue(target.Syntax.Range.Offset, out var original)) continue;
            result[original] = Value(preview, unresolved.Contains(offsets[command.Syntax.Range.Offset]));
        }
        return result;
    }

    private static CabBoundCommand? ReplacementTarget(IReadOnlyList<CabBoundCommand> commands, CabBoundCommand replacement, string? name)
        => commands.LastOrDefault(c => c.JobIndex == replacement.JobIndex && c.Syntax.Range.Offset < replacement.Syntax.Range.Offset
            && c.Syntax.Command is "T" or "W" or "B" && c.Parameter("name")?.Value == name);

    private static string Signature(CabBoundCommand command) => command.Syntax.Command + "/" + command.Definition.Catalog.Id;
    private static bool Editable(CabBoundContent content) => content.Parameter.Rule.DataType != CabDataType.Binary || content.Parameter.Rule.Pattern is not null;
    private static CabGraphicContentValue Value(CabPreviewField field, bool missing)
    {
        var value = field.Preview.Value with { Placeholder = field.Preview.Value.Placeholder || missing };
        var notes = field.Preview.Issues.Select(i => i.Message).ToList();
        if (missing) notes.Add("Für mindestens einen Platzhalter fehlt ein Beispielwert.");
        if (field.Preview.Hidden) notes.Add("Das Inhaltsfeld ist im gewählten Beispielszenario unsichtbar.");
        return new(field.Preview.Hidden ? "" : field.Preview.Text, value, notes, field.Preview.Hidden);
    }

    public static string Expand(string text, string examples, out bool missing)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in examples.ReplaceLineEndings("\n").Split('\n'))
        {
            var at = line.IndexOf('=');
            if (at <= 0) continue;
            var value = line[(at + 1)..];
            if (value.Any(char.IsControl)) throw new InvalidDataException("Beispielwerte dürfen keine Steuerzeichen enthalten; Inhaltsfelder wie [U:...] bleiben verfügbar.");
            values[line[..at].Trim()] = value;
        }
        var unresolved = false;
        var expanded = Regex.Replace(text, @"\{\{|\}\}|\{([^{}]+)\}", match =>
        {
            if (match.Value == "{{") return "{";
            if (match.Value == "}}") return "}";
            if (values.TryGetValue(match.Groups[1].Value.Trim(), out var value)) return value;
            unresolved = true; return match.Value;
        }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        missing = unresolved; return expanded;
    }
}
