namespace Testumgebung.Domain.Labels.CabJScript;

/// <summary>Undo stores byte patches, never repeated copies of unchanged binary resources.</summary>
public sealed class CabSourceHistory(CabSourceEditor initial, int capacity = 100)
{
    private sealed record Operation(CabTextChange Forward, CabTextChange Backward);
    private readonly List<Operation> _past = [];
    private readonly Stack<Operation> _future = [];
    public CabSourceEditor Current { get; private set; } = initial;
    public bool CanUndo => _past.Count > 0;
    public bool CanRedo => _future.Count > 0;

    public bool Record(CabSourceEditor next)
    {
        if (next.SetupCodePage != Current.SetupCodePage) throw new ArgumentException("Die Quelltexthistorie benötigt dieselbe Setup-Codepage.", nameof(next));
        var before = Current.Document.Write(); var after = next.Document.Write();
        var first = 0;
        while (first < before.Length && first < after.Length && before[first] == after[first]) first++;
        if (first == before.Length && first == after.Length) return false;
        var oldEnd = before.Length; var newEnd = after.Length;
        while (oldEnd > first && newEnd > first && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
        _past.Add(new(new(new(first, oldEnd - first), after[first..newEnd]), new(new(first, newEnd - first), before[first..oldEnd])));
        if (_past.Count > Math.Max(1, capacity)) _past.RemoveAt(0);
        _future.Clear(); Current = next;
        return true;
    }

    public CabSourceEditor Undo()
    {
        if (!CanUndo) return Current;
        var operation = _past[^1]; _past.RemoveAt(_past.Count - 1); _future.Push(operation);
        return Apply(operation.Backward);
    }

    public CabSourceEditor Redo()
    {
        if (!CanRedo) return Current;
        var operation = _future.Pop(); _past.Add(operation);
        return Apply(operation.Forward);
    }

    private CabSourceEditor Apply(CabTextChange change)
        => Current = new(Current.Document.Write([change]), Current.SetupCodePage, Current.Settings);
}
