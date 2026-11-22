namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabBoundProperty(CabPropertyDefinition Definition, CabBoundParameter? Source,
    int Occurrence = 0, CabBoundContent? Content = null, CabContentField? Field = null)
{
    public bool IsPresent => Source?.IsPresent == true;
    public string? Value => Source?.Value;
    public bool IsTextEditable => Definition.IsEnabled && Definition.Control != CabPropertyControl.Resource;
    public string EditorValue
    {
        get
        {
            if (IsPresent) return Value ?? (Definition.Control == CabPropertyControl.Resource ? "Binärdaten · über Datei ersetzen" : "Nicht als Text dekodierbare Bytes");
            if (Definition.Control == CabPropertyControl.Answer) return "Keine Druckerantwort gelesen";
            if (Definition.Control == CabPropertyControl.Resource) return "Dateidaten über die Ressourcenverwaltung wählen";
            return CabPropertyTemplates.Example(Definition.Rule.Entry.Id, Definition.Name);
        }
    }
}

public static class CabPropertyBindings
{
    public static IReadOnlyList<CabBoundProperty> ForCommand(CabSourceEditor editor, CabBoundCommand command)
    {
        var generation = editor.Settings.GenerationForJob(command.JobIndex);
        var result = new List<CabBoundProperty>();
        foreach (var rule in command.Definition.Parameters)
        {
            var present = command.Parameters.Where(p => p.Rule.Parameter.Name == rule.Parameter.Name).ToArray();
            if (present.Length == 0) result.Add(new(new(rule, generation), null));
            else for (var index = 0; index < present.Length; index++) result.Add(new(new(present[index].Rule, generation), present[index], index));
        }
        foreach (var extra in command.Parameters.Where(p => !command.Definition.Parameters.Any(r => r.Parameter.Name == p.Rule.Parameter.Name)))
            result.Add(new(new(extra.Rule, generation), extra));
        return result.AsReadOnly();
    }

    public static IReadOnlyList<CabBoundProperty> ForContent(CabSourceEditor editor, CabBoundContent content, CabContentField field)
    {
        var arguments = CabContentArguments.Bind(field);
        var generation = editor.Settings.GenerationForJob(content.Command.JobIndex);
        var result = new List<CabBoundProperty>();
        foreach (var parameter in field.Definition.Parameters)
        {
            var definition = new CabPropertyDefinition(CabDefinitions.ParameterRule(field.Definition.Id, parameter.Name), generation);
            var matches = arguments.Where(a => a.Parameter.Name == parameter.Name).ToArray();
            if (matches.Length == 0) result.Add(new(definition, null, Content: content, Field: field));
            else for (var index = 0; index < matches.Length; index++)
                result.Add(new(definition, BindArgument(content, matches[index], definition.Rule), index, content, field));
        }
        return result.AsReadOnly();
    }

    private static CabBoundParameter? BindArgument(CabBoundContent content, CabContentArgument argument, CabParameterRule rule)
    {
        if (argument.Offset < 0) return null;
        var parts = content.Source.Parts(argument.Offset, argument.Length);
        var range = new CabSourceRange(parts[0].Range.Offset, parts[^1].Range.End - parts[0].Range.Offset);
        var syntax = new CabSyntaxNode(CabNodeKind.Parameter, range, rule.Parameter.Name, parts[0].CodePage, content.Command.Syntax.Unit, []);
        return new(rule, syntax, argument.Value) { Parts = parts };
    }

    public static CabSourceEditor Replace(CabSourceEditor editor, CabBoundProperty property, string value)
    {
        if (!property.IsTextEditable) throw new InvalidOperationException("Dieser Parameter ist eine Ausgabe, Ressource oder für die Generation nicht verfügbar.");
        if (property.Source is null || !property.IsPresent) throw new InvalidOperationException("Optionalen Parameter zuerst hinzufügen.");
        var error = Validate(property, value);
        if (error is not null) throw new ArgumentException(error, nameof(value));
        var changes = CabParameterEditor.Replace(editor.Document, property.Source, value);
        return new(editor.Document.Write(changes), editor.SetupCodePage, editor.Settings);
    }

    public static string? Validate(CabBoundProperty property, string value)
    {
        var rule = property.Definition.Rule;
        if (value.Length == 0 && property.Field is { } field && CabContentGrammar.MayOmit(CabContentGrammar.Number(field), rule.Parameter.Name)) return null;
        var reference = property.Content?.PreviousNames.Contains(value.Trim()) == true;
        if (reference && rule.DataType is CabDataType.Number or CabDataType.Integer) return null;
        return rule.Validate(value, property.Definition.Generation);
    }
}
