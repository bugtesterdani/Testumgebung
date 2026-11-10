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
        var entries = Index(before.Definition);
        var entries2 = Index(after.Definition);
        if (before.Definition.Name != after.Definition.Name)
            list.Add(new(DiffKind.Changed, "Projekt", "Name", $"{before.Definition.Name} → {after.Definition.Name}"));
        if (before.Description != after.Description) list.Add(new(DiffKind.Changed, "Projekt", "Beschreibung", Short(after.Description)));
        CompareVariables(list, "Hauptablauf", before.Definition.Root, after.Definition.Root);

        foreach (var (id, nb) in entries2)
        {
            if (!entries.TryGetValue(id, out var na))
            {
                list.Add(new(DiffKind.Added, Kind(nb.Node), nb.Path, Describe(nb.Node)));
                continue;
            }
            CompareNode(list, na, nb);
        }
        foreach (var (id, na) in entries.Where(entry => !entries2.ContainsKey(entry.Key)))
            list.Add(new(DiffKind.Removed, Kind(na.Node), na.Path, Describe(na.Node)));

        CompareLibrary(list, before.Definition, after.Definition);
        CompareCurves(list, before.Definition, after.Definition);
        CompareBusDatabases(list, before.Definition, after.Definition);
        if (Json(before.Definition.Layout) != Json(after.Definition.Layout))
            list.Add(new(DiffKind.Changed, "Werkeransicht", "Layout", Json(after.Definition.Layout)));
        CompareList(list, "Simulation", before.Simulation, after.Simulation, resource => resource.Name);
        CompareList(list, "Simulation", before.SimulationOutputs, after.SimulationOutputs, output => output.Name);
        CompareList(list, "Simulation", before.SimulationComm, after.SimulationComm, communication => communication.Name);
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
            for (var index = 0; index < group.Children.Count; index++)
            {
                var child = group.Children[index];
                var path2 = path + " > " + child.Name;
                map[child.Id] = new NodeInfo(child, group.Id, index, path2);
                if (child is GroupDefinition group2) Walk(group2, path2);
            }
            if (group.ExitGroup is { } exit)
            {
                var path3 = path + " > " + exit.Name;
                map[exit.Id] = new NodeInfo(exit, group.Id + "#exit", 0, path3);
                Walk(exit, path3);
            }
        }
    }

    private static string Kind(NodeDefinition node) => node is GroupDefinition ? "Gruppe" : "Schritt";

    private static string Describe(NodeDefinition node) => node switch
    {
        StepDefinition step => step.BlockType ?? "Code",
        GroupDefinition group => $"{group.Children.Count} Element(e)",
        _ => "",
    };

    private static void CompareNode(List<DiffEntry> list, NodeInfo nodeInfo, NodeInfo nodeInfo2)
    {
        var area = Kind(nodeInfo2.Node);
        var item = nodeInfo2.Path;
        if (nodeInfo.Node.Name != nodeInfo2.Node.Name) list.Add(new(DiffKind.Changed, area, item, $"Name: {nodeInfo.Node.Name} → {nodeInfo2.Node.Name}"));
        if (nodeInfo.Node.Description != nodeInfo2.Node.Description) list.Add(new(DiffKind.Changed, area, item, "Beschreibung: " + Short(nodeInfo2.Node.Description)));
        if (nodeInfo.Node.Disabled != nodeInfo2.Node.Disabled) list.Add(new(DiffKind.Changed, area, item, nodeInfo2.Node.Disabled ? "Deaktiviert" : "Reaktiviert"));
        if (nodeInfo.Node.BreakpointRule != nodeInfo2.Node.BreakpointRule || Json(nodeInfo.Node.CodeBreakpointRules) != Json(nodeInfo2.Node.CodeBreakpointRules))
            list.Add(new(DiffKind.Changed, area, item, "Haltebedingungen geändert"));
        if (nodeInfo.Node.Storage != nodeInfo2.Node.Storage) list.Add(new(DiffKind.Changed, area, item, "Anzeige/Speicherung geändert"));
        if (nodeInfo.Parent != nodeInfo2.Parent) list.Add(new(DiffKind.Moved, area, item, $"aus {nodeInfo.Path} verschoben"));
        else if (nodeInfo.Index != nodeInfo2.Index) list.Add(new(DiffKind.Moved, area, item, $"Position {nodeInfo.Index + 1} → {nodeInfo2.Index + 1}"));

        CompareNodeContent(list, nodeInfo, nodeInfo2, area, item);
    }

    private static void CompareNodeContent(List<DiffEntry> list, NodeInfo nodeInfo, NodeInfo nodeInfo2, string area, string item)
    {
        switch (nodeInfo.Node, nodeInfo2.Node)
        {
            case (StepDefinition sa, StepDefinition sb):
                if (sa.BlockType != sb.BlockType) list.Add(new(DiffKind.Changed, area, item, $"Baustein: {sa.BlockType} → {sb.BlockType}"));
                foreach (var key in sa.Parameters.Keys.Union(sb.Parameters.Keys).OrderBy(union => union, StringComparer.Ordinal))
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

    private static string Rep(Repetition repetition) => repetition.Mode switch
    {
        RepeatMode.Count => $"{repetition.Count}×",
        RepeatMode.Duration => $"{repetition.Duration.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s",
        RepeatMode.While => "solange " + repetition.Expression,
        RepeatMode.Until => "bis " + repetition.Expression,
        RepeatMode.Endless => "endlos",
        _ => "einmal",
    };

    private static void CompareVariables(List<DiffEntry> list, string item, GroupDefinition group, GroupDefinition group2)
    {
        var va = group.Variables.ToDictionary(variable => variable.Name, StringComparer.Ordinal);
        var vb = group2.Variables.ToDictionary(variable => variable.Name, StringComparer.Ordinal);
        foreach (var (name, variable) in vb)
        {
            if (!va.TryGetValue(name, out var old)) list.Add(new(DiffKind.Added, "Variable", $"{item} > {name}", $"{variable.Type} = {TypedValue.Format(variable.Initial)}"));
            else if (old.Type != variable.Type || TypedValue.Format(old.Initial) != TypedValue.Format(variable.Initial))
                list.Add(new(DiffKind.Changed, "Variable", $"{item} > {name}", $"{old.Type} = {TypedValue.Format(old.Initial)} → {variable.Type} = {TypedValue.Format(variable.Initial)}"));
        }
        foreach (var name in va.Keys.Where(name2 => !vb.ContainsKey(name2)))
            list.Add(new(DiffKind.Removed, "Variable", $"{item} > {name}", ""));
    }

    private static void CompareLibrary(List<DiffEntry> list, TestDefinition definition, TestDefinition definition2)
    {
        CompareCodeLibrary(list, definition, definition2);
        CompareGroupLibrary(list, definition, definition2);
    }

    private static void CompareCodeLibrary(List<DiffEntry> list, TestDefinition definition, TestDefinition definition2)
    {
        foreach (var block in definition2.Library)
        {
            var old = definition.Library.FirstOrDefault(codeBlock => codeBlock.Id == block.Id);
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
        foreach (var old in definition.Library.Where(codeBlock => definition2.Library.All(codeBlock2 => codeBlock2.Id != codeBlock.Id)))
            list.Add(new(DiffKind.Removed, "CodeBlock", old.Name, $"v{old.Version}"));
    }

    private static void CompareGroupLibrary(List<DiffEntry> list, TestDefinition definition, TestDefinition definition2)
    {
        foreach (var block in definition2.GroupLibrary)
        {
            var old = definition.GroupLibrary.FirstOrDefault(groupBlock => groupBlock.Id == block.Id);
            if (old is null) list.Add(new(DiffKind.Added, "Gruppenbaustein", block.Name, $"v{block.Version}"));
            else if (old.Version != block.Version || Json(old.Ports) != Json(block.Ports))
                list.Add(new(DiffKind.Changed, "Gruppenbaustein", block.Name, $"v{old.Version} → v{block.Version}"));
        }
        foreach (var old in definition.GroupLibrary.Where(groupBlock => definition2.GroupLibrary.All(groupBlock2 => groupBlock2.Id != groupBlock.Id)))
            list.Add(new(DiffKind.Removed, "Gruppenbaustein", old.Name, $"v{old.Version}"));
    }

    private static void CompareBusDatabases(List<DiffEntry> list, TestDefinition definition, TestDefinition definition2)
    {
        static string Summary(BusDatabaseDefinition database)
        {
            try
            {
                return database.Parsed.Summary;
            }
            catch (Testumgebung.Contracts.BusDatabaseException ex)
            {
                return "fehlerhaft: " + ex.Message;
            }
        }
        foreach (var database2 in definition.BusDatabases.Where(database3 => definition2.FindBusDatabase(database3.Name) is null))
            list.Add(new(DiffKind.Removed, "Busdatenbank", database2.Name, database2.FileName ?? ""));
        foreach (var database3 in definition2.BusDatabases)
        {
            if (definition.FindBusDatabase(database3.Name) is not { } old) list.Add(new(DiffKind.Added, "Busdatenbank", database3.Name, Summary(database3)));
            else if (old.Content != database3.Content || old.Format != database3.Format) list.Add(new(DiffKind.Changed, "Busdatenbank", database3.Name, $"{Summary(old)} → {Summary(database3)}"));
        }
    }

    private static void CompareCurves(List<DiffEntry> list, TestDefinition definition, TestDefinition definition2)
    {
        var va = definition.Curves.Versions.Select(curve => (curve.Id, curve.Version, curve.Name)).ToHashSet();
        var vb = definition2.Curves.Versions.Select(curve => (curve.Id, curve.Version, curve.Name)).ToHashSet();
        foreach (var namedEntry in vb.Except(va)) list.Add(new(DiffKind.Added, "Kurve", $"{namedEntry.Name} v{namedEntry.Version}", namedEntry.Id));
        foreach (var namedEntry2 in va.Except(vb)) list.Add(new(DiffKind.Removed, "Kurve", $"{namedEntry2.Name} v{namedEntry2.Version}", namedEntry2.Id));
    }

    private static void CompareList<T>(List<DiffEntry> list, string area, IReadOnlyList<T> items, IReadOnlyList<T> items2, Func<T, string> key)
    {
        foreach (var item in items2)
        {
            var old = items.FirstOrDefault(t3 => key(t3) == key(item));
            if (old is null) list.Add(new(DiffKind.Added, area, key(item), ""));
            else if (Json(old) != Json(item)) list.Add(new(DiffKind.Changed, area, key(item), "Simulationsverhalten geändert"));
        }
        foreach (var old in items.Where(t3 => items2.All(t2 => key(t2) != key(t3))))
            list.Add(new(DiffKind.Removed, area, key(old), ""));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);

    private static int Lines(string line) => line.Length == 0 ? 0 : line.Count(character => character == '\n') + 1;

    private static string Short(string? inputText)
    {
        if (string.IsNullOrEmpty(inputText)) return "(leer)";
        var text = inputText.Replace("\r", "").Replace('\n', '⏎');
        return text.Length > 80 ? text[..80] + "…" : text;
    }
}
