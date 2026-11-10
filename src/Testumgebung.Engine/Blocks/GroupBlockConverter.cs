/**
 * @file
 * @brief Ermittelt die Schnittstelle einer wiederverwendbaren Gruppe und ersetzt sie durch einen Bibliotheksaufruf.
 */

using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
using Testumgebung.Domain.Scopes;

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
        var outsideTypes = outside.ToDictionary(variable => variable.Name, variable => variable.Type, StringComparer.Ordinal);
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
            var usage2 = usage.TryGetValue(name, out var existing) ? existing : usage[name] = new Usage();
            if (write) usage2.Write = true;
            else usage2.Read = true;
        }

        void Expr(string? text, HashSet<string> locals)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                foreach (var referencedName in Expression.Parse(text).ReferencedNames) Note(referencedName, false, locals);
            }
            catch (ExpressionException)
            {
                // fehlerhafte Ausdrücke meldet die Konfigurationsprüfung
            }
        }

        void Walk(GroupDefinition group2, HashSet<string> parentLocals)
        {
            var locals = new HashSet<string>(parentLocals, StringComparer.Ordinal);
            // Jede Gruppe besitzt ihr eigenes Ergebnis; es ist keine äußere Port-Abhängigkeit.
            locals.Add(Scope.GroupResultVariable);
            foreach (var variable in group2.Variables) locals.Add(variable.Name);
            if (group2.Resources.Count > 0) locals.Add(TestRun.ResourcesAcquiredVariable);
            Expr(group2.EntryCondition, locals);
            Expr(group2.Repetition.Expression, locals);
            if (group2.IsDutRun) Note(group2.DutIdVariable, true, locals);

            foreach (var child in group2.Children.Concat(group2.ExitGroup is null ? [] : [group2.ExitGroup]))
            {
                if (child is GroupDefinition cg)
                {
                    Walk(cg, locals);
                    continue;
                }
                if (child is not StepDefinition { BlockType: { } type } step) continue;
                if (type == BlockCatalog.Code)
                {
                    var visible = locals.Select(local => (n: local, VarType.Double))
                        .Concat(outside.Where(variable2 => !locals.Contains(variable2.Name)))
                        .ToList();
                    var lang = BlockCatalog.ParseLanguage(step.Parameters.GetValueOrDefault("Sprache") ?? "C#");
                    var deps = CodeBlockConverter.Analyze(lang, step.Parameters.GetValueOrDefault("Quelltext") ?? "", visible);
                    foreach (var port in deps.Ports)
                    {
                        if (port.IsInput) Note(port.Name, false, locals);
                        if (port.IsOutput) Note(port.Name, true, locals);
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
            .Where(variable2 => usage.ContainsKey(variable2.Name))
            .Select(variable2 =>
            {
                var usage3 = usage[variable2.Name];
                var direction = usage3.Read && usage3.Write ? PortDirection.InOut : usage3.Write ? PortDirection.Output : PortDirection.Input;
                return new CodePort(variable2.Name, variable2.Type, direction);
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
            Parallel = group.Parallel,
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
        foreach (var port in deps.Ports)
        {
            if (port.IsInput) parameters[BlockCatalog.InputBindingPrefix + port.Name] = port.Name;
            if (port.Direction == PortDirection.Output) parameters[BlockCatalog.OutputBindingPrefix + port.Name] = port.Name;
        }
        var step = catalog.Create(BlockCatalog.GroupBlock, originalId, group.Name, parameters);
        step.Description = group.Description;
        step.Disabled = group.Disabled;
        step.BreakpointRule = group.BreakpointRule;

        parent.Children[index] = step;
        definition.GroupLibrary.Add(block);
        return (block, step, deps.Warnings);
    }
}
