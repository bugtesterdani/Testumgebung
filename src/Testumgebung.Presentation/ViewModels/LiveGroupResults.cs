/**
 * @file
 * @brief Bildet die Live-Bewertung aus Gruppeninstanzen ab, ohne frühere Versuche erneut einzumischen.
 */
using Testumgebung.Contracts;
using Testumgebung.Engine;

namespace Testumgebung.Presentation.ViewModels;

/// <summary>Bewertungen aktiver Gruppen und ihrer abgeschlossenen Kinder für die Werkeranzeige.</summary>
public sealed class LiveGroupResults
{
    private sealed class Group(string nodeId, string? parent)
    {
        public string NodeId { get; } = nodeId;
        public string? Parent { get; } = parent;
        public ResultRating Rating { get; set; }
        public bool Completed { get; set; }
    }
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Scope, ResultRating Rating)> _steps = new(StringComparer.Ordinal);
    private string? _dut;
    private bool _hasDut;

    public void Clear()
    {
        _groups.Clear(); _latest.Clear(); _steps.Clear();
        _dut = null; _hasDut = false;
    }

    public void Apply(EngineEvent item)
    {
        switch (item)
        {
            case GroupEntered entered:
                Enter(entered);
                break;
            case StepCompleted step when _groups.TryGetValue(step.GroupScopeId, out var owner):
                owner.Rating = owner.Rating.Merge(step.Rating);
                var previous = _steps.GetValueOrDefault(step.NodeId);
                _steps[step.NodeId] = (step.GroupScopeId, previous.Scope == step.GroupScopeId ? previous.Rating.Merge(step.Rating) : step.Rating);
                break;
            case GroupCompleted completed when _groups.TryGetValue(completed.ScopeId, out var group):
                group.Rating = completed.Rating;
                group.Completed = true;
                if (group.Parent is { } parentId && _groups.TryGetValue(parentId, out var parent))
                    parent.Rating = parent.Rating.Merge(completed.Rating);
                break;
            case GroupIterationStarted { ResultReset: true } iteration when _groups.TryGetValue(iteration.ScopeId, out var repeated):
                repeated.Rating = ResultRating.NResult;
                Remove(iteration.ScopeId, includeRoot: false);
                break;
            case DutRunStarted dut when _groups.ContainsKey(dut.ScopeId):
                _dut = dut.ScopeId; _hasDut = true;
                break;
        }
    }

    private void Enter(GroupEntered entered)
    {
        // Mehrere parallele Aufrufe desselben grafischen Bausteins können in anderer Reihenfolge enden.
        foreach (var old in _groups.Where(entry => entry.Value.NodeId == entered.NodeId && entry.Value.Completed)
            .Select(entry => entry.Key).ToArray())
            Remove(old, includeRoot: true);
        _groups[entered.ScopeId] = new Group(entered.NodeId, entered.ParentScopeId);
        _latest[entered.NodeId] = entered.ScopeId;
    }

    public ResultRating? ForNode(string nodeId)
    {
        if (_latest.TryGetValue(nodeId, out var scope)) return Effective(scope);
        return _steps.TryGetValue(nodeId, out var step) ? step.Rating : null;
    }

    /// <summary>Ein abgeschlossener Prüfling bleibt sichtbar, bis ein neuer Prüfling beginnt.</summary>
    public ResultRating? CurrentRating => _hasDut
        ? _dut is { } dut && _groups.TryGetValue(dut, out var group) && !group.Completed ? Effective(dut) : null
        : _groups.Where(entry => entry.Value.Parent is null).Select(entry => Effective(entry.Key))
            .Aggregate(ResultRating.NResult, (rating, child) => rating.Merge(child));

    private ResultRating Effective(string scope)
    {
        var rating = _groups[scope].Rating;
        foreach (var child in _groups.Where(entry => entry.Value.Parent == scope && !entry.Value.Completed))
            rating = rating.Merge(Effective(child.Key));
        return rating;
    }

    private bool Within(string scope, string root)
    {
        while (_groups.TryGetValue(scope, out var group))
        {
            if (scope == root) return true;
            if (group.Parent is not { } parent) return false;
            scope = parent;
        }
        return false;
    }

    private void Remove(string root, bool includeRoot)
    {
        var scopes = _groups.Keys.Where(scope => Within(scope, root)).ToHashSet(StringComparer.Ordinal);
        foreach (var step in _steps.Where(entry => scopes.Contains(entry.Value.Scope)).Select(entry => entry.Key).ToArray())
            _steps.Remove(step);
        if (!includeRoot) scopes.Remove(root);
        foreach (var scope in scopes)
        {
            var nodeId = _groups[scope].NodeId;
            if (_latest.GetValueOrDefault(nodeId) == scope) _latest.Remove(nodeId);
            _groups.Remove(scope);
            if (_dut == scope) _dut = null;
        }
    }
}
