using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>
/// Zentrale, faire Ressourcenvermittlung (Abschnitt 7, Vorschlag):
/// <list type="bullet">
/// <item>Atomarer Erwerb aller angeforderten Ressourcen – nie Teilbesitz.</item>
/// <item>FIFO-Fairness: eine Anforderung überholt keine frühere wartende Anforderung, die eine gemeinsame Ressource benötigt.</item>
/// <item>Besitz an Run-ID gebunden; erneuter Erwerb durch denselben Run (verschachtelte Gruppen) wird referenzgezählt.</item>
/// <item>Timeout und Abbruch entfernen die Anforderung aus der Warteschlange.</item>
/// <item>Sicherheitsnetz zur statischen Zyklusprüfung: eine Anforderung, die gegenseitiges Warten erzeugen würde
/// (Wartegraph mit Zyklus), wird sofort mit <see cref="ResourceDeadlockException"/> abgelehnt statt ewig zu hängen.</item>
/// </list>
/// </summary>
public sealed class ResourceManager(IClock clock) : IResourceManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Owner, int Count)> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? OwnerName, string? ScopeName, DateTimeOffset Since)> _ownerInfo = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> _queue = new();

    public ResourceManager() : this(SystemClock.Instance)
    {
    }

    /// <summary>Wird bei jeder Zustandsänderung ausgelöst (für Anzeige „Wartestatus“).</summary>
    public event Action? Changed;

    public async Task<IResourceLease> AcquireAsync(ResourceRequest request, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (request.Resources.Count == 0) return new Lease(this, request);
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_gate)
        {
            if (CanGrant(request, before: null))
            {
                Grant(request);
                waiter = null!;
            }
            else
            {
                waiter = new Waiter(request);
                var node = _queue.AddLast(waiter);
                if (FindCycle(request.OwnerRunId) is { } cycle)
                {
                    _queue.Remove(node);
                    throw new ResourceDeadlockException(
                        $"Gegenseitiges Warten erkannt ({cycle}): Anforderung {string.Join(", ", request.Resources)} abgelehnt. " +
                        "Ressourcen gemeinsam in einer Gruppe anfordern oder überall in derselben Reihenfolge sperren.");
                }
            }
        }
        Changed?.Invoke();
        if (waiter is null) return new Lease(this, request);

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = timeout is { } t
            ? clock.Delay(t, delayCts.Token)
            : Task.Delay(Timeout.Infinite, delayCts.Token);

        var finished = await Task.WhenAny(waiter.Granted.Task, limit).ConfigureAwait(false);
        delayCts.Cancel();

        if (finished == waiter.Granted.Task) return new Lease(this, request);

        bool removed;
        lock (_gate)
        {
            removed = _queue.Remove(waiter);
            if (removed) ProcessQueue();
        }
        Changed?.Invoke();

        // Wettlauf: inzwischen zugeteilt → Sperre behalten.
        if (!removed) return new Lease(this, request);

        cancellationToken.ThrowIfCancellationRequested();
        throw new ResourceTimeoutException(
            $"Zeitlimit {timeout} beim Warten auf {string.Join(", ", request.Resources)} überschritten.");
    }

    public bool IsHeldBy(string resource, string runId)
    {
        lock (_gate) return _owners.TryGetValue(resource, out var o) && o.Owner == runId;
    }

    public IReadOnlyList<ResourceOwnership> Snapshot()
    {
        lock (_gate)
        {
            var names = _owners.Keys.Concat(_queue.SelectMany(w => w.Request.Resources)).Distinct().OrderBy(n => n);
            return names.Select(n =>
            {
                var held = _owners.TryGetValue(n, out var o);
                _ownerInfo.TryGetValue(n, out var info);
                var waiting = _queue.Where(w => w.Request.Resources.Contains(n)).Select(w => w.Request).ToList();
                return new ResourceOwnership(
                    n,
                    held ? o.Owner : null,
                    held ? o.Count : 0,
                    waiting.Select(w => w.OwnerRunId).ToList(),
                    held ? info.OwnerName : null,
                    waiting.Select(w => w.OwnerName ?? w.OwnerRunId).ToList(),
                    held ? info.ScopeName : null,
                    held ? info.Since : null);
            }).ToList();
        }
    }

    private bool CanGrant(ResourceRequest request, LinkedListNode<Waiter>? before)
    {
        foreach (var r in request.Resources)
        {
            if (_owners.TryGetValue(r, out var o) && o.Owner != request.OwnerRunId) return false;
        }

        // Fairness: frühere Wartende anderer Runs mit überlappenden, noch nicht selbst gehaltenen Ressourcen haben Vorrang.
        for (var n = _queue.First; n is not null && n != before; n = n.Next)
        {
            var other = n.Value.Request;
            if (other.OwnerRunId == request.OwnerRunId) continue;
            foreach (var r in request.Resources)
            {
                var ownedBySelf = _owners.TryGetValue(r, out var o) && o.Owner == request.OwnerRunId;
                if (!ownedBySelf && other.Resources.Contains(r)) return false;
            }
        }
        return true;
    }

    private void Grant(ResourceRequest request)
    {
        foreach (var r in request.Resources.Distinct())
        {
            if (_owners.TryGetValue(r, out var o))
            {
                _owners[r] = (o.Owner, o.Count + 1);
            }
            else
            {
                _owners[r] = (request.OwnerRunId, 1);
                _ownerInfo[r] = (request.OwnerName, request.ScopeName, clock.Now);
            }
        }
    }

    private void Release(ResourceRequest request)
    {
        lock (_gate)
        {
            foreach (var r in request.Resources.Distinct())
            {
                if (!_owners.TryGetValue(r, out var o) || o.Owner != request.OwnerRunId) continue;
                if (o.Count <= 1)
                {
                    _owners.Remove(r);
                    _ownerInfo.Remove(r);
                }
                else _owners[r] = (o.Owner, o.Count - 1);
            }
            ProcessQueue();
        }
        Changed?.Invoke();
    }

    private void ProcessQueue()
    {
        var node = _queue.First;
        while (node is not null)
        {
            var next = node.Next;
            if (CanGrant(node.Value.Request, before: node))
            {
                Grant(node.Value.Request);
                _queue.Remove(node);
                node.Value.Granted.TrySetResult();
            }
            node = next;
        }
    }

    /// <summary>Wartegraph: Run → Runs, auf die er wartet (Besitzer und vorrangige Wartende). Unter <c>_gate</c>.</summary>
    private HashSet<string> WaitsFor(string run)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var n = _queue.First; n is not null; n = n.Next)
        {
            var req = n.Value.Request;
            if (req.OwnerRunId != run) continue;
            foreach (var r in req.Resources)
                if (_owners.TryGetValue(r, out var o) && o.Owner != run) result.Add(o.Owner);
            for (var e = _queue.First; e is not null && e != n; e = e.Next)
            {
                var other = e.Value.Request;
                if (other.OwnerRunId == run) continue;
                if (req.Resources.Any(r => other.Resources.Contains(r) && !(_owners.TryGetValue(r, out var o) && o.Owner == run)))
                    result.Add(other.OwnerRunId);
            }
        }
        return result;
    }

    /// <summary>Zyklus im Wartegraph, der beim Run beginnt (Text für die Meldung) oder null.</summary>
    private string? FindCycle(string start)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in _queue) names[w.Request.OwnerRunId] = w.Request.OwnerName ?? w.Request.OwnerRunId;
        foreach (var (key, info) in _ownerInfo)
            if (info.OwnerName is { } on && _owners.TryGetValue(key, out var o)) names[o.Owner] = on;
        string Name(string run) => names.TryGetValue(run, out var n) ? n : run;

        var path = new List<string> { start };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Dfs(string run)
        {
            foreach (var next in WaitsFor(run))
            {
                if (next == start) return true;
                if (!visited.Add(next)) continue;
                path.Add(next);
                if (Dfs(next)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        return Dfs(start) ? string.Join(" → ", path.Append(start).Select(Name)) : null;
    }

    private sealed class Waiter(ResourceRequest request)
    {
        public ResourceRequest Request { get; } = request;
        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(ResourceManager owner, ResourceRequest request) : IResourceLease
    {
        private int _disposed;

        public ResourceRequest Request { get; } = request;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Request.Resources.Count > 0)
                owner.Release(Request);
        }
    }
}
