/**
 * @file
 * @brief Erstellt fachliche Änderungslisten zwischen Projektständen für den Versionsvergleich.
 */

using System.Globalization;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Persistence;

public enum DiffKind
{
    Added,
    Removed,
    Changed,
    Moved,
}

/// <summary>Eine Änderung zwischen zwei Projektständen.</summary>
public sealed record DiffEntry(DiffKind Kind, string Area, string Item, string Detail)
{
    public string KindText => Kind switch
    {
        DiffKind.Added => "neu",
        DiffKind.Removed => "entfernt",
        DiffKind.Moved => "verschoben",
        _ => "geändert",
    };
}

/// <summary>
/// Versionsvergleich (z. B. Release v1.4 ↔ v1.5 oder Release ↔ Editorstand): Ablaufknoten über ihre stabilen IDs
/// (Name, Parameter, Gruppeneinstellungen, Variablen, Position), Bibliotheksbausteine, Kurvenversionen, Werkeransicht
/// und Simulationsprofil.
/// </summary>
public static class ProjectDiff
{
    private sealed record NodeInfo(NodeDefinition Node, string Parent, int Index, string Path);

    public static IReadOnlyList<DiffEntry> Compare(TestProject before, TestProject after)
    {
        var list = new List<DiffEntry>();
        var a = Index(before.Definition);
        var b = Index(after.Definition);
        if (before.Definition.Name != after.Definition.Name)
            list.Add(new(DiffKind.Changed, "Projekt", "Name", $"{before.Definition.Name} → {after.Definition.Name}"));
        if (before.Description != after.Description) list.Add(new(DiffKind.Changed, "Projekt", "Beschreibung", Short(after.Description)));
        CompareVariables(list, "Hauptablauf", before.Definition.Root, after.Definition.Root);

        foreach (var (id, nb) in b)
        {
            if (!a.TryGetValue(id, out var na))
            {
                list.Add(new(DiffKind.Added, Kind(nb.Node), nb.Path, Describe(nb.Node)));
                continue;
            }
            CompareNode(list, na, nb);
        }
        foreach (var (id, na) in a.Where(x => !b.ContainsKey(x.Key)))
            list.Add(new(DiffKind.Removed, Kind(na.Node), na.Path, Describe(na.Node)));

        CompareLibrary(list, before.Definition, after.Definition);
        CompareCurves(list, before.Definition, after.Definition);
        CompareBusDatabases(list, before.Definition, after.Definition);
        if (Json(before.Definition.Layout) != Json(after.Definition.Layout))
            list.Add(new(DiffKind.Changed, "Werkeransicht", "Layout", Json(after.Definition.Layout)));
        CompareList(list, "Simulation", before.Simulation, after.Simulation, r => r.Name);
        CompareList(list, "Simulation", before.SimulationOutputs, after.SimulationOutputs, o => o.Name);
        CompareList(list, "Simulation", before.SimulationComm, after.SimulationComm, c => c.Name);
        return list;
    }

    private static Dictionary<string, NodeInfo> Index(TestDefinition definition)
    {
        var map = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
        foreach (var root in definition.AllRoots)
            Walk(root, root.Name);
        return map;

        void Walk(GroupDefinition group, string path)
        {
            for (var i = 0; i < group.Children.Count; i++)
            {
                var child = group.Children[i];
                var p = path + " > " + child.Name;
                map[child.Id] = new NodeInfo(child, group.Id, i, p);
                if (child is GroupDefinition g) Walk(g, p);
            }
            if (group.ExitGroup is { } exit)
            {
                var p = path + " > " + exit.Name;
                map[exit.Id] = new NodeInfo(exit, group.Id + "#exit", 0, p);
                Walk(exit, p);
            }
        }
    }

    private static string Kind(NodeDefinition n) => n is GroupDefinition ? "Gruppe" : "Schritt";

    private static string Describe(NodeDefinition n) => n switch
    {
        StepDefinition s => s.BlockType ?? "Code",
        GroupDefinition g => $"{g.Children.Count} Element(e)",
        _ => "",
    };

    private static void CompareNode(List<DiffEntry> list, NodeInfo a, NodeInfo b)
    {
        var area = Kind(b.Node);
        var item = b.Path;
        if (a.Node.Name != b.Node.Name) list.Add(new(DiffKind.Changed, area, item, $"Name: {a.Node.Name} → {b.Node.Name}"));
        if (a.Node.Description != b.Node.Description) list.Add(new(DiffKind.Changed, area, item, "Beschreibung: " + Short(b.Node.Description)));
        if (a.Node.Storage != b.Node.Storage) list.Add(new(DiffKind.Changed, area, item, "Anzeige/Speicherung geändert"));
        if (a.Parent != b.Parent) list.Add(new(DiffKind.Moved, area, item, $"aus {a.Path} verschoben"));
        else if (a.Index != b.Index) list.Add(new(DiffKind.Moved, area, item, $"Position {a.Index + 1} → {b.Index + 1}"));

        CompareNodeContent(list, a, b, area, item);
    }

