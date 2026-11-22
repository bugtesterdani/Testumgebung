using System.Text.Json;

namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabPropertyTemplate(string Id, string Mode, string DefinitionId, byte[] Bytes, string? Content);

/// <summary>Own synthetic examples, bundled with the application; never manufacturer snippets.</summary>
public static class CabPropertyTemplates
{
    private static readonly Lazy<JsonDocument> Data = new(() => JsonDocument.Parse(
        typeof(CabPropertyTemplates).Assembly.GetManifestResourceStream("Testumgebung.CabJScript.Templates.json")!));

    public static CabPropertyTemplate Get(string id)
    {
        var row = Data.Value.RootElement.GetProperty("samples").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        return new(id, row.GetProperty("mode").GetString()!, row.GetProperty("definition").GetString()!,
            row.GetProperty("bytes").GetBytesFromBase64(), row.GetProperty("content").GetString());
    }

    public static string Example(string id, string parameter, string? declared = null)
    {
        if (declared is not null)
        {
            var value = Data.Value.RootElement.GetProperty("values").EnumerateArray()
                .FirstOrDefault(r => r.GetProperty("id").GetString() == id && r.GetProperty("parameter").GetString() == parameter && r.GetProperty("value").GetString() == declared);
            if (value.ValueKind != JsonValueKind.Undefined) return value.GetProperty("example").GetString()!;
        }
        var row = Data.Value.RootElement.GetProperty("parameters").EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("id").GetString() == id && r.GetProperty("parameter").GetString() == parameter);
        return row.ValueKind == JsonValueKind.Undefined ? "" : row.GetProperty("valid").GetString()!;
    }

    public static byte[] Invocation(string id)
    {
        var template = Get(id);
        if (template.Mode is "content" or "context") throw new InvalidOperationException("Dieser Eintrag ist ein Inhaltsfeld oder eine Kontextregel, kein eigenständiger Befehl.");
        var editor = new CabSourceEditor(template.Bytes);
        var command = editor.Validation.Commands.First(c => c.Definition.Catalog.Id == template.DefinitionId || c.BaseCatalogId == template.DefinitionId);
        if (command.Syntax.Command == "d") throw new InvalidOperationException("Downloads über ‚Ressource hinzufügen‘ mit einer echten Datei anlegen.");
        if (command.Syntax.Kind == CabNodeKind.BinaryBlock) return CabResourceEditor.Frame([], command.Syntax.Command);
        var range = command.Syntax.Range;
        return editor.Document.Bytes(range);
    }
}
