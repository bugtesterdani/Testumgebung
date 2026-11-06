using Testumgebung.Contracts;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ReleaseHubRefreshTests
{
    private sealed class DelayedSnapshotStore(IReleaseStore inner) : IReleaseStore
    {
        private int _reads;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => inner.Name;
        public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken token = default)
        {
            var read = Interlocked.Increment(ref _reads);
            if (read > 1) SecondRead.TrySetResult();
            var snapshot = await inner.ListAsync(token);
            if (read == 1)
            {
                FirstRead.TrySetResult(); await ContinueFirst.Task.WaitAsync(token);
            }
            return snapshot;
        }
        public Task<byte[]> GetPackageAsync(string id, CancellationToken token = default) => inner.GetPackageAsync(id, token);
        public Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken token = default) => inner.CreateAsync(info, package, audit, token);
        public Task<bool> TransitionAsync(ReleaseTransition transition, AuditDraft audit, CancellationToken token = default) => inner.TransitionAsync(transition, audit, token);
        public Task AppendAuditAsync(AuditDraft audit, CancellationToken token = default) => inner.AppendAuditAsync(audit, token);
        public Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken token = default) => inner.ReadAuditAsync(token);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task A_slow_old_snapshot_cannot_remove_a_newly_released_program_and_cancellation_does_not_block_refresh(bool cancelFirst)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-refresh-" + Guid.NewGuid().ToString("N"));
        var original = new FolderReleaseStore(directory); var service = new ReleaseService(original);
        var project = SampleProjects.AcPruefung(); var actor = new Actor("fixture", UserRights.Develop | UserRights.Release);
        var draft = await service.CreateDraftAsync(project, "0.29", actor);
        var delayed = new DelayedSnapshotStore(original);
        await using var hub = new ReleaseHub(new ReleaseService(delayed));
        using var stop = new CancellationTokenSource();
        var old = hub.RefreshAsync(stop.Token); await delayed.FirstRead.Task.Within();
        try
        {
            Assert.All(await service.ReviewAsync(draft, actor), check => Assert.True(check.Passed, check.Detail));
            await service.ReleaseAsync((await service.ListAsync()).Single(), actor, true);
            var latest = hub.RefreshAsync();
            // Without serialization, force the newer read to finish first; the delayed draft then overwrites it.
            if (delayed.SecondRead.Task.IsCompleted) await latest.Within();
            if (cancelFirst) stop.Cancel(); else delayed.ContinueFirst.TrySetResult();
            if (cancelFirst) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old); else await old.Within();
            await latest.Within();
            Assert.Equal(ReleaseState.Released, Assert.Single(hub.All).State);
            Assert.NotNull(hub.GetReleased(project.Definition.Name));
            var station = new StationViewModel("fixture", () => project, new ResourceManager(), releases: hub);
            Assert.False(station.UseDraft); Assert.Equal("0.29", Assert.Single(station.AvailablePrograms).Version);
        }
        finally { delayed.ContinueFirst.TrySetResult(); }
    }
}
