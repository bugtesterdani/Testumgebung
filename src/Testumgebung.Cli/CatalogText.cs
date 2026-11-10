/**
 * @file
 * @brief Bausteinkatalog als kompakter Text (Befehl „katalog“ und Hinweise bei Konfigurationsfehlern).
 */

using System.Text;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Cli;

public static class CatalogText
{
    /// <summary>Eine Zeile je Baustein: technischer Typ, Anzeigename, Pflichtparameter.</summary>
    public static string Overview()
    {
        var text = new StringBuilder();
        foreach (var group in BlockCatalog.Default.All.GroupBy(block => block.Category).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"## {group.Key}");
            foreach (var block in group.OrderBy(block2 => block2.Type, StringComparer.Ordinal))
            {
                var required = block.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name).ToList();
                text.AppendLine($"{block.Type} – {block.DisplayName}{(required.Count > 0 ? " (Pflicht: " + string.Join(", ", required) + ")" : "")}");
            }
        }
        return text.ToString();
    }

    /// <summary>Alle Bausteine, deren Typ, Name, Kategorie oder Beschreibung den Suchtext enthält – mit allen Parametern.</summary>
    public static string Search(string query)
    {
        List<BlockDescriptor> exact = BlockCatalog.Default.TryGet(query, out var single) ? [single] : BlockCatalog.Default.All
            .Where(block => new[] { block.Type, block.DisplayName, block.Category, block.Description }.Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(block => block.Type, StringComparer.Ordinal).ToList();
        return exact.Count == 0
            ? $"Kein Baustein passt zu „{query}“. Übersicht: testumgebung-cli katalog"
            : string.Join(Environment.NewLine, exact.Select(block => Describe(block.Type)));
    }

    /// <summary>Ein Baustein mit allen statischen Parametern (Art, Standard, Pflicht, Auswahl, Hinweis).</summary>
    public static string Describe(string type)
    {
        if (!BlockCatalog.Default.TryGet(type, out var block)) return $"{type} – unbekannter Bausteintyp. Übersicht: testumgebung-cli katalog";
        var text = new StringBuilder();
        text.AppendLine($"{block.Type} – {block.DisplayName} [{block.Category}]");
        if (block.Description.Length > 0) text.AppendLine($"  {block.Description}");
        foreach (var parameter in block.Parameters)
        {
            var details = new List<string> { parameter.Kind.ToString() };
            if (parameter.Required) details.Add("Pflicht");
            if (parameter.Default.Length > 0) details.Add($"Standard „{parameter.Default}“");
            if (parameter.Choices is { Count: > 0 }) details.Add("Auswahl: " + string.Join(" | ", parameter.Choices));
            text.AppendLine($"  - {parameter.Name} ({string.Join(", ", details)}){(parameter.Hint is { Length: > 0 } ? " – " + parameter.Hint : "")}");
        }
        return text.ToString().TrimEnd();
    }
}