    private static void CompareNodeContent(List<DiffEntry> list, NodeInfo a, NodeInfo b, string area, string item)
    {
        switch (a.Node, b.Node)
        {
            case (StepDefinition sa, StepDefinition sb):
                if (sa.BlockType != sb.BlockType) list.Add(new(DiffKind.Changed, area, item, $"Baustein: {sa.BlockType} → {sb.BlockType}"));
                foreach (var key in sa.Parameters.Keys.Union(sb.Parameters.Keys).OrderBy(k => k, StringComparer.Ordinal))
                {
                    var va = sa.Parameters.GetValueOrDefault(key) ?? "";
                    var vb = sb.Parameters.GetValueOrDefault(key) ?? "";
                    if (va != vb) list.Add(new(DiffKind.Changed, area, item, key == "Code" ? "Code geändert" : $"{key}: {Short(va)} → {Short(vb)}"));
                }
                break;
            case (GroupDefinition ga, GroupDefinition gb):
                CompareGroupContent(list, ga, gb, area, item);
                break;
            default:
                list.Add(new(DiffKind.Changed, area, item, "Art geändert (Schritt ↔ Gruppe)"));
                break;
        }
    }

    private static void CompareGroupContent(List<DiffEntry> list, GroupDefinition ga, GroupDefinition gb, string area, string item)
    {
        if (ga.EntryCondition != gb.EntryCondition) list.Add(new(DiffKind.Changed, area, item, $"Bedingung: {ga.EntryCondition ?? "immer"} → {gb.EntryCondition ?? "immer"}"));
        if (ga.Repetition != gb.Repetition) list.Add(new(DiffKind.Changed, area, item, $"Wiederholung: {Rep(ga.Repetition)} → {Rep(gb.Repetition)}"));
        if (ga.Parallel != gb.Parallel) list.Add(new(DiffKind.Changed, area, item, $"Parallele Ausführung: {ga.Parallel} → {gb.Parallel}"));
        if (ga.ResultBeforeRepeat != gb.ResultBeforeRepeat) list.Add(new(DiffKind.Changed, area, item, $"Ergebnis bei Wiederholung: {ga.ResultBeforeRepeat} → {gb.ResultBeforeRepeat}"));
        if (!ga.Resources.SequenceEqual(gb.Resources)) list.Add(new(DiffKind.Changed, area, item, $"Reservierung: {string.Join(", ", ga.Resources)} → {string.Join(", ", gb.Resources)}"));
        if (ga.ResourceTimeout != gb.ResourceTimeout) list.Add(new(DiffKind.Changed, area, item, $"Wartezeit: {ga.ResourceTimeout} → {gb.ResourceTimeout}"));
        if (ga.IsDutRun != gb.IsDutRun) list.Add(new(DiffKind.Changed, area, item, gb.IsDutRun ? "jetzt Prüflingslauf" : "kein Prüflingslauf mehr"));
        if ((ga.ExitGroup is null) != (gb.ExitGroup is null)) list.Add(new(DiffKind.Changed, area, item, gb.ExitGroup is null ? "Exit-Gruppe entfernt" : "Exit-Gruppe hinzugefügt"));
        CompareVariables(list, item, ga, gb);
    }

    private static string Rep(Repetition r) => r.Mode switch
    {
        RepeatMode.Count => $"{r.Count}×",
        RepeatMode.Duration => $"{r.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s",
        RepeatMode.While => "solange " + r.Expression,
        RepeatMode.Until => "bis " + r.Expression,
        RepeatMode.Endless => "endlos",
        _ => "einmal",
    };

    private static void CompareVariables(List<DiffEntry> list, string item, GroupDefinition a, GroupDefinition b)
    {
        var va = a.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
        var vb = b.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
        foreach (var (name, v) in vb)
        {
            if (!va.TryGetValue(name, out var old)) list.Add(new(DiffKind.Added, "Variable", $"{item} > {name}", $"{v.Type} = {TypedValue.Format(v.Initial)}"));
            else if (old.Type != v.Type || TypedValue.Format(old.Initial) != TypedValue.Format(v.Initial))
                list.Add(new(DiffKind.Changed, "Variable", $"{item} > {name}", $"{old.Type} = {TypedValue.Format(old.Initial)} → {v.Type} = {TypedValue.Format(v.Initial)}"));
        }
        foreach (var name in va.Keys.Where(n => !vb.ContainsKey(n)))
            list.Add(new(DiffKind.Removed, "Variable", $"{item} > {name}", ""));
    }

