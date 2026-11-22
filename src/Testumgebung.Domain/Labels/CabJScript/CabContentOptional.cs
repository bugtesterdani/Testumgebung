namespace Testumgebung.Domain.Labels.CabJScript;

public static class CabContentOptional
{
    public static CabSourceEditor Add(CabSourceEditor editor, CabBoundContent content, CabContentField field, CabPropertyDefinition definition, string value)
    {
        if (!definition.IsEnabled) throw new InvalidOperationException("Parameter für diese Generation nicht verfügbar.");
        var error = CabPropertyBindings.Validate(new(definition, null, Content: content, Field: field), value);
        if (error is not null) throw new ArgumentException(error, nameof(value));
        var old = content.Source.Text.Substring(field.Offset, field.Length);
        var arguments = CabContentArguments.Bind(field);
        var attempt = 0;
        foreach (var fragment in Fragments(definition, value))
        for (var at = old.Length - 1; at > 0; at--)
        {
            if (++attempt > 4096) throw new InvalidOperationException("Keine eindeutige Argumentergänzung gefunden.");
            var text = old.Insert(at, fragment);
            var syntax = CabContentParser.Parse(text, content.PreviousNames);
            if (syntax.Fields.Count != 1 || syntax.Issues.Count != 0) continue;
            var candidate = syntax.Fields[0];
            if (candidate.Definition.Id != field.Definition.Id || CabContentGrammar.Errors(candidate, content.PreviousNames).Count != 0) continue;
            var bound = CabContentArguments.Bind(candidate);
            if (!bound.Any(a => a.Parameter.Name == definition.Name && a.Value == value)) continue;
            if (!arguments.All(a => bound.Any(b => b.Parameter.Name == a.Parameter.Name && b.Value == a.Value))) continue;
            return Insert(editor, content, field.Offset + at, fragment);
        }
        throw new InvalidOperationException("Die Ergänzung ist in dieser Inhaltsfeldvariante nicht eindeutig. Passende Variante im Inhaltsfelddialog wählen.");
    }

    private static IEnumerable<string> Fragments(CabPropertyDefinition definition, string value)
    {
        foreach (var separator in new[] { ",", ",,", ":", "", "+" }) yield return separator + value;
        foreach (var choice in definition.Rule.Parameter.Values)
        {
            var example = CabPropertyTemplates.Example(definition.Rule.Entry.Id, definition.Name, choice.Value);
            yield return "," + example;
        }
    }

    private static CabSourceEditor Insert(CabSourceEditor editor, CabBoundContent content, int at, string text)
    {
        var part = content.Source.Parts(at, 0).Single();
        var change = new CabTextChange(part.Range, CabEncoding.Get(part.CodePage).GetBytes(text));
        return new(editor.Document.Write([change]), editor.SetupCodePage, editor.Settings);
    }
}
