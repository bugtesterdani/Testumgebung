using System.IO.Compression;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Domain.Definitions;
using Testumgebung.Hardware;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Programs;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed partial class ProgramSourcesTests
{
    private sealed class FixtureProvider(string directory) : IProgramSourceProvider
    {
        public string Id => "Fixture";
        public string Content { get; set; } = "original";
        public string Identity { get; set; } = "repository-uuid";
        public bool Available { get; set; } = true;
        public string ProgramName { get; set; } = "AC-Prüfung";
        public int Calls { get; private set; }
        public Task<SourceSnapshot> ExportAsync(ProgramSource source, string? revision, CancellationToken ct = default)
        {
            Calls++; if (!Available) throw new IOException("Remote unavailable");
            var folder = Path.Combine(directory, Guid.NewGuid().ToString("N")); var export = Path.Combine(folder, "export"); Directory.CreateDirectory(export);
            var serializer = new ProjectSerializer(); var json = System.Text.Json.Nodes.JsonNode.Parse(serializer.Serialize(SampleProjects.AcPruefung()))!; json["name"] = ProgramName; var project = serializer.Deserialize(json.ToJsonString());
            var file = Path.Combine(export, source.ProjectPath); Directory.CreateDirectory(Path.GetDirectoryName(file)!); new ProjectSerializer().Save(project, file);
            File.WriteAllText(Path.Combine(export, "helper.py"), Content);
            return Task.FromResult(new SourceSnapshot(new(source, string.IsNullOrEmpty(revision) ? "1" : revision, Identity, ReleasePackage.Sha256(File.ReadAllBytes(file))), file, folder));
        }
    }
    private async Task<(ReleaseService Service, FolderProgramCatalogStore Store, FixtureProvider Provider, ProgramSource Source)> Context()
    {
        var store = new FolderProgramCatalogStore(P("catalog")); await store.UpgradeAsync(); var provider = new FixtureProvider(_root);
        var source = Source() with { Provider = provider.Id };
        var service = new ReleaseService(new FolderReleaseStore(P("releases"))) { ProgramCatalog = store, SourceProviders = new([provider]) };
        await store.SaveAsync(ProgramCatalog.Empty with { Sources = [source] }); return (service, store, provider, source);
    }
    private static async Task<ReleaseInfo> Publish(ReleaseService service, ProgramSource source, string version)
    {
        var draft = await service.CreateRemoteDraftAsync(source, "", version, Both);
        Assert.All(await service.ReviewAsync(draft, Both), c => Assert.True(c.Passed, c.Detail));
        var reviewed = (await service.ListAsync()).Single(r => r.ReleaseId == draft.ReleaseId);
        return await service.ReleaseAsync(reviewed, Both, true);
    }

    [Fact]
    public async Task Remote_release_requires_same_remote_identity_and_content_and_blocks_local_drafts()
    {
        var (service, store, provider, source) = await Context();
        var local = await service.CreateDraftAsync(SampleProjects.AcPruefung(), "local", Both);
        await service.ReviewAsync(local, Both);
        var localReviewed = (await service.ListAsync()).Single(r => r.ReleaseId == local.ReleaseId);
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(localReviewed, Both, true));
        var draft = await service.CreateRemoteDraftAsync(source, "", "1", Both); var opened = await service.OpenAsync(draft);
        Assert.True(opened.IsIntact); Assert.Equal(draft.Source, opened.Manifest.Source); Assert.Contains(opened.Manifest.Entries, e => e.Name == "source.zip");
        Assert.Equal(draft.Source, ReleasePackage.ReadSource(await service.Store.GetPackageAsync(draft.ReleaseId)));
        await service.ReviewAsync(draft, Both); var reviewed = (await service.ListAsync()).Single(r => r.ReleaseId == draft.ReleaseId);
        provider.Available = false; await Assert.ThrowsAsync<IOException>(() => service.ReleaseAsync(reviewed, Both, true));
        provider.Available = true; provider.Identity = "different"; await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(reviewed, Both, true));
        provider.Identity = "repository-uuid"; provider.Content = "changed"; await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(reviewed, Both, true));
        provider.Content = "original"; var released = await service.ReleaseAsync(reviewed, Both, true); Assert.Equal(ReleaseState.Released, released.State);
        provider.ProgramName = "Different program"; await Assert.ThrowsAsync<ReleaseRuleException>(() => service.CreateRemoteDraftAsync(source, "", "2", Both));
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.CreateRemoteDraftAsync(source, "", "3", new("worker", UserRights.None)));
        var bytes = await service.Store.GetPackageAsync(draft.ReleaseId);
        using var stream = new MemoryStream(); stream.Write(bytes); stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true)) zip.GetEntry("source.zip")!.Delete();
        Assert.False(ReleasePackage.Open(stream.ToArray()).IsIntact);
        Assert.Null(ReleasePackage.ReadSource([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => ReleasePackage.Build(opened.Project, "tester", DateTimeOffset.Now, source: draft.Source));
    }

    [SqliteFact]
    public async Task Database_persists_remote_provenance_in_immutable_package_and_list_metadata()
    {
        var (original, _, provider, source) = await Context();
        var db = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("source-release.db")}", new SqliteDialect());
        var service = new ReleaseService(new SqlReleaseStore(db)) { SourceProviders = new([provider]) };
        var released = await Publish(service, source, "remote");
        Assert.Equal(released.Source, Assert.Single(await service.ListAsync()).Source);
        Assert.Equal(released.Source, (await service.OpenAsync(released)).Manifest.Source);
    }

    [Fact]
    public async Task Catalog_workspace_edits_locally_assigns_pins_and_loads_default_without_station_approval()
    {
        var (service, store, provider, source) = await Context();
        var first = await Publish(service, source, "1"); var second = await Publish(service, source, "2");
        var project = SampleProjects.AcPruefung(); var editor = new EditorViewModel(project);
        var session = new SessionService(new SimulatedIdentityProvider(() => "tester"), null, allowDevelopmentMode: true); Assert.True(await session.SignInAsync());
        await using var hub = new ReleaseHub(service); await hub.RefreshAsync();
        var stations = new[] { new StationViewModel("A", () => editor.Project, new ResourceManager(), releases: hub), new StationViewModel("B", () => editor.Project, new ResourceManager(), releases: hub) };
        var vm = new ProgramSourcesViewModel(hub, editor, session, ImmediateDispatcher.Instance, stations, P("clients.json"));
        // The application normally creates standard clients; this fixture deliberately plugs a third provider in afterward.
        service.SourceProviders = new([provider]);
        await vm.RefreshAsync(); Assert.Single(vm.Sources); Assert.Contains("bereit", vm.SchemaText);
        vm.StationName = "A"; vm.AutoUpdate = false; vm.SelectedVersion = first; await vm.SaveAssignmentAsync();
        Assert.Equal(first.ReleaseId, hub.ResolveStation(stations[0].ProgramStationId, "")!.Release.ReleaseId); Assert.Contains("veraltet", hub.ResolveStation(stations[0].ProgramStationId, "")!.Warning);
        Assert.Single(stations[0].AvailablePrograms); Assert.Equal("1", stations[0].AvailablePrograms[0].Version);
        vm.SelectedVersion = first; await vm.SetDefaultAsync(); Assert.Equal(second.ReleaseId, hub.ResolveStation(stations[1].ProgramStationId, "")!.Release.ReleaseId);
        Assert.Contains("festgesetzt", vm.AssignmentText); Assert.Contains("v1", vm.DefaultText);
        var local = P("local.tproj.json"); new ProjectSerializer().Save(project, local); vm.LocalProject = local; vm.OpenLocalCommand.Execute(null);
        Assert.True(stations[0].UseDraft); Assert.False(stations[1].UseDraft); Assert.Equal(local, editor.FilePath);
        Assert.Equal(first.ReleaseId, hub.ResolveStation(stations[0].ProgramStationId, "")!.Release.ReleaseId);
        vm.LocalProject = P("missing.json"); vm.OpenLocalCommand.Execute(null); Assert.NotNull(vm.Status);
        await vm.ClearAssignmentAsync(); Assert.Equal(second.ReleaseId, hub.ResolveStation(stations[0].ProgramStationId, "")!.Release.ReleaseId);
        vm.Selected = null; vm.SelectedVersion = null; await vm.SaveAssignmentAsync(); Assert.Contains("Quelle", vm.Status);
        await vm.SetDefaultAsync(); Assert.Contains("auswählen", vm.Status);
        vm.NewSourceCommand.Execute(null); vm.Name = ""; await vm.SaveSourceAsync(); Assert.NotNull(vm.Status);
        vm.Name = source.Name; vm.Provider = source.Provider; vm.Repository = source.Repository; vm.Line = "branch"; vm.ProjectPath = source.ProjectPath; await vm.SaveSourceAsync(); Assert.Equal(2, vm.Sources.Count);
        vm.Selected = vm.Sources.Single(s => s.Id == source.Id); vm.NewVersion = "3"; await vm.CreateRemoteDraftAsync(); Assert.Contains("Remote-Entwurf", vm.Status);
        vm.GitClient = "git"; vm.SvnClient = "svn"; vm.SaveClientsCommand.Execute(null); Assert.True(File.Exists(P("clients.json")));
        await session.SignOutAsync(); await vm.SaveAssignmentAsync(); Assert.Contains("Berechtigung", vm.Status);
    }

    [Fact]
    public async Task Upgrade_requires_backup_maintenance_and_rights_and_is_not_automatic()
    {
        var catalog = new FolderProgramCatalogStore(P("new-catalog")); var service = new ReleaseService(new FolderReleaseStore(P("releases"))) { ProgramCatalog = catalog };
        await using var hub = new ReleaseHub(service); var editor = new EditorViewModel(SampleProjects.AcPruefung());
        var session = new SessionService(new SimulatedIdentityProvider(() => "tester"), null, allowDevelopmentMode: true);
        var vm = new ProgramSourcesViewModel(hub, editor, session, ImmediateDispatcher.Instance, [], P("clients.json"));
        await vm.RefreshAsync(); Assert.Contains("nicht eingerichtet", vm.SchemaText); Assert.False(await catalog.IsReadyAsync());
        await vm.UpgradeAsync(); Assert.Contains("Berechtigung", vm.Status); await session.SignInAsync();
        await vm.UpgradeAsync(); Assert.Contains("Sicherung", vm.Status);
        vm.MaintenanceConfirmed = vm.BackupConfirmed = true; await vm.UpgradeAsync(); Assert.True(await catalog.IsReadyAsync());
    }
    private static Task<(TestProject Project, ReleaseInfo? Release)> LoadSelected(StationViewModel station, string name, string version)
        => (Task<(TestProject, ReleaseInfo?)>)typeof(StationViewModel).GetMethod("LoadTestAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(station, [new Testumgebung.Persistence.Stations.AvailableTest(name, version, true, "")])!;

    [Fact]
    public async Task Startup_loads_default_once_preserves_local_work_and_start_respects_pins()
    {
        var (service, store, _, source) = await Context(); var first = await Publish(service, source, "1");
        var catalog = await store.ReadAsync(); await store.SaveAsync(catalog with { DefaultReleaseId = first.ReleaseId });
        await using var hub = new ReleaseHub(service); await hub.RefreshAsync();
        var editor = new EditorViewModel(new TestProject(new TestDefinition("Demo", "0", new("start", "Start"), new("loop", "Loop"), new("end", "End"))));
        var station = new StationViewModel("A", () => editor.Project, new ResourceManager(), releases: hub);
        var loader = new ProgramStartupLoader(hub, editor, () => station, () => false);
        editor.MarkChanged(); Assert.False(await loader.TryLoadAsync()); editor.Load(editor.Project);
        Assert.True(await loader.TryLoadAsync()); Assert.Equal(source.Name, editor.Project.Definition.Name); Assert.False(await loader.TryLoadAsync());
        var second = await Publish(service, source, "2"); await hub.RefreshAsync();
        var loaded = await LoadSelected(station, source.Name, "2"); Assert.Equal(second.ReleaseId, loaded.Release!.ReleaseId);
        var rescanned = await LoadSelected(station, source.Name, "1"); Assert.Equal(second.ReleaseId, rescanned.Release!.ReleaseId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LoadSelected(station, "missing", "0"));
        catalog = await store.ReadAsync(); await store.SaveAsync(catalog with { Stations = [new(station.ProgramStationId, source.Id, false, first.ReleaseId)] }); await hub.RefreshAsync();
        loaded = await LoadSelected(station, source.Name, "1"); Assert.Equal(first.ReleaseId, loaded.Release!.ReleaseId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LoadSelected(station, source.Name, "2"));
        var start = station.StartAsync();
        for (var i = 0; i < 300 && !station.IsRunning; i++) await Task.Delay(10);
        Assert.True(station.IsRunning); Assert.Equal(first.ReleaseId, station.ActiveRelease!.ReleaseId); Assert.Contains("veraltet", station.ProgramVersionWarning);
        Assert.Null(((Testumgebung.Engine.IVersionSource)station).NextDefinition(loaded.Project.Definition));
        station.AbortCommand.Execute(null); await start.Within();
        editor.Load(editor.Project, P("local.json")); Assert.False(await new ProgramStartupLoader(hub, editor, () => station, () => false).TryLoadAsync());
        editor.Load(editor.Project); Assert.False(await new ProgramStartupLoader(hub, editor, () => station, () => true).TryLoadAsync());
        catalog = await store.ReadAsync(); await store.SaveAsync(catalog with { DefaultReleaseId = "missing", Stations = [] }); await hub.RefreshAsync();
        var broken = new ProgramStartupLoader(hub, editor, () => station, () => false); Assert.False(await broken.TryLoadAsync()); Assert.NotNull(broken.Error);
    }

    private sealed class PackageFailureStore(IReleaseStore inner) : IReleaseStore
    {
        public string Name => inner.Name;
        public Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task<byte[]> GetPackageAsync(string releaseId, CancellationToken cancellationToken = default) => throw new IOException("Database unavailable");
        public Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken cancellationToken = default) => inner.CreateAsync(info, package, audit, cancellationToken);
        public Task<bool> TransitionAsync(ReleaseTransition transition, AuditDraft audit, CancellationToken cancellationToken = default) => inner.TransitionAsync(transition, audit, cancellationToken);
        public Task AppendAuditAsync(AuditDraft audit, CancellationToken cancellationToken = default) => inner.AppendAuditAsync(audit, cancellationToken);
        public Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken cancellationToken = default) => inner.ReadAuditAsync(cancellationToken);
    }
    [Fact]
    public async Task Verified_package_cache_survives_transient_fetch_failure_but_not_unknown_or_changed_hashes()
    {
        var inner = new FolderReleaseStore(P("cache-releases")); var switchable = new SwitchableReleaseStore(inner); var service = new ReleaseService(switchable);
        var release = await service.CreateDraftAsync(SampleProjects.AcPruefung(), "1", Both); Assert.True((await service.OpenAsync(release)).IsIntact);
        switchable.Inner = new PackageFailureStore(inner); Assert.True((await service.OpenAsync(release)).IsIntact);
        await Assert.ThrowsAsync<IOException>(() => service.OpenAsync(release with { PackageSha256 = new string('0', 64) }));
        switchable.Inner = inner;
        var file = P("cache-releases/" + release.ReleaseId + ".tpkg"); File.WriteAllBytes(file, [0, 1, 2]);
        await Assert.ThrowsAsync<ReleasePackageException>(() => service.OpenAsync(release));
    }

}
