using System.Collections.Frozen;

namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabCommandDefinition(CabCatalogEntry Catalog, IReadOnlyList<CabParameterRule> Parameters)
{
    public CabParameterRule Parameter(string name) => Parameters.Single(p => p.Parameter.Name == name);
}

public sealed class CabDefinitions
{
    private static readonly Lazy<CabDefinitions> Shared = new(() => new(CabCatalog.Default));
    public static CabDefinitions Default => Shared.Value;
    public IReadOnlyList<CabCommandDefinition> Commands { get; }
    private readonly FrozenDictionary<string, CabCommandDefinition> _byId;
    public CabCommandDefinition this[string id] => _byId[id];
    public static CabParameterRule ParameterRule(string id, string name)
    {
        var entry = CabCatalog.Default[id];
        var parameter = entry.Parameters.Single(p => p.Name == name);
        return entry.Kind == "inhaltsfeld" ? CabContentRuleFactory.Create(entry, parameter) : CabRuleFactory.Create(entry, parameter);
    }

    private CabDefinitions(CabCatalog catalog)
    {
        Commands = Array.AsReadOnly(catalog.Entries.Where(e => e.Kind != "inhaltsfeld")
            .Select(e => new CabCommandDefinition(e, Array.AsReadOnly(e.Parameters.Select(p => CabRuleFactory.Create(e, p)).ToArray()))).ToArray());
        _byId = Commands.ToFrozenDictionary(c => c.Catalog.Id, StringComparer.Ordinal);
    }
}
