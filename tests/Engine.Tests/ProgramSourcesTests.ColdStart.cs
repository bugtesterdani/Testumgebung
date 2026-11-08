using Testumgebung.Contracts;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed partial class ProgramSourcesTests
{
    [Fact]
    public async Task Scan_waits_for_release_data_and_ignores_duplicate_triggers()
    {
        var (service, _, _, source) = await Context();
        var release = await Publish(service, source, "1");
        var delayed = new DelayedReleaseStore(service.Store);
        await using var hub = new ReleaseHub(new ReleaseService(delayed) { ProgramCatalog = service.ProgramCatalog });
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project, 1);
        setup.Stations[0].Selection = new SelectionConfig { Rules = [new SelectionRule { Pattern = "*", Test = source.Name }] };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, releases: hub,
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        station.Selection.ScanText = "FA-42";
        var scan = station.Selection.ProcessScanAsync();
        try
        {
            await delayed.Entered.Task.Within();
            Assert.False(station.Selection.CanStart);
            Assert.Null(station.Run);
            await station.Selection.ProcessScanAsync();
            Assert.Equal(1, delayed.ListCalls);
            delayed.Resume.TrySetResult();
            await ui.Until(() => station.ActiveContent is { IsAwaiting: true }, "delayed releases");
            Assert.Equal(release.ReleaseId, station.ActiveRelease!.ReleaseId);
            Assert.Equal("FA-42", station.CurrentOrder);
            var run = station.Run;
            await station.Selection.ProcessScanAsync();
            Assert.Same(run, station.Run);
        }
        finally
        {
            delayed.Resume.TrySetResult();
            station.RequestAbort();
            await scan.Within();
            await station.WaitForCompletionAsync().Within();
            ui.Pump();
        }
    }

    [Fact]
    public async Task Cold_scan_never_starts_an_unreleased_program()
    {
        var (service, _, _, source) = await Context();
        await service.CreateRemoteDraftAsync(source, "", "1", Both);
        await using var hub = new ReleaseHub(service);
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project, 1);
        setup.Stations[0].Selection = new SelectionConfig { Rules = [new SelectionRule { Pattern = "*", Test = source.Name }] };
        var shell = new ShellViewModel(project: project, releases: hub,
            stations: new StationCoordinator(setup, new ResourceManager()));
        shell.SelectedStation.Selection.ScanText = "FA-42";
        await shell.SelectedStation.Selection.ProcessScanAsync();
        Assert.False(shell.SelectedStation.Selection.CanStart);
        Assert.True(shell.SelectedStation.Selection.HasProblem);
        Assert.Null(shell.SelectedStation.Run);
        Assert.False(shell.SelectedStation.UseDraft);
    }

    private sealed class DelayedReleaseStore(IReleaseStore inner) : IReleaseStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListCalls { get; private set; }
        public string Name => inner.Name;
        public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await inner.ListAsync(cancellationToken);
        }
        public Task<byte[]> GetPackageAsync(string releaseId, CancellationToken cancellationToken = default) => inner.GetPackageAsync(releaseId, cancellationToken);
        public Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken cancellationToken = default) => inner.CreateAsync(info, package, audit, cancellationToken);
        public Task<bool> TransitionAsync(ReleaseTransition transition, AuditDraft audit, CancellationToken cancellationToken = default) => inner.TransitionAsync(transition, audit, cancellationToken);
        public Task AppendAuditAsync(AuditDraft audit, CancellationToken cancellationToken = default) => inner.AppendAuditAsync(audit, cancellationToken);
        public Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken cancellationToken = default) => inner.ReadAuditAsync(cancellationToken);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task First_scan_loads_releases_without_login_and_preserves_the_configured_start_mode(bool orderSession, bool loginCycle)
    {
        var (service, _, _, source) = await Context();
        var release = await Publish(service, source, "1");
        await using var hub = new ReleaseHub(service);
        var project = SampleProjects.AcPruefung();
        var setup = StationSetup.Default(project, 1);
        setup.Stations[0].Selection = new SelectionConfig
        {
            OrderSession = orderSession,
            UseManual = false,
            Rules = [new SelectionRule { Pattern = "*", Test = source.Name }],
        };
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, releases: hub,
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;
        Assert.False(shell.Session.IsSignedIn);
        Assert.False(station.UseDraft);
        Assert.Empty(hub.All); // No startup refresh has completed yet.
        if (loginCycle)
        {
            Assert.True(await shell.Session.SignInAsync());
            await shell.LogoutAsync();
            ui.Pump();
            Assert.False(station.UseDraft);
        }

        station.Selection.ScanText = "SN-42";
        var scan = station.Selection.ProcessScanAsync();
        try
        {
            if (orderSession)
            {
                await ui.Until(() => station.ActiveContent is { IsAwaiting: true }, "cold-start order session");
                Assert.True(station.IsRunning);
                Assert.Equal(release.ReleaseId, station.ActiveRelease!.ReleaseId);
                Assert.Equal("SN-42", station.CurrentOrder);
                // A repeated Enter during the run must not start another session.
                var run = station.Run;
                await station.Selection.ProcessScanAsync();
                Assert.Same(run, station.Run);
            }
            else
            {
                await scan;
                ui.Pump();
                Assert.Equal("SN-42", station.Selection.ScanText);
                Assert.True(station.Selection.CanStart, station.Selection.ResultText);
                Assert.Null(station.Run); // Single-DUT mode still requires explicit Start.
            }
            Assert.False(shell.Session.IsSignedIn);
            Assert.False(station.UseDraft);
            Assert.Contains(station.AvailablePrograms, p => p.Name == source.Name && p.Version == release.Version);
        }
        finally
        {
            station.RequestAbort();
            await scan.Within();
            await station.WaitForCompletionAsync().Within();
            ui.Pump();
            if (loginCycle) await WaitForLoginAuditAsync(service.Store);
        }
    }

    private static async Task WaitForLoginAuditAsync(IReleaseStore store)
    {
        // The shell writes audit events in the background. Wait for both writes before deleting the fixture directory.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var audit = await store.ReadAuditAsync(timeout.Token);
            if (audit.Any(entry => entry.Action == "Anmeldung") &&
                audit.Any(entry => entry.Action == "Abmeldung"))
                return;
            await Task.Delay(10, timeout.Token);
        }
    }
}
