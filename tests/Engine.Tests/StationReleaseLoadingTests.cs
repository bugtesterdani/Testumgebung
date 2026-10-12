using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class StationReleaseLoadingTests
{
    private static Task<(TestProject Project, ReleaseInfo? Release)> Load(StationViewModel station, AvailableTest test)
        => (Task<(TestProject, ReleaseInfo?)>)typeof(StationViewModel).GetMethod("LoadTestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(station, [test])!;

    [Fact]
    public async Task Station_loads_current_and_archived_releases_and_rejects_missing_or_changed_packages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "station-releases-" + Guid.NewGuid());
        var project = SampleProjects.AcPruefung();
        var service = new ReleaseService(new FolderReleaseStore(directory));
        var actor = new Actor("tester", UserRights.Develop | UserRights.Release);
        try
        {
            foreach (var version in new[] { "1", "2" })
            {
                var draft = await service.CreateDraftAsync(project, version, actor);
                Assert.All(await service.ReviewAsync(draft, actor), check => Assert.True(check.Passed, check.Detail));
                await service.ReleaseAsync((await service.ListAsync()).Single(r => r.ReleaseId == draft.ReleaseId), actor, true);
            }
            await service.CreateDraftAsync(project, "3", actor);
            await using var hub = new ReleaseHub(service); await hub.RefreshAsync();
            var station = new StationViewModel("station", () => project, new ResourceManager(), releases: hub);
            Assert.Equal(new[] { "2", "1" }, station.AvailablePrograms.Select(p => p.Version));
            foreach (var selected in new[] { new AvailableTest(project.Definition.Name, "2", true, ""), new(project.Definition.Name, "2", false, ""), new(project.Definition.Name, "1", false, ""), new(project.Definition.Name, "1", true, "") })
            {
                var loaded = await Load(station, selected);
                Assert.Equal(selected.Version, loaded.Project.Definition.Version); Assert.Equal(selected.Version, loaded.Release!.Version);
                loaded.Project.Definition.Version = "modified copy";
                Assert.Equal(selected.Version, (await Load(station, selected)).Project.Definition.Version);
            }
            foreach (var selected in new[] { new AvailableTest(project.Definition.Name, "3", false, ""), new("unknown", "2", true, ""), new(project.Definition.Name, "missing", true, "") })
                Assert.Contains("nicht (mehr) freigegeben", (await Assert.ThrowsAsync<InvalidOperationException>(() => Load(station, selected))).Message);
            var archived = hub.All.Single(r => r.Version == "1");
            var replacement = ReleasePackage.Build(project, "changed", DateTimeOffset.UtcNow.AddDays(1)).Bytes;
            await File.WriteAllBytesAsync(Path.Combine(directory, archived.ReleaseId + ".tpkg"), replacement);
            Assert.Contains("Paket beschädigt", (await Assert.ThrowsAsync<InvalidOperationException>(() => Load(station, new(project.Definition.Name, "1", false, "")))).Message);
            station.UseDraft = true;
            Assert.Equal("Entwurf", Assert.Single(station.AvailablePrograms).Origin);
            var editorCopy = await Load(station, new("ignored", "ignored", false, ""));
            Assert.Null(editorCopy.Release); Assert.NotSame(project, editorCopy.Project);
            Assert.Equal(project.Definition.Version, editorCopy.Project.Definition.Version);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Examples_and_editor_fallback_return_independent_project_copies()
    {
        var project = SampleProjects.AcPruefung();
        var plain = new StationViewModel("station", () => project, new ResourceManager());
        Assert.Equal("Editor", Assert.Single(plain.AvailablePrograms).Origin);
        var loaded = await Load(plain, plain.AvailablePrograms[0]); Assert.Null(loaded.Release); Assert.NotSame(project, loaded.Project);
        var setup = StationSetup.Default(project); setup.ExampleProjects = [project];
        var examples = new StationViewModel("station", () => project, new ResourceManager(), coordinator: new StationCoordinator(setup, new ResourceManager()));
        Assert.Equal("Simulation / Beispiel", Assert.Single(examples.AvailablePrograms).Origin);
        loaded = await Load(examples, examples.AvailablePrograms[0]); Assert.Null(loaded.Release); Assert.NotSame(project, loaded.Project);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Load(examples, new("missing", "missing", false, "")));
    }
}

