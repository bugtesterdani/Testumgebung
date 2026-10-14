/**
 * @file
 * @brief Prüft verschachtelte Sperrerweiterungen auf Zyklusrisiken über alle beteiligten Prüfplätze.
 */

using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine;

/// <summary>
/// Verschachtelte Erweiterung: Wer <see cref="Held"/> hält, fordert in einer inneren Gruppe zusätzlich <see cref="Acquired"/> an.
/// Schlüssel = Sperrschlüssel der Prüfplatzkonfiguration (Gerät/Kanal), Namen = abstrakte Namen im Ablauf.
/// </summary>
public sealed record LockEdge(string Held, string Acquired, string HeldName, string AcquiredName, string Path, string Station)
{
    public override string ToString() => $"{Station}: {HeldName} → {AcquiredName} ({Path})";
}

/// <summary>Zyklus in der Sperrreihenfolge: zwei (oder mehr) Läufe können gegenseitig aufeinander warten.</summary>
public sealed record LockCycle(IReadOnlyList<LockEdge> Edges)
{
    public string Text => "Zusätzliche Sperre: Zyklusrisiko – " + string.Join("; ", Edges);

    public string Suggestion =>
        "Ressourcen gemeinsam an der äußeren Gruppe reservieren (atomarer Erwerb) oder überall in derselben Reihenfolge sperren.";
}

/// <summary>
/// Statische Prüfung der Sperrreihenfolge (Abschnitt 7): Atomarer Erwerb verhindert Teilbesitz, aber nicht jede Verklemmung
/// verschachtelter Gruppen. Hält ein Lauf A und fordert B an, während ein anderer Lauf B hält und A anfordert, warten beide
/// ewig. Darum werden alle Erweiterungen (gehalten → zusätzlich) als Kanten gesammelt – über alle Prüfplätze, mit deren
/// Sperrschlüsseln – und ein Zyklus verweigert den Start.
/// </summary>
public static class LockOrderAnalysis
{
    /// <summary>Alle Erweiterungskanten eines Ablaufs (inklusive Exit-Gruppen und grafischer Bausteine).</summary>
    public static IReadOnlyList<LockEdge> Edges(TestDefinition definition, Func<string, string>? keyOf = null, string station = "Ablauf")
    {
        keyOf ??= n => n;
        var edges = new List<LockEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Visit(definition.Root, new Dictionary<string, string>(StringComparer.Ordinal), definition.Root.Name, []);
        return edges;

        Dictionary<string, string> AddResourceEdges(GroupDefinition group, Dictionary<string, string> held, string path)
        {
            var added = new Dictionary<string, string>(held, StringComparer.Ordinal);
            foreach (var name in group.Resources)
            {
                var key = keyOf(name);
                if (held.ContainsKey(key) || added.ContainsKey(key)) continue;
                added[key] = name;
                foreach (var (hk, hn) in held)
                {
                    var edge = new LockEdge(hk, key, hn, name, path, station);
                    if (seen.Add($"{hk}\u0001{key}\u0001{path}")) edges.Add(edge);
                }
            }
            return added;
        }

        void Visit(GroupDefinition group, Dictionary<string, string> held, string path, HashSet<string> blockStack)
        {
            var added = AddResourceEdges(group, held, path);
            foreach (var child in group.Children)
            {
                switch (child)
                {
                    case GroupDefinition g:
                        Visit(g, added, path + " › " + g.Name, blockStack);
                        break;
                    case StepDefinition s when s.BlockType == Blocks.BlockCatalog.GroupBlock
                                               && s.Parameters.GetValueOrDefault("Baustein") is { } id
                                               && definition.GroupLibrary.FirstOrDefault(b => b.Id == id) is { } block
                                               && blockStack.Add(id):
                        Visit(block.Body, added, path + " › " + block.Name, blockStack);
                        blockStack.Remove(id);
                        break;
                }
            }
            if (group.ExitGroup is { } exit) Visit(exit, added, path + " › " + exit.Name, blockStack);
        }
    }

    /// <summary>Erster Zyklus der Kanten (Reihenfolge im Zyklus) oder null.</summary>
    public static LockCycle? FindCycle(IEnumerable<LockEdge> edges)
    {
        var graph = edges.GroupBy(e => e.Held, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = auf dem Pfad, 2 = fertig
        var stack = new List<LockEdge>();

        foreach (var start in graph.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (!state.ContainsKey(start) && Dfs(start) is { } cycle) return cycle;
        return null;

        LockCycle? Dfs(string node)
        {
            state[node] = 1;
            if (graph.TryGetValue(node, out var outgoing))
            {
                foreach (var e in outgoing)
                {
                    stack.Add(e);
                    if (state.TryGetValue(e.Acquired, out var st) && st == 1)
                    {
                        var from = stack.FindIndex(x => x.Held == e.Acquired);
                        return new LockCycle(stack.Skip(from).ToList());
                    }
                    if (!state.ContainsKey(e.Acquired) && Dfs(e.Acquired) is { } found) return found;
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            state[node] = 2;
            return null;
        }
    }
}
