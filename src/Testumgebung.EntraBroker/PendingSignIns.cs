using System.Security.Cryptography;
using Testumgebung.Identity;

namespace Testumgebung.EntraBroker;

/// <summary>Separate browser and result keys. Only the desktop receives the latter.</summary>
internal sealed class PendingSignIns(TimeProvider clock)
{
    private sealed record Entry(string BrowserKey, DateTimeOffset Expires)
    {
        public bool Started { get; set; }
        public EntraBrokerResult? Result { get; set; }
    }

    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public EntraBrokerAttempt? Create(string tenant, string client, string callback)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var item in _pending.Where(item => item.Value.Expires <= now).ToArray())
                _pending.Remove(item.Key);
            if (_pending.Count >= 128) return null;

            var browser = NewKey();
            var result = NewKey();
            _pending.Add(result, new Entry(browser, now + EntraBrokerProtocol.Lifetime));
            return new(browser, result, tenant, client, callback);
        }
    }

    public bool Begin(string browser)
    {
        lock (_gate)
        {
            var entry = FindBrowser(browser);
            if (entry is null || entry.Started) return false;
            entry.Started = true;
            return true;
        }
    }

    public bool Complete(string browser, EntraBrokerResult result)
    {
        lock (_gate)
        {
            var entry = FindBrowser(browser);
            if (entry is not { Started: true, Result: null }) return false;
            entry.Result = result;
            return true;
        }
    }

    public EntraBrokerResult? Take(string resultKey)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(resultKey, out var entry) || entry.Expires <= clock.GetUtcNow())
                return null;
            if (entry.Result is null) return new("pending");
            _pending.Remove(resultKey);
            return entry.Result;
        }
    }

    public void Cancel(string resultKey)
    {
        lock (_gate) _pending.Remove(resultKey);
    }

    private Entry? FindBrowser(string browser) =>
        _pending.Values.FirstOrDefault(entry => entry.BrowserKey == browser && entry.Expires > clock.GetUtcNow());

    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
