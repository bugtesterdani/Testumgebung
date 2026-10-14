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
/// <para><c>autoAdvance = true</c>: jedes <see cref="Delay"/> rückt die Zeit sofort vor (ein einzelner Lauf).</para>
/// <para><c>autoAdvance = false</c>: Wartende werden erst durch <see cref="Advance"/> freigegeben (Parallelbetrieb, Timeouts).</para>
/// </summary>
public sealed class VirtualClock(DateTimeOffset start, bool autoAdvance) : IClock
{
    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Tcs)> _timers = [];
    private DateTimeOffset _now = start;

    public VirtualClock(bool autoAdvance = true)
        : this(new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(2)), autoAdvance)
    {
    }

    public DateTimeOffset Now
    {
        get { lock (_gate) return _now; }
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
            Advance(duration);
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
