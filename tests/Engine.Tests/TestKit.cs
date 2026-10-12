using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;

namespace Testumgebung.Engine.Tests;

/// <summary>Hilfsmittel für deterministische Engine-Tests.</summary>
internal static class Kit
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static GroupDefinition Group(string id, string? name = null) => new(id, name ?? id);

    /// <summary>Ablauf mit leerem Init, gegebenem Loop-Inhalt (einmalig) und leerer Finalization.</summary>
    public static TestDefinition Test(GroupDefinition loop, GroupDefinition? init = null, GroupDefinition? finalization = null)
        => new("Beispiel", "1.0", init ?? Group("init", "Test Start / Init"), loop, finalization ?? Group("fin", "Test End / Finalization"));

    public static async Task<T> Within<T>(this Task<T> task) => await task.WaitAsync(Timeout);

    public static async Task Within(this Task task) => await task.WaitAsync(Timeout);

    /// <summary>Wartet auf das erste (auch bereits vergangene) Ereignis, das das Prädikat erfüllt.</summary>
    public static Task<T> WaitFor<T>(this TestRun run, Func<T, bool>? predicate = null) where T : EngineEvent
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(EngineEvent e)
        {
            if (e is T t && (predicate?.Invoke(t) ?? true)) tcs.TrySetResult(t);
        }
        run.EventRaised += Handler;
        var existing = run.Events.OfType<T>().FirstOrDefault(e => predicate?.Invoke(e) ?? true);
        if (existing is not null) tcs.TrySetResult(existing);
        tcs.Task.ContinueWith(_ => run.EventRaised -= Handler, TaskScheduler.Default);
        return tcs.Task.WaitAsync(Timeout);
    }

    public static IEnumerable<StepCompleted> Steps(this TestRun run, string nodeId)
        => run.Events.OfType<StepCompleted>().Where(e => e.NodeId == nodeId);

    public static GroupCompleted GroupResult(this TestRun run, string nodeId)
        => run.Events.OfType<GroupCompleted>().Last(e => e.NodeId == nodeId);
}

/// <summary>Protokolliert die Reihenfolge ausgeführter Testschritte und stellt steuerbare „Gates“ bereit.</summary>
internal sealed class Probe
{
    private readonly ConcurrentQueue<string> _marks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _reached = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _released = new();

    public IReadOnlyList<string> Marks => _marks.ToList();

    public void Record(string mark) => _marks.Enqueue(mark);

    /// <summary>Unbewerteter Schritt, der nur seine Marke protokolliert.</summary>
    public StepDefinition Mark(string id, string? mark = null, ResultRating rating = ResultRating.NResult)
        => Steps.Code(id, id, _ =>
        {
            _marks.Enqueue(mark ?? id);
            return new StepOutcome(rating);
        });

    /// <summary>Schritt, der bis zur Freigabe (oder bis zum Abbruch) blockiert.</summary>
    public StepDefinition Gate(string id, ResultRating rating = ResultRating.NResult)
        => Steps.Code(id, id, async ctx =>
        {
            var count = _marks.Count(m => m == id || m.StartsWith(id + "#", StringComparison.Ordinal));
            _marks.Enqueue(count == 0 ? id : $"{id}#{count + 1}");
            Reached(id).TrySetResult();
            await Released(id).Task.WaitAsync(ctx.CancellationToken);
            return new StepOutcome(rating);
        });

    public Task WhenReached(string id) => Reached(id).Task.WaitAsync(Kit.Timeout);

    public void Release(string id) => Released(id).TrySetResult();

    /// <summary>Setzt ein Gate zurück, damit es erneut blockiert.</summary>
    public void Reset(string id)
    {
        _reached.TryRemove(id, out _);
        _released.TryRemove(id, out _);
    }

    private TaskCompletionSource Reached(string id) => _reached.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource Released(string id) => _released.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}
