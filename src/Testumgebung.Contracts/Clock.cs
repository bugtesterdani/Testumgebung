/**
 * @file
 * @brief Trennt die Engine von der realen Uhr, damit Wiederholungen und Zeitlimits kontrolliert getestet werden können.
 */

namespace Testumgebung.Contracts;

/// <summary>
/// Zeitquelle der Engine. Ermöglicht eine deterministische Uhr für Wiederholungen und Timeouts in Tests.
/// </summary>
public interface IClock
{
    DateTimeOffset Now { get; }

    /// <summary>Wartet die angegebene Dauer; bricht bei Cancellation mit <see cref="OperationCanceledException"/> ab.</summary>
    Task Delay(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>Optionale virtuelle Zeitachsen für parallele Zweige; beim Zusammenführen gilt die späteste Endzeit.</summary>
public interface IParallelClock : IClock
{
    /// <summary>Gibt an, ob der aktuelle Zweig seine virtuelle Zeit selbst vorrückt.</summary>
    bool AdvancesBranches { get; }
    /// <summary>Öffnet eine eigene Zeitachse; Dispose übernimmt deren Endzeit in die übergeordnete Achse.</summary>
    IDisposable BeginBranch(DateTimeOffset start);
    /// <summary>Rückt die aktuelle Achse mindestens bis zur Verfügbarkeit einer Ressource vor.</summary>
    void Synchronize(DateTimeOffset time);
}

/// <summary>Echte Systemzeit.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.Now;

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        => duration <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(duration, cancellationToken);
}

/// <summary>
/// Deterministische, virtuelle Uhr.
/// <para><c>autoAdvance = true</c>: jedes <see cref="Delay"/> rückt die jeweilige Zeitachse sofort vor; parallele Zweige werden über ihre späteste Endzeit zusammengeführt.</para>
/// <para><c>autoAdvance = false</c>: Wartende werden erst durch <see cref="Advance"/> freigegeben (Parallelbetrieb, Timeouts).</para>
/// </summary>
public sealed class VirtualClock(DateTimeOffset start, bool autoAdvance) : IParallelClock
{
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Tcs)> _timers = [];
    private DateTimeOffset _now = start;
    private readonly AsyncLocal<BranchTime?> _branch = new();
    public bool AdvancesBranches => autoAdvance && _branch.Value is not null;

    public VirtualClock(bool autoAdvance = true)
        : this(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(2)), autoAdvance)
    {
    }

    public DateTimeOffset Now
    {
        get { lock (_gate) return _branch.Value?.Time ?? _now; }
    }

    /// <summary>Anzahl aktuell wartender Timer (für Tests).</summary>
    public int PendingTimers
    {
        get { lock (_gate) return _timers.Count; }
    }

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero) return Task.CompletedTask;

        if (autoAdvance)
        {
            if (_branch.Value is not null) Synchronize(Now + duration); else Advance(duration);
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _timers.Add((_now + duration, tcs));
        if (cancellationToken.CanBeCanceled)
        {
            var reg = cancellationToken.Register(() =>
            {
                lock (_gate) _timers.RemoveAll(t => t.Tcs == tcs);
                tcs.TrySetCanceled(cancellationToken);
            });
            tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        }
        return tcs.Task;
    }

    public IDisposable BeginBranch(DateTimeOffset started)
    {
        if (!autoAdvance) return new BranchLease(() => { });
        var parent = _branch.Value;
        var child = new BranchTime(started);
        _branch.Value = child;
        return new BranchLease(() =>
        {
            lock (_gate)
            {
                if (parent is not null && child.Time > parent.Time) parent.Time = child.Time;
                if (child.Time > _now) _now = child.Time;
            }
            _branch.Value = parent;
        });
    }

    public void Synchronize(DateTimeOffset time)
    {
        if (!autoAdvance) return;
        lock (_gate)
        {
            if (_branch.Value is { } branch && time > branch.Time) branch.Time = time;
            if (time > _now) _now = time;
        }
    }

    private sealed class BranchTime(DateTimeOffset time) { public DateTimeOffset Time = time; }
    private sealed class BranchLease(Action end) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) end(); }
    }

    /// <summary>Zeit vorrücken und fällige Timer auslösen.</summary>
    public void Advance(TimeSpan duration)
    {
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _now += duration;
            due = _timers.Where(t => t.Due <= _now).Select(t => t.Tcs).ToList();
            _timers.RemoveAll(t => t.Due <= _now);
        }
        foreach (var tcs in due) tcs.TrySetResult();
    }
}
