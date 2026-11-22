namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabCompletionKind { Command, Parameter, Value }
public sealed record CabCompletionItem(string CatalogId, CabCompletionKind Kind, string? Parameter, string Text,
    string Description, string Insertion, CabAvailability Availability, string Tooltip)
{
    public override string ToString() => $"{CatalogId} · {KindName} · {Parameter} · {Text} · {AvailabilityName}";
    private string KindName => Kind switch { CabCompletionKind.Command => "Befehl", CabCompletionKind.Parameter => "Parameter", _ => "Wert" };
    private string AvailabilityName => Availability switch { CabAvailability.Supported => "verfügbar", CabAvailability.Unsupported => "nicht verfügbar", _ => "Verfügbarkeit ungeklärt" };
}

/// <summary>The same catalog supplies discovery, ordered parameters and generation annotations.</summary>
public static class CabSourceCatalog
{
    public static IReadOnlyList<CabCompletionItem> Complete(CabPrinterGeneration generation, string query = "")
        => CabCatalog.Default.Entries.SelectMany(e => Items(e, generation))
            .Where(i => string.IsNullOrEmpty(query) || (i.CatalogId + " " + i.Parameter + " " + i.Text + " " + i.Description)
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static string Tooltip(CabCatalogEntry entry)
        => entry.Id + " · " + entry.Title + "\n" + string.Join("\n", entry.Syntax)
            + $"\nHandbuch S. {entry.FirstPage}–{entry.LastPage}";

    private static IEnumerable<CabCompletionItem> Items(CabCatalogEntry entry, CabPrinterGeneration generation)
    {
        var inherited = entry.Availability(generation);
        var tooltip = Tooltip(entry);
        var insertion = Insertion(entry);
        if (entry.Kind == "esc") insertion = insertion.Replace("ESC", "␛e", StringComparison.Ordinal);
        yield return new(entry.Id, CabCompletionKind.Command, null, entry.Command ?? entry.Title,
            entry.Title, insertion, inherited, tooltip);
        foreach (var parameter in entry.Parameters)
        {
            var available = parameter.Availability(generation, inherited);
            var description = parameter.Description + (parameter.IsAnswer ? " (Antwortfeld, kein Eingabeparameter)" : "");
            var help = tooltip + "\n" + parameter.Spelling + ": " + description;
            yield return new(entry.Id, CabCompletionKind.Parameter, parameter.Name, parameter.Spelling,
                description, parameter.IsAnswer ? "" : parameter.Spelling, available, help);
            foreach (var value in parameter.Values)
                yield return new(entry.Id, CabCompletionKind.Value, parameter.Name, value.Value,
                    value.Description, parameter.IsAnswer ? "" : value.Value,
                    value.Availability(generation, available), help + "\n" + value.Value + ": " + value.Description);
        }
    }

    private static string Insertion(CabCatalogEntry entry)
    {
        if (entry.Kind != "inhaltsfeld") return entry.Command ?? "";
        if (entry.Id is "JS-5.88" or "JS-5.89") return entry.Syntax[0];
        var syntax = entry.Syntax[0]; var end = syntax.IndexOfAny([':', '{', ']', '['], 1);
        var name = syntax[1..end];
        return "[" + name + (CabContentGrammar.AcceptsBare(entry) ? "" : ":") + "]";
    }
}

public enum CabSourceStyle { LabelCommand, ImmediateCommand, EscapeCommand, Comment, Parameter, ContentField }
public sealed record CabSourceHighlight(CabSourceRange Range, CabSourceStyle Style, string Tooltip);

public static class CabSourceHighlights
{
    public static IReadOnlyList<CabSourceHighlight> Read(CabSourceEditor editor)
    {
        var result = new List<CabSourceHighlight>();
        result.AddRange(CabSourceEditor.Physical(editor.Document.Root).Where(n => n.Kind == CabNodeKind.Comment)
            .Select(n => new CabSourceHighlight(n.Range, CabSourceStyle.Comment, "Kommentar; Originaltext bleibt erhalten.")));
        foreach (var command in editor.Validation.Commands)
        {
            var style = command.Syntax.Kind switch
            {
                CabNodeKind.EscapeCommand => CabSourceStyle.EscapeCommand,
                CabNodeKind.ImmediateCommand => CabSourceStyle.ImmediateCommand,
                CabNodeKind.Comment => CabSourceStyle.Comment,
                _ => CabSourceStyle.LabelCommand,
            };
            var help = CabSourceCatalog.Tooltip(command.Definition.Catalog);
            result.Add(new(command.Syntax.Range, style, help));
            foreach (var parameter in command.Parameters.Where(p => p.IsPresent))
                foreach (var part in parameter.Parts)
                    result.Add(new(part.Range, CabSourceStyle.Parameter, help + "\n" + parameter.Rule.Parameter.Description));
        }
        foreach (var content in CabContentValidation.Bind(editor.Document, editor.Validation.Commands))
            foreach (var field in content.Syntax.Fields)
                foreach (var range in content.Source.Ranges(field.Offset, field.Length))
                    result.Add(new(range, CabSourceStyle.ContentField, CabSourceCatalog.Tooltip(field.Definition)));
        return result.AsReadOnly();
    }
}
