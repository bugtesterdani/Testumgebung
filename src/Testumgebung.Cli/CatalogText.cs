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
        foreach (var group in BlockCatalog.Default.All.GroupBy(b => b.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine($"## {group.Key}");
            foreach (var b in group.OrderBy(b => b.Type, StringComparer.Ordinal))
            {
                var required = b.Parameters.Where(p => p.Required).Select(p => p.Name).ToList();
                text.AppendLine($"{b.Type} – {b.DisplayName}{(required.Count > 0 ? " (Pflicht: " + string.Join(", ", required) + ")" : "")}");
            }
        }
        return text.ToString();
    }

    /// <summary>Alle Bausteine, deren Typ, Name, Kategorie oder Beschreibung den Suchtext enthält – mit allen Parametern.</summary>
    public static string Search(string query)
    {
        List<BlockDescriptor> exact = BlockCatalog.Default.TryGet(query, out var single) ? [single] : BlockCatalog.Default.All
            .Where(b => new[] { b.Type, b.DisplayName, b.Category, b.Description }.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b.Type, StringComparer.Ordinal).ToList();
        return exact.Count == 0
            ? $"Kein Baustein passt zu „{query}“. Übersicht: testumgebung-cli katalog"
            : string.Join(Environment.NewLine, exact.Select(b => Describe(b.Type)));
    }

    /// <summary>Ein Baustein mit allen statischen Parametern (Art, Standard, Pflicht, Auswahl, Hinweis).</summary>
    public static string Describe(string type)
    {
        if (!BlockCatalog.Default.TryGet(type, out var b)) return $"{type} – unbekannter Bausteintyp. Übersicht: testumgebung-cli katalog";
        var text = new StringBuilder();
        text.AppendLine($"{b.Type} – {b.DisplayName} [{b.Category}]");
        if (b.Description.Length > 0) text.AppendLine($"  {b.Description}");
        foreach (var p in b.Parameters)
        {
            var details = new List<string> { p.Kind.ToString() };
            if (p.Required) details.Add("Pflicht");
            if (p.Default.Length > 0) details.Add($"Standard „{p.Default}“");
            if (p.Choices is { Count: > 0 }) details.Add("Auswahl: " + string.Join(" | ", p.Choices));
            text.AppendLine($"  - {p.Name} ({string.Join(", ", details)}){(p.Hint is { Length: > 0 } ? " – " + p.Hint : "")}");
        }
        return text.ToString().TrimEnd();
    }
}
