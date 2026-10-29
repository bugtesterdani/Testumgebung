/**
 * @file
 * @brief Unterscheidet verschachtelte Reservierungen von gleichzeitig laufenden Geschwisterzweigen.
 */
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>Besitzbuch unter dem Gate des ResourceManager. Ein Kind darf Elternreservierungen übernehmen, Geschwister warten.</summary>
internal sealed class BranchReservations
{
    private readonly Dictionary<string, Dictionary<(string Run, string Branch), int>> _held = new(StringComparer.Ordinal);

    public static string Node(ResourceRequest request) => Node(request.OwnerRunId, request.Branch);
    private static string Node(string run, string branch) => branch.Length == 0 ? run : run + "\0" + branch;
    private static bool Descendant(string branch, string ancestor) => ancestor.Length == 0 || branch == ancestor || branch.StartsWith(ancestor + "/", StringComparison.Ordinal);
    public static bool Within(string node, string ancestor) => node == ancestor || node.StartsWith(ancestor + (ancestor.Contains('\0') ? "/" : "\0"), StringComparison.Ordinal);

    private static bool Compatible((string Run, string Branch) owner, ResourceRequest request)
        => owner.Run == request.OwnerRunId && Descendant(request.Branch, owner.Branch);

    public bool CanAccess(ResourceRequest request, string resource)
        => !_held.TryGetValue(resource, out var owners) || owners.Keys.All(o => Compatible(o, request));

    public IEnumerable<string> Blockers(ResourceRequest request)
        => request.Resources.Where(_held.ContainsKey).SelectMany(r => _held[r].Keys)
            .Where(o => !Compatible(o, request)).Select(o => Node(o.Run, o.Branch)).Distinct();

    public void Add(ResourceRequest request)
    {
        var owner = (request.OwnerRunId, request.Branch);
        foreach (var resource in request.Resources.Distinct())
        {
            if (!_held.TryGetValue(resource, out var owners)) _held[resource] = owners = [];
            owners[owner] = owners.GetValueOrDefault(owner) + 1;
        }
    }

    public void Remove(ResourceRequest request)
    {
        var owner = (request.OwnerRunId, request.Branch);
        foreach (var resource in request.Resources.Distinct())
        {
            var owners = _held[resource];
            if (owners[owner] == 1) owners.Remove(owner); else owners[owner]--;
            if (owners.Count == 0) _held.Remove(resource);
        }
    }
}
