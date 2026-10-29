using Testumgebung.Contracts;
using Testumgebung.Hardware;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ParallelResourceTests
{
    private static ResourceRequest Request(string branch, params string[] keys)
        => new("run", branch, keys, "Station", branch) { Branch = branch };

    [Fact]
    public async Task Parents_are_reentrant_but_siblings_wait_and_external_runs_stay_excluded()
    {
        var manager = new ResourceManager();
        using var parent = await manager.AcquireAsync(Request("", "R"), null, default);
        var first = await manager.AcquireAsync(Request("a", "R"), null, default);
        var sibling = manager.AcquireAsync(Request("b", "R"), null, default);
        var external = manager.AcquireAsync(new("other", "scope", ["R"]), null, default);
        Assert.False(sibling.IsCompleted);
        using (var nested = await manager.AcquireAsync(Request("a/child", "R"), null, default).Within())
            Assert.Equal(3, manager.Snapshot().Single().ReferenceCount);
        first.Dispose();
        using (await sibling.Within()) Assert.False(external.IsCompleted);
        Assert.False(external.IsCompleted);
        parent.Dispose();
        using (await external.Within()) Assert.True(manager.IsHeldBy("R", "other"));
        Assert.Empty(manager.Snapshot());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a/child")]
    public async Task Cycles_between_branches_are_rejected_even_when_parent_waits_for_nested_child(string waitingBranch)
    {
        var manager = new ResourceManager();
        using var parent = await manager.AcquireAsync(Request("", "R", "S"), null, default);
        using var a = await manager.AcquireAsync(Request("a", "R"), null, default);
        var b = await manager.AcquireAsync(Request("b", "S"), null, default);
        var waiting = manager.AcquireAsync(Request(waitingBranch, "S"), null, default);
        Assert.False(waiting.IsCompleted);
        await Assert.ThrowsAsync<ResourceDeadlockException>(() => manager.AcquireAsync(Request("b", "R"), null, default));
        b.Dispose();
        using (await waiting.Within()) Assert.True(manager.IsHeldBy("S", "run"));
    }

    [Fact]
    public async Task Cancelled_sibling_leaves_no_waiter_or_lease()
    {
        var manager = new ResourceManager();
        var first = await manager.AcquireAsync(Request("a", "R"), null, default);
        using var cancel = new CancellationTokenSource();
        var waiter = manager.AcquireAsync(Request("b", "R"), null, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Empty(manager.Snapshot().Single().WaitingRuns);
        first.Dispose();
        using (await manager.AcquireAsync(Request("c", "R"), null, default).Within()) Assert.True(manager.IsHeldBy("R", "run"));
        Assert.Empty(manager.Snapshot());
    }
}
