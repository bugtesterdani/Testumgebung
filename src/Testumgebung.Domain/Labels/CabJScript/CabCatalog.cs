using System.Collections.Frozen;
using System.Text.Json;

namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabPrinterGeneration { Ax, X2, X3, X4 }
public enum CabAvailability { Unknown, Supported, Unsupported }

public sealed record CabCatalogValue(string Value, string Description, JsonElement Metadata)
{
    public CabAvailability Availability(CabPrinterGeneration generation, CabAvailability inherited = CabAvailability.Unknown)
        => CabCatalog.GenerationAvailability(Metadata, generation, inherited);
}

public sealed record CabCatalogParameter(string Name, string Spelling, string Source, string Description,
    IReadOnlyList<CabCatalogValue> Values, JsonElement Metadata)
{
    public bool IsAnswer => Source == "antwortfeld";
    public CabAvailability Availability(CabPrinterGeneration generation, CabAvailability inherited = CabAvailability.Unknown)
        => CabCatalog.GenerationAvailability(Metadata, generation, inherited);
}

public sealed record CabCatalogEntry(string Id, string Section, string Kind, string? Command, string Title,
    int FirstPage, int LastPage, string? Reference, IReadOnlyList<string> Syntax,
    IReadOnlyList<CabCatalogParameter> Parameters, JsonElement Metadata)
{
    public CabAvailability Availability(CabPrinterGeneration generation)
        => CabCatalog.GenerationAvailability(Metadata, generation, CabAvailability.Unknown);
}

/// <summary>The reviewed catalog is embedded, so installed applications do not depend on repository paths.</summary>
public sealed class CabCatalog
{
    private static readonly Lazy<CabCatalog> Shared = new(LoadEmbedded);
    public static CabCatalog Default => Shared.Value;
    public IReadOnlyList<CabCatalogEntry> Entries { get; }
    private readonly FrozenDictionary<string, CabCatalogEntry> _byId;
    public CabCatalogEntry this[string id] => _byId[id];
    public bool TryGet(string id, out CabCatalogEntry? entry) => _byId.TryGetValue(id, out entry);

    private CabCatalog(JsonElement root)
    {
        Entries = Array.AsReadOnly(root.GetProperty("eintraege").EnumerateArray().Select(ReadEntry).ToArray());
        _byId = Entries.ToFrozenDictionary(e => e.Id, StringComparer.Ordinal);
    }

    private static CabCatalog LoadEmbedded()
    {
        using var stream = typeof(CabCatalog).Assembly.GetManifestResourceStream("Testumgebung.CabJScript.Catalog.json")
            ?? throw new InvalidOperationException("Eingebetteter cab-JScript-Katalog fehlt.");
        using var document = JsonDocument.Parse(stream);
        return new(document.RootElement);
    }

    private static CabCatalogEntry ReadEntry(JsonElement entry)
    {
        var pages = entry.GetProperty("seiten");
        return new(Text(entry, "id")!, Text(entry, "abschnitt")!, Text(entry, "art")!, Text(entry, "befehl"),
            Text(entry, "titel")!, pages[0].GetInt32(), pages[1].GetInt32(), Text(entry, "verweis"),
            Array.AsReadOnly(entry.GetProperty("syntax").EnumerateArray().Select(s => s.GetString()!).ToArray()),
            Array.AsReadOnly(entry.GetProperty("parameter").EnumerateArray().Select(ReadParameter).ToArray()), entry.Clone());
    }

    private static CabCatalogParameter ReadParameter(JsonElement parameter)
        => new(Text(parameter, "name")!, Text(parameter, "schreibweise")!, Text(parameter, "quelle")!,
            Text(parameter, "beschreibung")!, Array.AsReadOnly(parameter.GetProperty("werte").EnumerateArray()
                .Select(v => new CabCatalogValue(Text(v, "wert")!, Text(v, "beschreibung")!, v.Clone())).ToArray()), parameter.Clone());

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    internal static CabAvailability GenerationAvailability(JsonElement element, CabPrinterGeneration generation, CabAvailability inherited)
    {
        if (!element.TryGetProperty("generationen", out var generations) || generations.ValueKind == JsonValueKind.Null)
            return inherited;
        if (!generations.TryGetProperty(generation.ToString(), out var value)) return CabAvailability.Unknown;
        return value.GetString() switch { "y" => CabAvailability.Supported, "n" => CabAvailability.Unsupported, _ => CabAvailability.Unknown };
    }
}
