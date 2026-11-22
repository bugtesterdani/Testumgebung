namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>A parameter form backed by an actual, synthetic command rather than a syntax illustration.</summary>
public sealed class CabContentForm
{
    private readonly IReadOnlySet<string> _names;
    public string Id { get; }
    public CabSourceEditor Editor { get; }
    public CabBoundContent Content { get; }
    public CabContentField Field { get; }
    public string Text => Content.Source.Text.Substring(Field.Offset, Field.Length);
    public IReadOnlyList<CabBoundProperty> Properties => CabPropertyBindings.ForContent(Editor, Content, Field);

    public CabContentForm(string id, CabPrinterGeneration generation, IReadOnlySet<string>? previousNames = null)
        : this(id, new CabSourceEditor(CabPropertyTemplates.Get(id).Bytes, settings: new() { DefaultGeneration = generation }), previousNames ?? new HashSet<string>(StringComparer.Ordinal)) { }

    private CabContentForm(string id, CabSourceEditor editor, IReadOnlySet<string> names)
    {
        Id = id; Editor = editor; _names = names;
        var match = CabContentValidation.Bind(editor.Document, editor.Validation.Commands)
            .Select(c => c with { Syntax = CabContentParser.Parse(c.Source.Text, c.PreviousNames.Concat(names).ToHashSet(StringComparer.Ordinal)),
                PreviousNames = c.PreviousNames.Concat(names).ToHashSet(StringComparer.Ordinal) })
            .SelectMany(c => c.Syntax.Fields.Where(f => f.Definition.Id == id).Select(f => (Content: c, Field: f))).Last();
        Content = match.Content; Field = match.Field;
    }

    public CabContentForm Change(CabBoundProperty property, string value)
        => new(Id, CabPropertyBindings.Replace(Editor, property, value),
            property.Definition.Name == "name" ? _names.Append(value).ToHashSet(StringComparer.Ordinal) : _names);

    public CabContentPreviewResult Preview(CabPreviewSettings settings)
        => CabDocumentPreview.Evaluate(Editor.Document, settings, Editor.Settings)
            .Last(p => p.Content.Command.Syntax.Range.Offset == Content.Command.Syntax.Range.Offset).Preview;
}
