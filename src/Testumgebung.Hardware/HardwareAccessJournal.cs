using System.Collections.Concurrent;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>Ein Gerätezugriff eines Laufs (Prüfplatz) über die zentrale Hardwarevermittlung.</summary>
public sealed record HardwareAccess(
    long Sequence,
    DateTimeOffset Start,
    DateTimeOffset? End,
    string RunId,
    string Station,
    string Resource,
    string Key,
    string Operation,
    bool Locked,
    string? Conflict);

/// <summary>
/// Zugriffsprotokoll gemeinsam genutzter Geräte (Etappe 9): jeder Zugriff wird mit Lauf, Prüfplatz, Sperrschlüssel und
/// Dauer erfasst. Ein Zugriff gilt als widersprüchlich, wenn ein anderer Lauf zur selben Zeit dasselbe Gerät/den
/// Kanal benutzt oder der Zugriff auf gesperrter Hardware ohne eigene Reservierung erfolgt.
/// </summary>
public sealed class HardwareAccessJournal(IResourceManager resources, IClock? clock = null, int capacity = 400)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Active>> _active = new(StringComparer.Ordinal);
    private readonly LinkedList<HardwareAccess> _recent = new();
    private readonly ConcurrentQueue<HardwareAccess> _conflicts = new();
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private long _sequence;
    private long _count;

    private sealed class Active(long sequence, string runId, string station, string resource, string key, string op, bool locked, DateTimeOffset start)
    {
        public long Sequence { get; } = sequence;
        public string RunId { get; } = runId;
        public string Station { get; } = station;
        public string Resource { get; } = resource;
        public string Key { get; } = key;
        public string Operation { get; } = op;
        public bool Locked { get; } = locked;
        public DateTimeOffset Start { get; } = start;
        public string? Conflict { get; set; }
    }

    /// <summary>Nach jedem abgeschlossenen Zugriff (Anzeige).</summary>
    public event Action? Changed;

    /// <summary>Anzahl erfasster Zugriffe.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Widersprüchliche Zugriffe (sollte immer leer sein).</summary>
    public IReadOnlyList<HardwareAccess> Conflicts => _conflicts.ToArray();

    /// <summary>Letzte Zugriffe, neueste zuerst.</summary>
    public IReadOnlyList<HardwareAccess> Recent()
    {
        lock (_gate) return _recent.ToList();
    }

    /// <summary>Zugriff beginnen; <see cref="IDisposable.Dispose"/> beendet ihn.</summary>
    public IDisposable Begin(string runId, string station, ResourceBinding binding, string operation)
    {
        var locked = binding.RequiresLock;
        Active access;
        lock (_gate)
        {
            access = new Active(++_sequence, runId, station, binding.Name, binding.Key, operation, locked, _clock.Now);
            if (!_active.TryGetValue(binding.Key, out var list)) _active[binding.Key] = list = [];
            var other = list.FirstOrDefault(a => a.RunId != runId);
            if (other is not null)
                access.Conflict = $"gleichzeitig mit {other.Station} ({other.Resource}, {other.Operation})";
            else if (locked && !resources.IsHeldBy(binding.Key, runId))
                access.Conflict = "ohne eigene Reservierung";
            list.Add(access);
        }
        return new Ending(this, access);
    }

    private void End(Active access)
    {
        HardwareAccess entry;
        lock (_gate)
        {
            if (_active.TryGetValue(access.Key, out var list))
            {
                list.Remove(access);
                if (list.Count == 0) _active.Remove(access.Key);
            }
            if (access.Conflict is null && access.Locked && !resources.IsHeldBy(access.Key, access.RunId))
                access.Conflict = "Reservierung während des Zugriffs verloren";
            entry = new HardwareAccess(access.Sequence, access.Start, _clock.Now, access.RunId, access.Station, access.Resource,
                access.Key, access.Operation, access.Locked, access.Conflict);
            _recent.AddFirst(entry);
            while (_recent.Count > capacity) _recent.RemoveLast();
        }
        Interlocked.Increment(ref _count);
        if (entry.Conflict is not null) _conflicts.Enqueue(entry);
        Changed?.Invoke();
    }

    private sealed class Ending(HardwareAccessJournal journal, Active access) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) journal.End(access);
        }
    }
}
