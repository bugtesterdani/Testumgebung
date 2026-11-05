using System.Diagnostics;
using System.IO.Compression;
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

public sealed class SvnClientFactAttribute : FactAttribute
{
    public SvnClientFactAttribute() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TESTUMGEBUNG_SVN_TEST_CLIENT"))) Skip = "SVN client not configured (TESTUMGEBUNG_SVN_TEST_CLIENT)."; }
}
public sealed partial class ProgramSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tu-source-tests-" + Guid.NewGuid().ToString("N"));
    private static Actor Both => new("tester", UserRights.Develop | UserRights.Release);
    public ProgramSourcesTests() => Directory.CreateDirectory(_root);
    private string P(string path) => Path.Combine(_root, path);
    public void Dispose() { foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal); Directory.Delete(_root, true); }
    private static ProgramSource Source(string repository = "https://example.invalid/program.git") => new("source", "AC-Prüfung", "Git", repository, "main", "program.tproj.json");
    private static ReleaseInfo Released(string id, ProgramSource source, int time, ReleaseState state = ReleaseState.Released)
        => new(id, source.Name, id, state, "hash", 1, "tester", DateTimeOffset.UnixEpoch, ReleasedAt: DateTimeOffset.UnixEpoch.AddDays(time), Source: new(source, new string('a', 40), "identity", "hash"));

    [Fact]
    public void Assignments_follow_only_their_line_pin_archived_releases_and_report_newer_versions()
    {
        var source = Source(); var other = source with { Id = "other", Line = "other" };
        var old = Released("1", source, 1, ReleaseState.Archived); var next = Released("2", source, 2); var unrelated = Released("3", other, 3);
        var releases = new[] { old, next, unrelated };
        var catalog = new ProgramCatalog(0, [source, other], [new("pinned", source.Id, false, old.ReleaseId), new("auto", source.Id)], old.ReleaseId);
        Assert.Equal("2", ProgramAssignmentResolver.Resolve(catalog, releases, "auto", "")!.Release.ReleaseId);
        var pin = ProgramAssignmentResolver.Resolve(catalog, releases, "pinned", "")!; Assert.Equal("1", pin.Release.ReleaseId); Assert.Contains("veraltet", pin.Warning);
        Assert.Null(ProgramAssignmentResolver.Resolve(catalog, [old], "pinned", "")!.Warning);
        Assert.Equal("2", ProgramAssignmentResolver.Resolve(catalog, releases, "default", "")!.Release.ReleaseId);
        Assert.Throws<InvalidOperationException>(() => ProgramAssignmentResolver.Resolve(catalog, [next], "pinned", ""));
        Assert.Throws<InvalidOperationException>(() => ProgramAssignmentResolver.Resolve(catalog with { Sources = [] }, releases, "auto", ""));
        Assert.Throws<InvalidOperationException>(() => ProgramAssignmentResolver.Resolve(catalog, [unrelated], "auto", ""));
        Assert.Throws<InvalidOperationException>(() => ProgramAssignmentResolver.Resolve(catalog, [next], "default", ""));
        Assert.Null(ProgramAssignmentResolver.Resolve(ProgramCatalog.Empty, [], "none", "missing"));
        Assert.Equal(next, ProgramAssignmentResolver.Resolve(ProgramCatalog.Empty, [next], "none", source.Name)!.Release);
    }

    [Fact]
    public async Task Folder_catalog_upgrade_is_explicit_and_stale_edits_do_not_overwrite_other_stations()
    {
        var store = new FolderProgramCatalogStore(P("catalog")); Assert.False(await store.IsReadyAsync());
        await store.UpgradeAsync(); await store.UpgradeAsync(); var original = await store.ReadAsync();
        await store.SaveAsync(original with { Sources = [Source()] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(original));
        Assert.Single((await store.ReadAsync()).Sources);
        var proxy = new SwitchableProgramCatalogStore(store); Assert.True(await proxy.IsReadyAsync()); await proxy.UpgradeAsync();
        var changed = await proxy.ReadAsync(); await proxy.SaveAsync(changed with { DefaultReleaseId = "x" });
        Assert.Equal("x", (await proxy.ReadAsync()).DefaultReleaseId);
        var settings = new ProgramSourceClientSettings("git-custom", "svn-custom"); settings.Save(P("clients.json"));
        Assert.Equal(settings, ProgramSourceClientSettings.Load(P("clients.json"))); Assert.Equal(new(), ProgramSourceClientSettings.Load(P("missing.json")));
        Assert.Equal(new[] { "Git", "SVN" }, settings.Providers().Names); Assert.Throws<InvalidOperationException>(() => settings.Providers().Get("missing"));
    }

    [SqliteFact]
    public async Task Sql_catalog_upgrade_preserves_existing_releases_and_uses_compare_and_swap()
    {
        var database = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("catalog.db")}", new SqliteDialect());
        var service = new ReleaseService(new SqlReleaseStore(database)); var project = SampleProjects.AcPruefung();
        var draft = await service.CreateDraftAsync(project, "before", Both);
        var store = new SqlProgramCatalogStore(database); Assert.False(await store.IsReadyAsync());
        await store.UpgradeAsync(); await store.UpgradeAsync(); Assert.True(await store.IsReadyAsync());
        var catalog = await store.ReadAsync(); await store.SaveAsync(catalog with { Sources = [Source()] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(catalog));
        Assert.Single((await store.ReadAsync()).Sources); Assert.Equal(draft.ReleaseId, Assert.Single(await service.ListAsync()).ReleaseId);
        Assert.True((await service.OpenAsync(draft)).IsIntact);
        foreach (var provider in Enum.GetValues<DatabaseProvider>()) Assert.Contains("tu_program_catalog", SqlDialect.For(provider).CreateProgramCatalog);
    }

    [Theory]
    [InlineData("../project.json")]
    [InlineData("C:/project.json")]
    [InlineData("a//project.json")]
    [InlineData("/project.json")]
    public void Project_paths_cannot_escape_export(string path) => Assert.Throws<ArgumentException>(() => ProgramSourceValidation.Validate(Source() with { ProjectPath = path }));
    [Theory]
    [InlineData("https://user:secret@example.com/repo")]
    [InlineData("git://example.com/repo")]
    [InlineData("-command")]
    public void Credentials_and_unsupported_urls_are_rejected(string repository) => Assert.Throws<ArgumentException>(() => ProgramSourceValidation.Validate(Source(repository)));

    [Fact]
    public async Task Git_exports_exact_remote_commit_rejects_local_only_commits_and_ignores_dirty_worktree()
    {
        var work = P("work"); Directory.CreateDirectory(work); var remote = P("remote.git");
        await Command("git", "init", "--bare", remote); await Command("git", "init", "-b", "main", work);
        var project = SampleProjects.AcPruefung(); new ProjectSerializer().Save(project, Path.Combine(work, "program.tproj.json"));
        File.WriteAllText(Path.Combine(work, "helper.py"), "print('original')");
        await Git(work, "add", "."); await Git(work, "commit", "-m", "initial"); await Git(work, "remote", "add", "origin", new Uri(remote + Path.DirectorySeparatorChar).AbsoluteUri);
        await Git(work, "push", "origin", "main"); var commit = await Git(work, "rev-parse", "HEAD");
        var source = Source(new Uri(remote + Path.DirectorySeparatorChar).AbsoluteUri) with { Name = project.Definition.Name };
        var provider = new GitProgramSource(allowLocalRepositories: true);
        using (var snapshot = await provider.ExportAsync(source, null))
        {
            Assert.Equal(commit, snapshot.Version.Revision); Assert.True(File.Exists(snapshot.ProjectFile));
            Assert.Equal(SourceArchive.Build(snapshot), SourceArchive.Build(snapshot));
            using var zip = new ZipArchive(new MemoryStream(SourceArchive.Build(snapshot))); Assert.NotNull(zip.GetEntry("helper.py"));
        }
        File.WriteAllText(Path.Combine(work, "helper.py"), "print('only local')"); await Git(work, "add", "."); await Git(work, "commit", "-m", "local only");
        var local = await Git(work, "rev-parse", "HEAD"); await Assert.ThrowsAsync<IOException>(() => provider.ExportAsync(source, local));
        File.WriteAllText(Path.Combine(work, "program.tproj.json"), "dirty and invalid");
        using (var snapshot = await provider.ExportAsync(source, commit)) Assert.Equal(project.Definition.Name, new ProjectSerializer().Load(snapshot.ProjectFile).Definition.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExportAsync(source, "main"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExportAsync(source with { Line = "--bad" }, null));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExportAsync(source with { Line = "main~1" }, null));
        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.ExportAsync(source with { ProjectPath = "missing.json" }, null));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExportAsync(source, null, cancellation.Token));
    }

    [SvnClientFact]
    public async Task Svn_exports_remote_revision_and_preserves_uuid_and_historical_content()
    {
        var client = Environment.GetEnvironmentVariable("TESTUMGEBUNG_SVN_TEST_CLIENT")!;
        var admin = Path.Combine(Path.GetDirectoryName(client)!, "svnadmin.exe"); var remote = P("svn-repository");
        await Command(admin, "create", remote); var uri = new Uri(remote + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await Command(client, "mkdir", "--non-interactive", uri + "/trunk", "-m", "create trunk");
        var work = P("svn-work"); await Command(client, "checkout", "--non-interactive", uri + "/trunk", work);
        new ProjectSerializer().Save(SampleProjects.AcPruefung(), Path.Combine(work, "program.tproj.json"));
        File.WriteAllText(Path.Combine(work, "helper.py"), "original"); await Command(client, "add", work + "/program.tproj.json", work + "/helper.py");
        await Command(client, "commit", "--non-interactive", work, "-m", "program");
        var provider = new SvnProgramSource(client, allowLocalRepositories: true); var source = Source(uri) with { Provider = "SVN", Line = "trunk" };
        string revision;
        using (var first = await provider.ExportAsync(source, null)) { revision = first.Version.Revision; Assert.NotEmpty(first.Version.RepositoryIdentity); Assert.Equal("original", File.ReadAllText(Path.Combine(first.Directory, "export", "helper.py"))); }
        File.WriteAllText(Path.Combine(work, "helper.py"), "new"); await Command(client, "commit", "--non-interactive", work, "-m", "change");
        using (var old = await provider.ExportAsync(source, revision)) Assert.Equal("original", File.ReadAllText(Path.Combine(old.Directory, "export", "helper.py")));
        using (var latest = await provider.ExportAsync(source, null)) Assert.NotEqual(revision, latest.Version.Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExportAsync(source, "-1"));
        await Assert.ThrowsAsync<IOException>(() => provider.ExportAsync(source with { Line = "missing" }, null));
    }


    [Fact]
    public async Task Missing_client_has_actionable_diagnostic_and_local_production_repository_is_rejected()
    {
        var provider = new GitProgramSource(P("missing-client.exe"));
        var error = await Assert.ThrowsAsync<IOException>(() => provider.ExportAsync(Source(), null));
        Assert.Contains("Clientpfade", error.Message);
        Assert.Throws<ArgumentException>(() => ProgramSourceValidation.Validate(Source(new Uri(_root + Path.DirectorySeparatorChar).AbsoluteUri)));
        Assert.Throws<ArgumentException>(() => ProgramSourceValidation.Validate(Source("https://example.com/repo?token=secret")));
    }

    private static Task<string> Git(string work, params string[] arguments) => Command("git", ["-C", work, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", .. arguments]);
    private static async Task<string> Command(string executable, params string[] args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error); return (await output).Trim();
    }
}
