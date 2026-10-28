using Testumgebung.Contracts;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ReleasePageWorkflowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Release_page_obeys_roles_and_requires_review_and_confirmation(bool develop, bool release)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-page-" + Guid.NewGuid());
        var project = SampleProjects.AcPruefung();
        await using var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(directory)));
        var config = new AccessConfig { Users = [new RightsEntry { Upn = "tester@simuliert.local", Develop = develop, Release = release }] };
        var identity = IdentityServices.Simulated(() => "Tester", config); var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, releases: hub, identity: identity, clock: new VirtualClock());
        var page = shell.ReleasesPage!;
        try
        {
            Assert.Equal("Nicht angemeldet", page.UserName); Assert.Equal("", page.IdentitySource);
            Assert.False(page.CanConfirm); Assert.False(page.ReviewCommand.CanExecute(null)); Assert.False(await page.ReviewAsync()); Assert.False(await page.ReleaseAsync());
            page.SelectedProgramName = ""; page.SelectedProgramName = page.ProjectName;
            await shell.LoginAsync(false); ui.Pump();
            Assert.Equal(develop, page.CreateDraftCommand.CanExecute(null)); Assert.Equal(develop, page.Rights[0].Granted); Assert.Equal(release, page.Rights[1].Granted);
            Assert.False(page.IsConfirmed); Assert.True(page.IsDevelopmentMode);
            var actor = new Actor("fixture", UserRights.Develop);
            var draft = await hub.Service.CreateDraftAsync(project, "1", actor); await page.RefreshAsync(); ui.Pump();
            page.Selected = page.Releases.Single(); await ui.Until(() => page.Package.Count > 0, "Paket-Inhalt");
            Assert.Contains("v1", page.SelectedTitle); Assert.Equal(3, page.Stages.Count); Assert.True(page.Stages[0].Current);
            Assert.All(page.Package, row => Assert.True(row.Ok)); Assert.Equal(develop, page.ReviewCommand.CanExecute(null));
            var reviewed = await page.ReviewAsync(); ui.Pump(); Assert.Equal(develop, reviewed); Assert.Equal(!develop, page.StatusIsError);
            if (develop)
            {
                Assert.Equal(ReleaseState.Review, page.Selected!.State); Assert.True(page.Stages[1].Current);
                Assert.Equal(release, page.CanConfirm); Assert.False(page.ReleaseCommand.CanExecute(null));
                Assert.False(await page.ReleaseAsync()); ui.Pump(); Assert.True(page.StatusIsError);
                page.Confirmed = true; page.Confirmed = true; Assert.Equal(release, page.ReleaseCommand.CanExecute(null));
                Assert.Equal(release, await page.ReleaseAsync()); ui.Pump();
                if (release)
                {
                    Assert.Equal(ReleaseState.Released, page.Selected!.State); Assert.True(page.Stages[2].Current);
                    Assert.Contains("Freigegeben", page.ProgramStatus); Assert.Contains("startet mit v1", page.StationVersions[0].Text);
                }
            }
            page.NewVersion = " 1 "; Assert.Null(await page.CreateDraftAsync()); ui.Pump(); Assert.True(page.StatusIsError);
            page.NewVersion = " 2 "; var nextTask = page.CreateDraftAsync(); Assert.Null(await page.CreateDraftAsync());
            var next = await nextTask; ui.Pump(); Assert.Equal(develop, next is not null);
            if (next is not null)
            {
                Assert.Equal("2", next.Version); Assert.Equal("", page.NewVersion); Assert.False(page.Confirmed);
                Assert.Equal("2", project.Definition.Version);
                Assert.True(await page.ReviewAsync()); ui.Pump();
                page.Confirmed = true;
                if (release)
                {
                    Assert.True(await page.ReleaseAsync()); ui.Pump();
                    page.Selected = page.Releases.Single(r => r.Version == "1");
                    Assert.Equal(ReleaseState.Archived, page.Selected.State); Assert.Equal("Archiviert", page.Stages[2].Title);
                    Assert.False(page.CanConfirm); Assert.False(page.ReleaseCommand.CanExecute(null));
                }
                page.NewVersion = ""; var suggested = await page.CreateDraftAsync(); ui.Pump(); Assert.NotNull(suggested); Assert.Equal("2.1", suggested!.Version);
            }
            page.SelectedProgramName = "not in editor"; ui.Pump();
            Assert.Equal("Kein Release vorhanden", page.ProgramStatus); Assert.Empty(page.Releases); Assert.Null(page.Selected);
            Assert.Null(await page.CreateDraftAsync()); ui.Pump(); Assert.Contains("zuerst im Editor öffnen", page.Status);
            page.SelectedProgramName = project.Definition.Name; ui.Pump();
            await page.LoadAuditAsync(); ui.Pump(); Assert.Contains("Hash-Kette geprüft", page.AuditState); Assert.NotEmpty(page.Audit);
            await shell.LogoutAsync(); ui.Pump(); Assert.False(page.CreateDraftCommand.CanExecute(null)); Assert.Equal("Nicht angemeldet", page.UserName);
        }
        finally
        {
            // UI updates can still have an audit read in flight after logout.
            // Wait for actual file release instead of relying on runner timing.
            await ui.Until(() => TryDeleteTestDirectory(directory), "Freigabe der temporären Release-Ablage");
        }
    }

    [Fact]
    public void Cleanup_waits_for_a_real_audit_lock_and_handles_removed_directories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-page-cleanup-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using (var held = new FileStream(Path.Combine(directory, "audit.jsonl"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(TryDeleteTestDirectory(directory));
                Assert.True(Directory.Exists(directory));
            }
            Assert.True(TryDeleteTestDirectory(directory));
            Assert.False(Directory.Exists(directory));
            Assert.True(TryDeleteTestDirectory(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static bool TryDeleteTestDirectory(string directory)
    {
        try { Directory.Delete(directory, true); return true; }
        catch (DirectoryNotFoundException) { return true; }
        // Retry only Windows sharing/lock violations. Other IO failures stay visible.
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
    }

}

