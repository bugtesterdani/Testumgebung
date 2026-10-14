/**
 * @file
 * @brief Validiert und übernimmt Änderungen am laufenden Ablauf, ohne gerade aktive Scopes umzuschreiben.
 */

using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Engine;

/// <summary>
/// Einzelne Änderung eines Run-Patches.
/// <list type="bullet">
/// <item><c>param:&lt;Name&gt;</c> – Bausteinparameter eines Schritts (auch Quelltext eines CodeBlocks)</item>
/// <item><c>name</c> – Bezeichnung eines Schritts oder einer Gruppe</item>
/// <item><c>condition</c> – Eintrittsbedingung einer Gruppe (leer = immer)</item>
/// <item><c>code</c> – Quelltext eines Bibliotheks-CodeBlocks (gilt für folgende Aufrufe im Lauf)</item>
/// <item><c>delete</c> – noch nicht ausgeführten Schritt/Gruppe in einer nicht aktiven Gruppe entfernen</item>
/// <item><c>move</c> – Reihenfolge innerhalb der (nicht aktiven) Elterngruppe verschieben (Wert = Versatz, z. B. -1)</item>
/// </list>
/// Zweiter Teil (ADR 0015): Bibliotheksbausteine und Ablaufstruktur nicht aktiver Gruppen.
/// </summary>
public sealed record PatchChange(string NodeId, string Property, string? Value);

public sealed record PatchResult(bool Applied, int PatchNumber, IReadOnlyList<string> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}

/// <summary>
/// Run-Patch (Abschnitt 6.3): Änderungen an noch nicht ausgeführten Elementen im pausierten Lauf.
/// Gilt nur für diesen Lauf (eigene Definitionskopie), wird geprüft, nummeriert und protokolliert.
/// Aktive Scopes werden nicht umstrukturiert; bereits ausgeführte Schritte werden nicht wiederholt.
/// </summary>
internal sealed class RunPatcher(TestRun run, BlockCatalog catalog)
{
    private readonly object _gate = new();

    public int PatchCount { get; private set; }

    public PatchResult Validate(IReadOnlyList<PatchChange> changes) => Process(changes, apply: false, user: "");

    public PatchResult Apply(IReadOnlyList<PatchChange> changes, string user) => Process(changes, apply: true, user);

    /// <summary>Elterngruppe eines Knotens im Lauf (null = Wurzel/Baustein-Körper).</summary>
    private static GroupDefinition? ParentOf(TestRun run, string id)
    {
        var chain = run.Definition.AncestorsOf(id);
        return chain.Count > 0 ? chain[^1] : null;
    }

    private static void ValidateStructural(TestRun run, NodeDefinition node, PatchChange change, HashSet<string> active, List<string> diagnostics)
    {
        var parent = ParentOf(run, node.Id);
        if (parent is null)
        {
            diagnostics.Add($"„{node.Name}“: ohne Elterngruppe nicht umstrukturierbar.");
            return;
        }
        if (active.Contains(parent.Id))
        {
            diagnostics.Add($"„{node.Name}“: die Elterngruppe „{parent.Name}“ ist gerade aktiv – Struktur wird nicht geändert.");
            return;
        }
        if (node is GroupDefinition g && active.Contains(g.Id))
        {
            diagnostics.Add($"„{node.Name}“ ist gerade aktiv.");
            return;
        }
        if (change.Property == "move")
        {
            if (!int.TryParse(change.Value, out var offset) || offset == 0)
                diagnostics.Add($"„{node.Name}“: Versatz als ganze Zahl ungleich 0 angeben (z. B. -1).");
            else
            {
                var index = parent.Children.FindIndex(c => c.Id == node.Id);
                var target = index + offset;
                if (index < 0 || target < 0 || target >= parent.Children.Count)
                    diagnostics.Add($"„{node.Name}“: Verschieben um {offset} liegt außerhalb der Gruppe.");
            }
        }
    }

    private static string NameOf(TestRun run, string id)
        => run.Definition.Find(id)?.Name
           ?? run.Definition.Library.FirstOrDefault(b => b.Id == id)?.Name
           ?? run.Definition.GroupLibrary.FirstOrDefault(b => b.Id == id)?.Name
           ?? id;

