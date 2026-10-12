using Testumgebung.Contracts;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ReleaseComparisonBoundaryTests
{
    [Fact]
    public async Task Compare_editor_versions_missing_target_and_missing_package()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(directory)));
        var ui = new QueueDispatcher(); var project = SampleProjects.AcPruefung();
        var shell = new ShellViewModel(ui, project: project, releases: hub);
        try
        {
            var page = shell.ReleasesPage!;
            page.Selected = null; page.CompareWith = null;
            await page.CompareAsync(); Assert.Empty(page.Differences); Assert.Contains("keine Unterschiede", page.CompareStatus);
            page.CompareWith = "missing"; await page.CompareAsync(); Assert.Contains("nicht gefunden", page.CompareStatus);
            var actor = new Actor("test", UserRights.Develop);
            var first = await hub.Service.CreateDraftAsync(project, "1", actor);
            var changed = new ProjectSerializer().Clone(project); changed.Description = "new";
            var second = await hub.Service.CreateDraftAsync(changed, "2", actor);
            await hub.RefreshAsync(); ui.Pump();
            await ui.Until(() => page.Package.Count > 0, "Paket geladen");
            page.Selected = page.Releases.First(r => r.ReleaseId == second.ReleaseId);
            page.CompareWith = page.CompareTargets.First(t => t.StartsWith("v1 ("));
            await page.CompareAsync(); Assert.NotEmpty(page.Differences); Assert.Contains("Änderung(en)", page.CompareStatus);
            page.Selected = null; page.CompareWith = ReleasesViewModel.EditorTarget;
            await page.CompareAsync(); Assert.Empty(page.Differences);
            page.CompareWith = page.CompareTargets.First(t => t.StartsWith("v2 ("));
            await page.CompareAsync(); Assert.NotEmpty(page.Differences);
            File.Delete(Path.Combine(directory, first.ReleaseId + ".tpkg"));
            page.CompareWith = page.CompareTargets.First(t => t.StartsWith("v1 ("));
            await page.CompareAsync(); Assert.Empty(page.Differences); Assert.Contains("Vergleich nicht möglich", page.CompareStatus);
        }
        finally { await hub.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
