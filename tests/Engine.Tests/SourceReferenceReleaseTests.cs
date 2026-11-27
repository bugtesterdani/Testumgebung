using System.IO.Compression;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Domain.Code;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Programs;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Engine.Tests;

public sealed class SourceReferenceReleaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tu-reference-" + Guid.NewGuid().ToString("N"));
    private static readonly Actor Actor = new("tester", UserRights.Develop | UserRights.Release);
    private const string Secret = "return 277123; // source must never enter SQL";
    public SourceReferenceReleaseTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Provider(string root) : IProgramSourceProvider
    {
        public string Id => "Fixture";
        public bool Available { get; set; } = true;
        public bool Tamper { get; set; }
        public string Identity { get; set; } = "repository-identity";
        public string? LastRevision { get; private set; }
        public int Calls { get; private set; }
        public Task<SourceSnapshot> ExportAsync(ProgramSource source, string? revision, CancellationToken ct = default)
        {
            Calls++;
            LastRevision = revision;
            if (!Available) throw new IOException("offline");
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            var export = Path.Combine(directory, "export");
            Directory.CreateDirectory(export);
            var project = SampleProjects.AcPruefung();
            project.Definition.Library.Add(new CodeBlockDefinition("secret", "Secret") { Source = Secret });
            var file = Path.Combine(export, source.ProjectPath);
            new ProjectSerializer().Save(project, file);
            File.WriteAllText(Path.Combine(export, "helper.py"), Tamper ? "changed" : Secret);
            return Task.FromResult(new SourceSnapshot(new(source, revision ?? new string('a', 40), Identity,
                ReleasePackage.Sha256(File.ReadAllBytes(file))), file, directory));
        }
    }

    private (ReleaseService Service, Provider Provider, ProgramSource Source) Setup(IReleaseStore store)
    {
        var provider = new Provider(_root);
        var source = new ProgramSource("program", SampleProjects.AcPruefung().Definition.Name, provider.Id,
            "https://example.invalid/program.git", "main", "program.tproj.json");
        return (new ReleaseService(store) { SourceProviders = new([provider]), SourceCacheDirectory = Path.Combine(_root, "cache") }, provider, source);
    }

    private ReleaseService Restart(ReleaseService original, Provider provider) => new(original.Store)
    {
        SourceProviders = new([provider]), SourceCacheDirectory = original.SourceCacheDirectory
    };

    [SqliteFact]
    public async Task Sql_contains_only_reference_and_reopens_exact_revision_with_verified_offline_cache()
    {
        var database = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(_root, "sql.db")}", new SqliteDialect());
        var (service, provider, source) = Setup(new SwitchableReleaseStore(new SqlReleaseStore(database)));
        var draft = await service.CreateRemoteDraftAsync(source, null, "release-277", Actor);
        var bytes = await service.Store.GetPackageAsync(draft.ReleaseId);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        {
            Assert.Equal(new[] { "manifest.json" }, zip.Entries.Select(entry => entry.FullName));
            using var reader = new StreamReader(zip.Entries.Single().Open());
            Assert.DoesNotContain(Secret, await reader.ReadToEndAsync());
        }
        Assert.Equal(ReleasePackage.ReferenceFormat, ReleasePackage.ReadReference(bytes)!.Format);
        Assert.Equal(ReleasePackage.Sha256(bytes), draft.PackageSha256);
        Assert.Equal(draft.Source, (await service.ListAsync()).Single().Source);
        provider.Available = false;
        await Assert.ThrowsAsync<ReleasePackageException>(() => service.OpenAsync(draft));
        provider.Available = true;
        var opened = await service.OpenAsync(draft);
        Assert.True(opened.IsIntact);
        Assert.Equal(Secret, opened.Project.Definition.Library.Single().Source);
        Assert.Equal(draft.Source!.Revision, provider.LastRevision);
        Assert.Equal("release-277", opened.Project.Definition.Version);
        var calls = provider.Calls;
        provider.Available = false;
        Assert.True((await Restart(service, provider).OpenAsync(draft)).IsIntact);
        Assert.Equal(calls, provider.Calls); // persisted cache, not merely in-process state
        var cache = Directory.GetFiles(service.SourceCacheDirectory, "*.zip").Single();
        await File.WriteAllBytesAsync(cache, [1, 2, 3]);
        await Assert.ThrowsAsync<ReleasePackageException>(() => Restart(service, provider).OpenAsync(draft));
        provider.Available = true;
        Assert.True((await Restart(service, provider).OpenAsync(draft)).IsIntact); // repair cache from exact source
        await Assert.ThrowsAsync<ReleasePackageException>(() => service.OpenAsync(draft with { PackageSha256 = new string('0', 64) }));
    }

    [Fact]
    public async Task Reference_preserves_runtime_evidence_and_rejects_self_consistent_but_different_cached_project()
    {
        var (_, provider, source) = Setup(new FolderReleaseStore(Path.Combine(_root, "folder")));
        using var snapshot = await provider.ExportAsync(source, null);
        var serializer = new ProjectSerializer();
        var project = serializer.Load(snapshot.ProjectFile);
        project.Definition.Version = "release";
        var environment = new RuntimeSnapshot([new RuntimeComponent("test-runtime", "1", new string('b', 64))]);
        var archive = SourceArchive.Build(snapshot);
        var (full, _) = ReleasePackage.Build(project, Actor.User, DateTimeOffset.UnixEpoch, serializer,
            environment, snapshot.Version, archive);
        var reference = ReleasePackage.ToReference(full);
        using (var zip = new ZipArchive(new MemoryStream(reference)))
            Assert.Equal(new[] { "manifest.json", "runtime.json" }, zip.Entries.Select(entry => entry.FullName));
        var opened = ReleasePackage.OpenReferenced(reference, full);
        Assert.Equal(environment.Components, opened.Environment!.Components);
        project.Definition.Library.Single().Source = "return 999;";
        var (tampered, _) = ReleasePackage.Build(project, Actor.User, DateTimeOffset.UnixEpoch, serializer,
            environment, snapshot.Version, archive);
        Assert.True(ReleasePackage.Open(tampered).IsIntact); // attacker recomputed the cache's own hashes
        Assert.Throws<ReleasePackageException>(() => ReleasePackage.OpenReferenced(reference, tampered));
        var (missingEnvironment, _) = ReleasePackage.Build(serializer.Load(snapshot.ProjectFile), Actor.User,
            DateTimeOffset.UnixEpoch, serializer, source: snapshot.Version, sourceArchive: archive);
        Assert.Throws<ReleasePackageException>(() => ReleasePackage.OpenReferenced(reference, missingEnvironment));
    }

    [SqliteFact]
    public async Task Remote_tampering_is_rejected_and_release_requires_fresh_remote_verification()
    {
        var database = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(_root, "sql.db")}", new SqliteDialect());
        var (service, provider, source) = Setup(new SqlReleaseStore(database));
        var draft = await service.CreateRemoteDraftAsync(source, null, "1", Actor);
        provider.Tamper = true; // project hash unchanged; external source archive changed
        await Assert.ThrowsAsync<ReleasePackageException>(() => service.OpenAsync(draft));
        provider.Tamper = false;
        provider.Identity = "different-repository";
        await Assert.ThrowsAsync<ReleasePackageException>(() => service.OpenAsync(draft));
        provider.Identity = "repository-identity";
        Assert.All(await service.ReviewAsync(draft, Actor), check => Assert.True(check.Passed, check.Detail));
        var reviewed = (await service.ListAsync()).Single();
        provider.Tamper = true;
        await Assert.ThrowsAsync<ReleaseRuleException>(() => service.ReleaseAsync(reviewed, Actor, true));
        provider.Tamper = false;
        var released = await service.ReleaseAsync(reviewed, Actor, true);
        Assert.Equal(ReleaseState.Released, released.State);
        Assert.Null(AuditChain.Verify(await service.Store.ReadAuditAsync()));
    }

    [SqliteFact]
    public async Task Legacy_embedded_and_unversioned_sql_releases_remain_readable()
    {
        var database = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(_root, "sql.db")}", new SqliteDialect());
        var sql = new SqlReleaseStore(database);
        var (service, provider, source) = Setup(sql);
        var (legacyService, _, _) = Setup(new FolderReleaseStore(Path.Combine(_root, "legacy")));
        var legacy = await legacyService.CreateRemoteDraftAsync(source, null, "old", Actor);
        var embedded = await legacyService.Store.GetPackageAsync(legacy.ReleaseId);
        await sql.CreateAsync(legacy, embedded, new AuditDraft(DateTimeOffset.UtcNow, Actor.User, "legacy", "import"));
        provider.Available = false;
        Assert.True((await service.OpenAsync(legacy)).IsIntact);
        Assert.Null(ReleasePackage.ReadReference(embedded));
        var local = await service.CreateDraftAsync(SampleProjects.AcPruefung(), "local", Actor);
        Assert.Null(ReleasePackage.ReadReference(await sql.GetPackageAsync(local.ReleaseId)));
        Assert.True((await service.OpenAsync(local)).IsIntact);
    }
}

