using System.Text.Json.Nodes;
using Testumgebung.Contracts;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Mvvm;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ReleaseEditorLoadingTests
{
    private static readonly Actor Author = new("fixture", UserRights.Develop | UserRights.Release);
    private static TestProject Project(string name)
    {
        var serializer = new ProjectSerializer(); var json = JsonNode.Parse(serializer.Serialize(SampleProjects.AcPruefung()))!;
        json["name"] = name; return serializer.Deserialize(json.ToJsonString());
    }
    private sealed class Dialogs : IFileDialogService
    {
        public UnsavedProjectDecision Decision { get; set; } = UnsavedProjectDecision.Cancel;
        public string? SavePath { get; set; }
        public int Asked { get; private set; }
        public List<string> Errors { get; } = [];
        public UnsavedProjectDecision AskSaveProjectChanges() { Asked++; return Decision; }
        public string? OpenProject() => null;
        public string? SaveProject(string suggestedName) => SavePath;
        public void ShowError(string message) => Errors.Add(message);
        public void OpenExternal(string path) { }
    }
    private sealed class BlockingStore(IReleaseStore inner) : IReleaseStore
    {
        public int HoldNext;
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => inner.Name;
        public Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async Task<byte[]> GetPackageAsync(string id, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref HoldNext, 0) == 1) { Reading.TrySetResult(); await Continue.Task.WaitAsync(ct); }
            return await inner.GetPackageAsync(id, ct);
        }
        public Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken ct = default) => inner.CreateAsync(info, package, audit, ct);
        public Task<bool> TransitionAsync(ReleaseTransition transition, AuditDraft audit, CancellationToken ct = default) => inner.TransitionAsync(transition, audit, ct);
        public Task AppendAuditAsync(AuditDraft audit, CancellationToken ct = default) => inner.AppendAuditAsync(audit, ct);
        public Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken ct = default) => inner.ReadAuditAsync(ct);
    }
    private sealed record Context(string Directory, ReleaseService Service, ReleaseHub Hub, ShellViewModel Shell, QueueDispatcher Ui, ReleaseInfo Info, BlockingStore Store)
    {
        public ReleasesViewModel Page => Shell.ReleasesPage!;
        public async Task<bool> Load()
        {
            var load = Page.LoadInEditorAsync(); await Ui.Until(() => load.IsCompleted, "editor load"); return await load;
        }
    }
    private static async Task<Context> Setup(ReleaseState state = ReleaseState.Draft, bool develop = true, Dialogs? dialogs = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "release-editor-" + Guid.NewGuid().ToString("N"));
        var blocking = new BlockingStore(new FolderReleaseStore(directory)); var service = new ReleaseService(blocking);
        var project = Project("Programm B"); var info = await service.CreateDraftAsync(project, "1.0", Author);
        if (state != ReleaseState.Draft)
        {
            await service.ReviewAsync(info, Author); info = (await service.ListAsync()).Single();
            if (state is ReleaseState.Released or ReleaseState.Archived) info = await service.ReleaseAsync(info, Author, true);
            if (state == ReleaseState.Archived)
            {
                var next = await service.CreateDraftAsync(project, "2.0", Author); await service.ReviewAsync(next, Author);
                await service.ReleaseAsync((await service.ListAsync()).Single(r => r.ReleaseId == next.ReleaseId), Author, true);
                info = (await service.ListAsync()).Single(r => r.ReleaseId == info.ReleaseId);
            }
        }
        var hub = new ReleaseHub(service); await hub.RefreshAsync(); var ui = new QueueDispatcher();
        var identity = IdentityServices.Simulated(() => "Tester", new AccessConfig { Users = [new RightsEntry { Upn = "tester@simuliert.local", Develop = develop, Release = true }] });
        var shell = new ShellViewModel(ui, dialogs: dialogs, project: Project("Programm A"), releases: hub, identity: identity);
        await shell.LoginAsync(false); ui.Pump(); var page = shell.ReleasesPage!;
        page.SelectedProgramName = "Programm B"; page.Selected = page.Releases.Single(r => r.ReleaseId == info.ReleaseId);
        await ui.Until(() => page.Package.Count > 0, "package preview");
        return new(directory, service, hub, shell, ui, info, blocking);
    }
    [Theory]
    [InlineData(ReleaseState.Draft)] [InlineData(ReleaseState.Review)] [InlineData(ReleaseState.Released)] [InlineData(ReleaseState.Archived)]
    public async Task Selected_version_opens_as_a_complete_independent_editor_copy_without_changing_the_package(ReleaseState state)
    {
        var c = await Setup(state); await using var hub = c.Hub;
        var bytes = await c.Service.Store.GetPackageAsync(c.Info.ReleaseId); var before = c.Shell.Editor.Project;
        c.Shell.Page = ShellPage.Releases;
        Assert.True(c.Page.LoadInEditorCommand.CanExecute(null)); Assert.True(await c.Load());
        Assert.NotSame(before, c.Shell.Editor.Project); Assert.Equal("Programm B", c.Shell.Editor.ProjectName); Assert.Equal("1.0", c.Shell.Editor.Version);
        Assert.Null(c.Shell.Editor.FilePath); Assert.True(c.Shell.Editor.IsDirty); Assert.Equal(ShellPage.Editor, c.Shell.Page);
        var serializer = new ProjectSerializer();
        Assert.Equal(serializer.Serialize((await c.Service.OpenAsync(c.Info)).Project), serializer.Serialize(c.Shell.Editor.Project));
        c.Shell.Editor.Project.Definition.Version = "local-change";
        Assert.Equal("1.0", (await c.Service.OpenAsync(c.Info)).Project.Definition.Version);
        Assert.Equal(bytes, await c.Service.Store.GetPackageAsync(c.Info.ReleaseId)); Assert.False(c.Shell.SelectedStation.UseDraft);
    }
    [Fact]
    public async Task Release_right_alone_cannot_load_the_editor()
    {
        var c = await Setup(develop: false); await using var hub = c.Hub; var before = c.Shell.Editor.Project;
        Assert.False(c.Page.LoadInEditorCommand.CanExecute(null)); Assert.False(await c.Load()); Assert.Same(before, c.Shell.Editor.Project);
    }
    [Theory]
    [InlineData(UnsavedProjectDecision.Cancel, false)] [InlineData(UnsavedProjectDecision.Discard, true)] [InlineData(UnsavedProjectDecision.Save, true)]
    public async Task Unsaved_work_is_preserved_until_the_user_saves_or_explicitly_discards_it(UnsavedProjectDecision decision, bool opens)
    {
        var dialogs = new Dialogs { Decision = decision }; var c = await Setup(dialogs: dialogs); await using var hub = c.Hub;
        dialogs.SavePath = Path.Combine(c.Directory, "previous.tproj.json"); var previous = c.Shell.Editor.Project;
        previous.Definition.Version = "unsaved"; c.Shell.Editor.MarkChanged();
        Assert.Equal(opens, await c.Load()); Assert.Equal(1, dialogs.Asked);
        if (!opens) { Assert.Same(previous, c.Shell.Editor.Project); Assert.True(c.Shell.Editor.IsDirty); }
        if (decision == UnsavedProjectDecision.Save) Assert.Equal("unsaved", new ProjectSerializer().Load(dialogs.SavePath).Definition.Version);
        if (opens) { Assert.True(c.Shell.Editor.IsDirty); Assert.Null(c.Shell.Editor.FilePath); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancelled_or_failed_save_keeps_the_previous_editor_project(bool fails)
    {
        var dialogs = new Dialogs { Decision = UnsavedProjectDecision.Save }; var c = await Setup(dialogs: dialogs); await using var hub = c.Hub;
        dialogs.SavePath = fails ? c.Directory : null; var previous = c.Shell.Editor.Project; c.Shell.Editor.MarkChanged();
        Assert.False(await c.Load()); Assert.Same(previous, c.Shell.Editor.Project); Assert.True(c.Shell.Editor.IsDirty);
        Assert.Equal(fails, dialogs.Errors.Count > 0);
    }
    [Fact]
    public async Task A_dirty_editor_without_a_confirmation_service_is_never_silently_replaced()
    {
        var c = await Setup(); await using var hub = c.Hub; var previous = c.Shell.Editor.Project; c.Shell.Editor.MarkChanged();
        Assert.False(await c.Load()); Assert.Same(previous, c.Shell.Editor.Project);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Corrupt_or_missing_packages_do_not_replace_the_editor_or_prompt_to_discard_changes(bool missing)
    {
        var dialogs = new Dialogs { Decision = UnsavedProjectDecision.Discard }; var c = await Setup(dialogs: dialogs); await using var hub = c.Hub;
        var file = Path.Combine(c.Directory, c.Info.ReleaseId + ".tpkg");
        if (missing) File.Delete(file); else await File.WriteAllTextAsync(file, "broken package");
        var previous = c.Shell.Editor.Project; c.Shell.Editor.MarkChanged();
        Assert.False(await c.Load()); Assert.Same(previous, c.Shell.Editor.Project); Assert.Equal(0, dialogs.Asked); Assert.True(c.Page.StatusIsError);
    }
    [Theory]
    [InlineData("editor")] [InlineData("selection")] [InlineData("version")] [InlineData("logout")]
    public async Task A_pending_load_cannot_overwrite_a_new_editor_selection_or_revoke_permissions(string change)
    {
        var c = await Setup(); await using var hub = c.Hub; c.Store.HoldNext = 1;
        var load = c.Page.LoadInEditorAsync(); await c.Store.Reading.Task.Within();
        try
        {
            Assert.False(c.Page.LoadInEditorCommand.CanExecute(null)); Assert.False(await c.Page.LoadInEditorAsync());
            if (change == "editor") c.Shell.Editor.Load(Project("New editor"));
            if (change == "selection") c.Page.SelectedProgramName = "Programm A";
            if (change == "version")
            {
                var source = (await c.Service.OpenAsync(c.Info)).Project;
                var next = await c.Service.CreateDraftAsync(source, "2.0", Author); await c.Hub.RefreshAsync(); c.Ui.Pump();
                c.Page.Selected = c.Page.Releases.Single(r => r.ReleaseId == next.ReleaseId);
            }
            if (change == "logout") { await c.Shell.LogoutAsync(); c.Ui.Pump(); }
            var expected = c.Shell.Editor.Project; c.Store.Continue.TrySetResult();
            await c.Ui.Until(() => load.IsCompleted, "pending load stopped"); Assert.False(await load); Assert.Same(expected, c.Shell.Editor.Project);
        }
        finally { c.Store.Continue.TrySetResult(); }
    }
    [Fact]
    public async Task Loading_another_program_does_not_stop_or_replace_an_active_station_run()
    {
        var c = await Setup(ReleaseState.Released); await using var hub = c.Hub;
        var draft = await c.Service.CreateDraftAsync(c.Shell.Editor.Project, "1.0", Author);
        await c.Service.ReviewAsync(draft, Author);
        await c.Service.ReleaseAsync((await c.Service.ListAsync()).Single(r => r.ReleaseId == draft.ReleaseId), Author, true);
        await hub.RefreshAsync(); c.Ui.Pump();
        var station = c.Shell.SelectedStation; var running = station.StartAsync();
        try
        {
            await c.Ui.Until(() => station.IsRunning && station.ActiveContent is { IsAwaiting: true }, "active station waiting for input");
            var prompt = station.ActiveContent; var setup = c.Shell.Coordinator.Setup;
            Assert.True(await c.Load());
            Assert.True(station.IsRunning); Assert.False(running.IsCompleted); Assert.Same(prompt, station.ActiveContent);
            Assert.Same(setup, c.Shell.Coordinator.Setup); Assert.False(station.UseDraft);
            Assert.Equal("Programm B", c.Shell.Editor.ProjectName);
        }
        finally
        {
            station.RequestAbort(); await c.Ui.Until(() => running.IsCompleted, "station stopped after test"); await running;
        }
    }
    [Fact]
    public async Task Saving_the_old_project_uses_its_existing_path_and_the_new_copy_still_has_no_file_destination()
    {
        var dialogs = new Dialogs { Decision = UnsavedProjectDecision.Save }; var c = await Setup(dialogs: dialogs); await using var hub = c.Hub;
        var path = Path.Combine(c.Directory, "existing.tproj.json"); c.Shell.Editor.Load(c.Shell.Editor.Project, path);
        c.Shell.Editor.Project.Definition.Version = "keep-this"; c.Shell.Editor.MarkChanged();
        Assert.True(await c.Load()); Assert.Equal("keep-this", new ProjectSerializer().Load(path).Definition.Version);
        Assert.Null(c.Shell.Editor.FilePath); Assert.True(c.Shell.Editor.IsDirty);
    }
    [Fact]
    public async Task A_local_example_without_a_package_can_be_loaded_without_changing_the_example()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        var example = Testumgebung.Persistence.Stations.OrderExample.Load(Path.Combine(root.FullName, "docs", "beispiele"));
        var directory = Path.Combine(Path.GetTempPath(), "editor-example-" + Guid.NewGuid().ToString("N"));
        await using var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(directory))); var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: example.Project, releases: hub,
            stations: new Testumgebung.Persistence.Stations.StationCoordinator(example.Setup, new Testumgebung.Hardware.ResourceManager()));
        shell.Login(); ui.Pump(); var page = shell.ReleasesPage!; page.SelectedProgramName = "Programm D";
        Assert.Null(page.Selected); var loading = page.LoadInEditorAsync(); await ui.Until(() => loading.IsCompleted, "load local example"); Assert.True(await loading);
        Assert.Equal("Programm D", shell.Editor.ProjectName); Assert.True(shell.Editor.IsDirty); Assert.Null(shell.Editor.FilePath);
        shell.Editor.Project.Definition.Version = "changed";
        Assert.NotEqual("changed", example.Setup.ExampleProjects.Single(p => p.Definition.Name == "Programm D").Definition.Version);
    }
}
