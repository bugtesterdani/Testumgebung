/**
 * @file
 * @brief Hält dynamisch benutzte Gerätesperren bis zum Ende eines parallelen Schritts.
 */
using Testumgebung.Contracts;

namespace Testumgebung.Engine;

internal sealed class ParallelStepAccess(TestRun run, string branch, string scopeId, string name, CancellationToken phase) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(phase);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly List<IResourceLease> _leases = [];
    private bool _disposed;
    public CancellationToken Token => _lifetime.Token;

    public async Task AcquireAsync(IEnumerable<string> resources)
    {
        await _gate.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var keys = resources.Distinct().Where(k => !_keys.Contains(k)).Order(StringComparer.Ordinal).ToList();
            if (keys.Count == 0) return;
            var request = new ResourceRequest(run.RunId, scopeId, keys, run.Options.StationName, name) { Branch = branch };
            var lease = await run.Resources.AcquireAsync(request, null, Token).ConfigureAwait(false);
            _leases.Add(lease);
            _keys.UnionWith(keys);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            foreach (var lease in _leases.AsEnumerable().Reverse()) lease.Dispose();
        }
        finally { _gate.Release(); }
        // Unregister from the run token: endless loops must not retain completed steps.
        _lifetime.Dispose();
    }
}