    private PatchResult Process(IReadOnlyList<PatchChange> changes, bool apply, string user)
    {
        lock (_gate)
        {
            var diagnostics = new List<string>();
            if (changes.Count == 0) diagnostics.Add("Keine Änderungen.");
            if (run.Options.Debugger is not { IsPaused: true }) diagnostics.Add("Run-Patches sind nur im pausierten Lauf möglich.");
            var active = run.ExecutionStack.Select(f => f.NodeId).ToHashSet(StringComparer.Ordinal);

            // Prüfen an Kopien – nichts wird geändert, solange eine Diagnose existiert.
            var stepCopies = new Dictionary<string, StepDefinition>(StringComparer.Ordinal);
            void ApplyChange(PatchChange change)
            {
                if (change.Property is "code" or "name" && run.Definition.Library.FirstOrDefault(b => b.Id == change.NodeId) is { } libBlock)
                {
                    if (change.Property == "code") libBlock.Source = change.Value ?? "";
                    else libBlock.Name = change.Value!.Trim();
                    return;
                }
                if (change.Property == "name" && run.Definition.GroupLibrary.FirstOrDefault(b => b.Id == change.NodeId) is { } grpBlock)
                {
                    grpBlock.Name = change.Value!.Trim();
                    return;
                }
                ApplyNodeChange(change);
            }

            void ApplyNodeChange(PatchChange change)
            {
                var node = run.Definition.Find(change.NodeId)!;
                switch (node, change.Property)
                {
                    case (_, "delete"):
                        ParentOf(run, node.Id)!.Children.RemoveAll(c => c.Id == node.Id);
                        break;
                    case (_, "move"):
                        var parent = ParentOf(run, node.Id)!;
                        var index = parent.Children.FindIndex(c => c.Id == node.Id);
                        var item = parent.Children[index];
                        parent.Children.RemoveAt(index);
                        parent.Children.Insert(Math.Clamp(index + int.Parse(change.Value!), 0, parent.Children.Count), item);
                        break;
                    case (_, "name"):
                        node.Name = change.Value!.Trim();
                        break;
                    case (GroupDefinition g, "condition"):
                        g.EntryCondition = string.IsNullOrWhiteSpace(change.Value) ? null : change.Value;
                        break;
                    case (StepDefinition s, var p):
                        s.Parameters[p["param:".Length..]] = change.Value ?? "";
                        break;
                }
            }

            void ValidateLibraryText(PatchChange change, string name)
            {
                if (change.Property == "name" && string.IsNullOrWhiteSpace(change.Value)) diagnostics.Add($"„{name}“: Bezeichnung darf nicht leer sein.");
                if (change.Property == "code" && string.IsNullOrWhiteSpace(change.Value)) diagnostics.Add($"„{name}“: Quelltext darf nicht leer sein.");
            }

            void ValidateChange(PatchChange change)
            {
                // Bibliotheksbaustein (Code oder grafisch) – gilt für künftige Aufrufe, auch wenn er schon lief
                if (change.Property is "code" or "name" && run.Definition.Library.FirstOrDefault(b => b.Id == change.NodeId) is { } libBlock)
                {
                    ValidateLibraryText(change, libBlock.Name);
                    return;
                }
                if (change.Property == "name" && run.Definition.GroupLibrary.FirstOrDefault(b => b.Id == change.NodeId) is { } grpBlock)
                {
                    if (string.IsNullOrWhiteSpace(change.Value)) diagnostics.Add($"„{grpBlock.Name}“: Bezeichnung darf nicht leer sein.");
                    return;
                }

                var node = run.Definition.Find(change.NodeId);
                if (node is null)
                {
                    diagnostics.Add($"Element '{change.NodeId}' existiert nicht im Lauf.");
                    return;
                }
                if (change.Property is "delete" or "move")
                {
                    ValidateStructural(run, node, change, active, diagnostics);
                    return;
                }
                if (active.Contains(change.NodeId))
                {
                    diagnostics.Add($"„{node.Name}“ ist gerade aktiv – aktive Scopes werden nicht geändert.");
                    return;
                }
                ValidateNodeChange(node, change);
            }

            void ValidateNodeChange(NodeDefinition node, PatchChange change)
            {
                switch (node, change.Property)
                {
                    case (_, "name") when string.IsNullOrWhiteSpace(change.Value):
                        diagnostics.Add($"„{node.Name}“: Bezeichnung darf nicht leer sein.");
                        break;
                    case (_, "name"):
                        break;
                    case (GroupDefinition g, "condition"):
                        if (!string.IsNullOrWhiteSpace(change.Value))
                        {
                            try { Expression.Parse(change.Value); }
                            catch (ExpressionException ex) { diagnostics.Add($"„{g.Name}“, Bedingung: {ex.Message}"); }
                        }
                        break;
                    case (StepDefinition s, var p) when p.StartsWith("param:", StringComparison.Ordinal):
                        if (s.BlockType is null)
                        {
                            diagnostics.Add($"„{s.Name}“ ist im Code definiert und nicht patchbar.");
                            break;
                        }
                        if (!stepCopies.TryGetValue(s.Id, out var copy))
                        {
                            copy = new StepDefinition(s.Id, s.Name, s.Action) { BlockType = s.BlockType };
                            foreach (var (k, v) in s.Parameters) copy.Parameters[k] = v;
                            stepCopies[s.Id] = copy;
                        }
                        copy.Parameters[p["param:".Length..]] = change.Value ?? "";
                        break;
                    default:
                        diagnostics.Add($"„{node.Name}“: Änderung '{change.Property}' wird nicht unterstützt.");
                        break;
                }
            }

            foreach (var change in changes)
            {
                ValidateChange(change);
            }
            foreach (var copy in stepCopies.Values)
                foreach (var d in catalog.Rebuild(copy)) diagnostics.Add($"„{copy.Name}“: {d}");

            if (diagnostics.Count > 0 || !apply) return new PatchResult(false, PatchCount, diagnostics);

            foreach (var change in changes)
            {
                ApplyChange(change);
            }
            foreach (var id in stepCopies.Keys) catalog.Rebuild((StepDefinition)run.Definition.Find(id)!);

            PatchCount++;
            run.MarkDevelopmentRun();
            var details = string.Join("; ", changes.Select(c => $"{NameOf(run, c.NodeId)}.{c.Property}"));
            run.Emit(new DevelopmentEvent(user, $"Run-Patch {PatchCount} angewendet", details) { Time = run.Clock.Now, RunId = run.RunId });
            return new PatchResult(true, PatchCount, []);
        }
    }
}