    private static void CompareLibrary(List<DiffEntry> list, TestDefinition a, TestDefinition b)
    {
        CompareCodeLibrary(list, a, b);
        CompareGroupLibrary(list, a, b);
    }

    private static void CompareCodeLibrary(List<DiffEntry> list, TestDefinition a, TestDefinition b)
    {
        foreach (var block in b.Library)
        {
            var old = a.Library.FirstOrDefault(x => x.Id == block.Id);
            if (old is null) list.Add(new(DiffKind.Added, "CodeBlock", block.Name, $"v{block.Version} ({block.Language})"));
            else
            {
                var changes = new List<string>();
                if (old.Version != block.Version) changes.Add($"v{old.Version} → v{block.Version}");
                if (old.Source != block.Source) changes.Add($"Code geändert ({Lines(old.Source)} → {Lines(block.Source)} Zeilen)");
                if (Json(old.Ports) != Json(block.Ports)) changes.Add("Schnittstelle geändert");
                if (changes.Count > 0) list.Add(new(DiffKind.Changed, "CodeBlock", block.Name, string.Join(", ", changes)));
            }
        }
        foreach (var old in a.Library.Where(x => b.Library.All(y => y.Id != x.Id)))
            list.Add(new(DiffKind.Removed, "CodeBlock", old.Name, $"v{old.Version}"));
    }

    private static void CompareGroupLibrary(List<DiffEntry> list, TestDefinition a, TestDefinition b)
    {
        foreach (var block in b.GroupLibrary)
        {
            var old = a.GroupLibrary.FirstOrDefault(x => x.Id == block.Id);
            if (old is null) list.Add(new(DiffKind.Added, "Gruppenbaustein", block.Name, $"v{block.Version}"));
            else if (old.Version != block.Version || Json(old.Ports) != Json(block.Ports))
                list.Add(new(DiffKind.Changed, "Gruppenbaustein", block.Name, $"v{old.Version} → v{block.Version}"));
        }
        foreach (var old in a.GroupLibrary.Where(x => b.GroupLibrary.All(y => y.Id != x.Id)))
            list.Add(new(DiffKind.Removed, "Gruppenbaustein", old.Name, $"v{old.Version}"));
    }

    private static void CompareBusDatabases(List<DiffEntry> list, TestDefinition a, TestDefinition b)
    {
        static string Summary(BusDatabaseDefinition d)
        {
            try
            {
                return d.Parsed.Summary;
            }
            catch (Testumgebung.Contracts.BusDatabaseException ex)
            {
                return "fehlerhaft: " + ex.Message;
            }
        }
        foreach (var d in a.BusDatabases.Where(d => b.FindBusDatabase(d.Name) is null))
            list.Add(new(DiffKind.Removed, "Busdatenbank", d.Name, d.FileName ?? ""));
        foreach (var d in b.BusDatabases)
        {
            if (a.FindBusDatabase(d.Name) is not { } old) list.Add(new(DiffKind.Added, "Busdatenbank", d.Name, Summary(d)));
            else if (old.Content != d.Content || old.Format != d.Format) list.Add(new(DiffKind.Changed, "Busdatenbank", d.Name, $"{Summary(old)} → {Summary(d)}"));
        }
    }

    private static void CompareCurves(List<DiffEntry> list, TestDefinition a, TestDefinition b)
    {
        var va = a.Curves.Versions.Select(v => (v.Id, v.Version, v.Name)).ToHashSet();
        var vb = b.Curves.Versions.Select(v => (v.Id, v.Version, v.Name)).ToHashSet();
        foreach (var v in vb.Except(va)) list.Add(new(DiffKind.Added, "Kurve", $"{v.Name} v{v.Version}", v.Id));
        foreach (var v in va.Except(vb)) list.Add(new(DiffKind.Removed, "Kurve", $"{v.Name} v{v.Version}", v.Id));
    }

    private static void CompareList<T>(List<DiffEntry> list, string area, IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, string> key)
    {
        foreach (var item in b)
        {
            var old = a.FirstOrDefault(x => key(x) == key(item));
            if (old is null) list.Add(new(DiffKind.Added, area, key(item), ""));
            else if (Json(old) != Json(item)) list.Add(new(DiffKind.Changed, area, key(item), "Simulationsverhalten geändert"));
        }
        foreach (var old in a.Where(x => b.All(y => key(y) != key(x))))
            list.Add(new(DiffKind.Removed, area, key(old), ""));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static string Json(object? o) => JsonSerializer.Serialize(o, JsonOptions);

    private static int Lines(string s) => s.Length == 0 ? 0 : s.Count(c => c == '\n') + 1;

    private static string Short(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(leer)";
        var t = s.Replace("\r", "").Replace('\n', '⏎');
        return t.Length > 80 ? t[..80] + "…" : t;
    }
}
