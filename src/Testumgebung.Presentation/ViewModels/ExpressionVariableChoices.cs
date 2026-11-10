using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Scopes;
using Testumgebung.Domain.Expressions;
using Testumgebung.Engine;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Variablen im tatsächlichen Scope der ausgewählten Eingabe, mit eindeutigen Elternpfaden.</summary>
public static class ExpressionVariableChoices
{
    public static IReadOnlyList<ExpressionVariable> ForNode(TestDefinition definition, string nodeId, bool writable = false)
    {
        var groups = definition.AncestorsOf(nodeId).Reverse().ToList();
        if (definition.Find(nodeId) is GroupDefinition selected) groups.Insert(0, selected);
        var result = new List<ExpressionVariable>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int level = 0;
        foreach (var group in groups)
        {
            var prefix = string.Concat(Enumerable.Repeat("../", level));
            AddGroupVariables(group, prefix, result, names, writable);
            if (group.IsInterfaceBoundary) return result;
            level++;
        }
        if (definition.OwningGroupBlock(nodeId) is { } block)
            foreach (var port in block.Ports) Add(result, names, string.Concat(Enumerable.Repeat("../", level)), port.Name, port.Type, block.Name);
        return result;
    }

    private static void AddGroupVariables(GroupDefinition group, string prefix, List<ExpressionVariable> result, HashSet<string> names, bool writable)
    {
        foreach (var variable in group.Variables.Where(variable => !writable || !variable.Name.StartsWith("_BlockZweig_", StringComparison.Ordinal)))
            Add(result, names, prefix, variable.Name, variable.Type, group.Name);
        if (!writable) Add(result, names, prefix, Scope.GroupResultVariable, VarType.String, group.Name);
        if (group.Resources.Count > 0) Add(result, names, prefix, TestRun.ResourcesAcquiredVariable, VarType.Bool, group.Name);
    }
    public static bool IsPath(string source)
    {
        try { if (source.Length > 32768) return false; var syntax = Expression.Parse(source, 2, 1).ToSyntax(); return syntax.Kind == ExpressionSyntaxKind.Variable && syntax.Text == source; }
        catch (ExpressionException) { return false; }
    }
    private static void Add(List<ExpressionVariable> result, HashSet<string> names, string prefix, string name, VarType type, string origin)
    {
        // Jeder Vorfahr ist explizit erreichbar, auch wenn der kurze Name nicht verdeckt ist.
        if (names.Add(name) && IsPath(name)) result.Add(new(name, type, origin));
        if (prefix.Length > 0 && IsPath(prefix + name)) result.Add(new(prefix + name, type, origin));
    }
}