public sealed partial class ProgramSourcesTests
{
    [SqliteFact]
    public async Task Sql_reference_reloads_original_git_commit_after_remote_branch_advances()
    {
        var work = P("reference-work");
        var remote = P("reference-remote.git");
        Directory.CreateDirectory(work);
        await Command("git", "init", "--bare", remote);
        await Command("git", "init", "-b", "main", work);
        var serializer = new ProjectSerializer();
        var project = SampleProjects.AcPruefung();
        project.Description = "original commit";
        serializer.Save(project, Path.Combine(work, "program.tproj.json"));
        await Git(work, "add", ".");
        await Git(work, "commit", "-m", "original");
        var uri = new Uri(remote + Path.DirectorySeparatorChar).AbsoluteUri;
        await Git(work, "remote", "add", "origin", uri);
        await Git(work, "push", "origin", "main");
        var commit = await Git(work, "rev-parse", "HEAD");
        var database = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={P("git-reference.db")}", new SqliteDialect());
        var service = new ReleaseService(new SqlReleaseStore(database))
        {
            SourceProviders = new([new GitProgramSource(allowLocalRepositories: true)]),
            SourceCacheDirectory = P("git-reference-cache")
        };
        var draft = await service.CreateRemoteDraftAsync(Source(uri), commit, "released", Both);
        project.Description = "new branch head";
        serializer.Save(project, Path.Combine(work, "program.tproj.json"));
        await Git(work, "add", ".");
        await Git(work, "commit", "-m", "next");
        await Git(work, "push", "origin", "main");
        var opened = await service.OpenAsync(draft);
        Assert.True(opened.IsIntact);
        Assert.Equal(commit, opened.Manifest.Source!.Revision);
        Assert.Equal("original commit", opened.Project.Description);
    }
}
