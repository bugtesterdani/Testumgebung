/**
 * @file
 * @brief Ermittelt die Schnittstelle einer wiederverwendbaren Gruppe und ersetzt sie durch einen Bibliotheksaufruf.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

/// <summary>
/// „Aus Gruppe erstellen“: Eine Gruppe wird zum wiederverwendbaren grafischen Baustein.
/// Verwendete äußere Variablen werden Ports; die Gruppe wird durch einen Bausteinaufruf mit
/// gleichwertigen Bindungen ersetzt (Knoten-ID bleibt erhalten).
/// </summary>
public static class GroupBlockConverter
{
    private sealed class Usage
    {
        public bool Read;
        public bool Write;
    }

    /// <summary>Ermittelt, welche außerhalb definierten Variablen die Gruppe liest bzw. schreibt.</summary>
    public static CodeDependencies Analyze(TestDefinition definition, GroupDefinition group, BlockCatalog catalog)
    {
        var outside = CodeBlockConverter.VisibleVariables(definition, group.Id);
        var outsideTypes = outside.ToDictionary(v => v.Name, v => v.Type, StringComparer.Ordinal);
        var usage = new Dictionary<string, Usage>(StringComparer.Ordinal);
        var warnings = new List<string>();

        void Note(string name, bool write, HashSet<string> locals)
        {
            if (name.StartsWith("../", StringComparison.Ordinal))
            {
                warnings.Add($"„{name}“ greift über die Gruppe hinaus – im Baustein nicht möglich, bitte anpassen.");
                return;
            }
            if (locals.Contains(name) || !outsideTypes.ContainsKey(name)) return;
            var u = usage.TryGetValue(name, out var existing) ? existing : usage[name] = new Usage();
            if (write) u.Write = true;
            else u.Read = true;
        }

        void Expr(string? text, HashSet<string> locals)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                foreach (var n in Expression.Parse(text).ReferencedNames) Note(n, false, locals);
            }
            catch (ExpressionException)
            {
                // fehlerhafte Ausdrücke meldet die Konfigurationsprüfung
            }
        }

        void Walk(GroupDefinition g, HashSet<string> parentLocals)
        {
            var locals = new HashSet<string>(parentLocals, StringComparer.Ordinal);
            foreach (var v in g.Variables) locals.Add(v.Name);
            if (g.Resources.Count > 0) locals.Add(TestRun.ResourcesAcquiredVariable);
            Expr(g.EntryCondition, locals);
            Expr(g.Repetition.Expression, locals);
            if (g.IsDutRun) Note(g.DutIdVariable, true, locals);

            foreach (var child in g.Children.Concat(g.ExitGroup is null ? [] : [g.ExitGroup]))
            {
                if (child is GroupDefinition cg)
                {
                    Walk(cg, locals);
                    continue;
                }
                if (child is not StepDefinition { BlockType: { } type } step) continue;
                if (type == BlockCatalog.Code)
                {
                    var visible = locals.Select(n => (n, VarType.Double))
                        .Concat(outside.Where(o => !locals.Contains(o.Name)))
                        .ToList();
                    var lang = BlockCatalog.ParseLanguage(step.Parameters.GetValueOrDefault("Sprache") ?? "C#");
                    var deps = CodeBlockConverter.Analyze(lang, step.Parameters.GetValueOrDefault("Quelltext") ?? "", visible);
                    foreach (var p in deps.Ports)
                    {
                        if (p.IsInput) Note(p.Name, false, locals);
                        if (p.IsOutput) Note(p.Name, true, locals);
                    }
                    warnings.AddRange(deps.Warnings);
                    continue;
                }
                if (!catalog.TryGet(type, out var descriptor)) continue;
                foreach (var param in descriptor.Parameters)
                {
                    var value = step.Parameters.GetValueOrDefault(param.Name);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    if (param.Kind == ParameterKind.Expression) Expr(value, locals);
                    else if (param.Kind == ParameterKind.Variable) Note(value.Trim(), true, locals);
                }
                foreach (var (key, value) in step.Parameters)
                {
                    if (key.StartsWith(BlockCatalog.InputBindingPrefix, StringComparison.Ordinal)) Expr(value, locals);
                    else if (key.StartsWith(BlockCatalog.OutputBindingPrefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value))
                        Note(value.Trim(), true, locals);
                }
            }
        }

        Walk(group, []);
        var ports = outside
            .Where(o => usage.ContainsKey(o.Name))
            .Select(o =>
            {
                var u = usage[o.Name];
                var direction = u.Read && u.Write ? PortDirection.InOut : u.Write ? PortDirection.Output : PortDirection.Input;
                return new CodePort(o.Name, o.Type, direction);
            })
            .ToList();
        return new CodeDependencies(ports, warnings.Distinct().ToList());
    }

    /// <summary>
    /// Wandelt die Gruppe um: Körper wandert in die Bausteinbibliothek (Bausteingrenze), an ihrer Stelle
    /// steht ein Gruppenbaustein-Schritt mit derselben ID und Bindungen auf die gleichnamigen Variablen.
    /// </summary>
    public static (GroupBlockDefinition Block, StepDefinition Step, IReadOnlyList<string> Warnings) Convert(
        TestDefinition definition, GroupDefinition group, string blockId, BlockCatalog catalog)
    {
        var parent = definition.AncestorsOf(group.Id).LastOrDefault()
                     ?? throw new ArgumentException("Die Wurzel kann nicht umgewandelt werden.", nameof(group));
        if (parent.ExitGroup == group) throw new ArgumentException("Exit-Gruppen können nicht umgewandelt werden.", nameof(group));
        if (definition.Root.Children.Contains(group) || definition.Root.ExitGroup == group)
            throw new ArgumentException("Init, Loop und Finalization können nicht umgewandelt werden.", nameof(group));

        var deps = Analyze(definition, group, catalog);
        var index = parent.Children.IndexOf(group);
        var originalId = group.Id;

        // Körper: dieselbe Gruppe unter neuer ID (die alte ID übernimmt der Aufruf-Schritt).
        var body = new GroupDefinition(blockId + ".koerper", group.Name)
        {
            Description = group.Description,
            EntryCondition = group.EntryCondition,
            Repetition = group.Repetition,
            ResultBeforeRepeat = group.ResultBeforeRepeat,
            ExitGroup = group.ExitGroup,
            ResourceTimeout = group.ResourceTimeout,
            IsDutRun = group.IsDutRun,
            DutIdVariable = group.DutIdVariable,
        };
        body.Children.AddRange(group.Children);
        body.Variables.AddRange(group.Variables);
        body.Resources.AddRange(group.Resources);

        var block = new GroupBlockDefinition(blockId, group.Name, body) { Description = group.Description };
        block.Ports.AddRange(deps.Ports);

        var parameters = new Dictionary<string, string> { ["Baustein"] = blockId };
        foreach (var p in deps.Ports)
        {
            if (p.IsInput) parameters[BlockCatalog.InputBindingPrefix + p.Name] = p.Name;
            if (p.Direction == PortDirection.Output) parameters[BlockCatalog.OutputBindingPrefix + p.Name] = p.Name;
        }
        var step = catalog.Create(BlockCatalog.GroupBlock, originalId, group.Name, parameters);
        step.Description = group.Description;

        parent.Children[index] = step;
        definition.GroupLibrary.Add(block);
        return (block, step, deps.Warnings);
    }
}
