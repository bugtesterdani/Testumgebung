using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Presentation.Releases;

/// <summary>
/// Kennt die aktuell freigegebenen Versionen (je Ablauf) für den Werkerbetrieb. Lädt Pakete geprüft (Hashes),
/// meldet neue Freigaben und fragt die Ablage regelmäßig ab (andere Prüfplätze, gemeinsame Datenbank).
/// Jeder Abruf liefert eine eigene Kopie – laufende Prüflinge behalten ihre Version.
/// </summary>
public sealed class ReleaseHub : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ReleaseInfo Info, string Json)> _released = new(StringComparer.Ordinal);
    private readonly ProjectSerializer _serializer = new();
    private CancellationTokenSource? _poll;
    private IReadOnlyList<ReleaseInfo> _all = [];

    public ReleaseHub(ReleaseService service)
    {
        Service = service;
        service.Changed += () => _ = RefreshAsync();
    }

    public ReleaseService Service { get; }

    public IReadOnlyList<ReleaseInfo> All
    {
        get
        {
            lock (_gate) return _all;
        }
    }

    /// <summary>Ablage nicht erreichbar (z. B. Datenbank aus) – der zuletzt geladene Stand bleibt nutzbar.</summary>
    public string? Error { get; private set; }

    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            string? rejected = null;
            var all = await Service.ListAsync(ct).ConfigureAwait(false);
            foreach (var current in all.Where(r => r.State == ReleaseState.Released).GroupBy(r => r.ProjectName)
                         .Select(g => g.OrderByDescending(r => r.ReleasedAt).First()))
            {
                bool known;
                lock (_gate) known = _released.TryGetValue(current.ProjectName, out var cached) && cached.Info.ReleaseId == current.ReleaseId;
                if (known) continue;
                var opened = await Service.OpenAsync(current, ct).ConfigureAwait(false);
                if (!opened.IsIntact)
                {
                    rejected = $"Freigegebene Version {current.Version} ist beschädigt und wird nicht verwendet: {string.Join("; ", opened.Problems)}";
                    continue;
                }
                var differences = await Service.VerifyEnvironmentAsync(current, ct).ConfigureAwait(false);
                if (differences.Count > 0)
                {
                    rejected = $"Freigegebene Version {current.Version}: Laufzeitumgebung weicht ab: {string.Join("; ", differences)}";
                    continue;
                }
                lock (_gate) _released[current.ProjectName] = (current, opened.ProjectJson);
            }
            lock (_gate)
            {
                _all = all;
                foreach (var name in _released.Keys.Where(n => all.All(r => r.ProjectName != n || r.State != ReleaseState.Released)).ToList())
                    _released.Remove(name); // keine freigegebene Version mehr (z. B. andere Ablage)
            }
            Error = rejected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = "Release-Ablage nicht erreichbar: " + ex.Message;
        }
        Changed?.Invoke();
    }

    /// <summary>Eigene Kopie der freigegebenen Version (null = keine).</summary>
    public (ReleaseInfo Info, TestProject Project)? GetReleased(string projectName)
    {
        (ReleaseInfo Info, string Json) entry;
        lock (_gate)
        {
            if (!_released.TryGetValue(projectName, out entry)) return null;
        }
        return (entry.Info, _serializer.Deserialize(entry.Json));
    }

    public ReleaseInfo? CurrentInfo(string projectName)
    {
        lock (_gate) return _released.TryGetValue(projectName, out var e) ? e.Info : null;
    }

    /// <summary>Regelmäßig abfragen (Freigaben anderer Prüfplätze in der gemeinsamen Datenbank).</summary>
    public void StartPolling(TimeSpan interval)
    {
        if (_poll is not null) return;
        _poll = new CancellationTokenSource();
        var token = _poll.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(interval, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                await RefreshAsync(token).ConfigureAwait(false);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_poll is not null) await _poll.CancelAsync().ConfigureAwait(false);
    }
}
