namespace Testumgebung.Presentation.ViewModels;

public sealed partial class EditorViewModel
{
    private string _paletteSearch = "";

    /// <summary>Die Suche betrifft nur die normale Liste; visuelle Einfügemenüs bleiben vollständig.</summary>
    public string PaletteSearch
    {
        get => _paletteSearch;
        set { if (SetProperty(ref _paletteSearch, value ?? "")) OnPropertyChanged(nameof(Palette)); }
    }

    /// <summary>Gruppenvorlagen sind nur in den visuellen Ansichten zusätzliche Einfügevarianten.</summary>
    public IReadOnlyList<PaletteCategory> Palette
    {
        get
        {
            var visual = VisualBlocks || GraphView;
            var search = visual ? "" : PaletteSearch.Trim();
            return _palette.Where(category => visual || category.Name != "Steuerung")
                .Select(category => category with
                {
                    Items = category.Items.Where(item => MatchesPaletteItem(category.Name, item, search)).ToList(),
                    IsExpanded = search.Length > 0 || category.Name is "Struktur" or "Variablen" or "Ablauf" or "Werkerinteraktion"
                })
                .Where(category => category.Items.Count > 0)
                .OrderBy(category => PaletteRank(category.Name)).ToList();
        }
    }

    private static bool MatchesPaletteItem(string category, PaletteItem item, string search)
    {
        if (search.Length == 0) return true;
        // Alle Suchwörter müssen vorkommen; Kategorie und Beschreibung helfen bei unbekannten Fachbegriffen.
        var text = $"{category} {item.DisplayName} {item.Description} {item.BlockType}";
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    private static int PaletteRank(string name) => name switch
    {
        "Steuerung" => 0, "Struktur" => 1, "Variablen" => 2, "Ablauf" => 3,
        "Werkerinteraktion" => 4, "Bewertung" => 5, _ => 6
    };
}
