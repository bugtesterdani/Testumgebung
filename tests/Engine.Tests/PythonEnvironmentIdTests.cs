using System.IO.Compression;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PythonEnvironmentIdTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "tu-python-id-" + Guid.NewGuid().ToString("N"));
    private static PythonEnvironments Catalog(string root) => new(environmentRoot: root, selectionFile: Path.Combine(root, "selection.json"));
    private static string Fixture(string root, PythonEnvironments service)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        foreach (var file in new[] { "python.exe", "python315.dll", "python315.zip", "python315._pth", "LICENSE.txt", "pip-bootstrap/pip.whl" })
        {
            var path = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        var executable = Path.Combine(directory, "python.exe");
        service.EnsureId(executable);
        return executable;
    }

    [Fact]
    public void Multiple_environments_export_ids_and_restore_to_another_station_without_paths()
    {
        var root = Temp();
        try
        {
            var first = Catalog(Path.Combine(root, "station-a"));
            var a = Fixture(Path.Combine(root, "station-a"), first);
            var b = Fixture(Path.Combine(root, "station-a"), first);
            var id = first.EnsureId(a);
            Assert.NotEqual(id, first.EnsureId(b));
            Assert.Equal(2, first.List().Count);
            first.SaveSelection(a);
            Assert.DoesNotContain(root, File.ReadAllText(Path.Combine(root, "station-a", "selection.json")));
            var zip = Path.Combine(root, "python.zip");
            first.Export(a, zip);
            using (var archive = ZipFile.OpenRead(zip))
            using (var reader = new StreamReader(archive.GetEntry(PythonEnvironments.ManifestFile)!.Open()))
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(id), reader.ReadToEnd());
            var second = Catalog(Path.Combine(root, "station-b"));
            var imported = second.Import(zip);
            Assert.NotEqual(a, imported);
            Assert.Equal(id, second.EnsureId(imported));
            Assert.Equal(imported, PythonEnvironmentResolver.Resolve("workers", "id:" + id, b, a, second));
            Assert.Throws<InvalidDataException>(() => second.Import(zip));
            Assert.Single(second.List());
            File.Delete(a);
            Assert.Equal("id:" + id, first.LoadSelection());
            Assert.Throws<FileNotFoundException>(() => PythonEnvironmentResolver.Resolve("workers", first.LoadSelection(), b, b, first));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("id:missing")]
    [InlineData(" id:missing ")]
    [InlineData("id:../escape")]
    [InlineData("bundled")]
    public void Explicit_missing_selection_never_falls_back(string selection)
    {
        var root = Temp();
        try
        {
            var service = Catalog(root);
            var fallback = Fixture(root, service);
            Assert.Throws<FileNotFoundException>(() => PythonEnvironmentResolver.Resolve(Path.Combine(root, "workers"), selection, fallback, fallback, service));
            Assert.Throws<FileNotFoundException>(() => PythonEnvironmentResolver.Resolve(Path.Combine(root, "workers"), null, selection, fallback, service));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Explicit_bundle_overrides_existing_paths_and_UI_lists_both_copies()
    {
        var root = Temp();
        try
        {
            var service = Catalog(root);
            var first = Fixture(root, service);
            var second = Fixture(root, service);
            var bundled = Path.Combine(root, "workers", "python", "runtime", "python.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
            File.WriteAllText(bundled, "bundle");
            Assert.Equal(bundled, PythonEnvironmentResolver.Resolve(Path.Combine(root, "workers"), "bundled", first, second, service));
            using var runtime = new CodeRuntime();
            var vm = new PythonEnvironmentViewModel(runtime, () => false, service);
            Assert.Equal(2, vm.Environments.Count);
            vm.SelectedEnvironment = vm.Environments.Single(item => item.Executable == second);
            Assert.Equal(Path.GetDirectoryName(second), vm.EnvironmentPath);
            Assert.Equal("id:" + service.EnsureId(second), vm.EnvironmentId);
            vm.Select(second);
            Assert.Equal(vm.EnvironmentId, runtime.Options.PythonSelection);
            Assert.Equal(vm.EnvironmentId, service.LoadSelection(preserveReference: true));
            File.Delete(second);
            Assert.Throws<FileNotFoundException>(() => runtime.WithPythonEnvironment(null, null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bundled")]
    [InlineData("id:0123456789abcdef0123456789abcdef")]
    public void Project_selection_survives_serialization_cloning_and_release_package(string? selection)
    {
        var project = new TestProject(Test(Group("loop")));
        project.Definition.PythonEnvironmentId = selection;
        var serializer = new ProjectSerializer();
        Assert.Equal(selection, serializer.Clone(project).Definition.PythonEnvironmentId);
        var (bytes, _) = ReleasePackage.Build(project, "developer", DateTimeOffset.UtcNow, serializer);
        var opened = ReleasePackage.Open(bytes, serializer);
        Assert.True(opened.IsIntact, string.Join(";", opened.Problems));
        Assert.Equal(selection, opened.Project.Definition.PythonEnvironmentId);
        var changed = serializer.Clone(project);
        changed.Definition.PythonEnvironmentId = "id:another";
        Assert.Contains(ProjectDiff.Compare(project, changed), item => item.Item == "Python-Umgebung");
    }

    [Fact]
    public async Task Fingerprint_uses_project_selection_and_selection_program_override()
    {
        var definition = Test(Group("loop").Add(BlockCatalog.Default.Create(BlockCatalog.Code, "python", "Python",
            new Dictionary<string, string> { ["Sprache"] = "Python", ["Quelltext"] = "return Pass()" })));
        definition.PythonEnvironmentId = "id:production";
        var runtime = new SelectionRuntime();
        var project = new TestProject(definition);
        var expected = await RuntimeSnapshot.CaptureAsync(project, runtime, default);
        Assert.Equal("id:production", expected.Components.Single(item => item.Name == "CodeRuntime/Python").Version);
        var actual = await RuntimeSnapshot.CaptureAsync(project, runtime, default, "id:different");
        Assert.Contains(expected.Compare(actual), item => item.Contains("CodeRuntime/Python"));
        Assert.Empty(expected.Compare(await RuntimeSnapshot.CaptureAsync(project, runtime, default)));
    }

    [Fact]
    public async Task Runtime_variable_change_cannot_bypass_released_fingerprint()
    {
        var runtime = new SelectionRuntime();
        var definition = Test(Group("loop").Add(BlockCatalog.Default.Create(BlockCatalog.Code, "python", "Python",
            new Dictionary<string, string> { ["Sprache"] = "Python", ["Quelltext"] = "return Pass()" })));
        definition.Root.Var("PythonVenv", VarType.String, "id:changed");
        var run = new TestRun(definition, new TestRunOptions { CodeRuntime = runtime, ExpectedPythonFingerprint = "id:released", Clock = new VirtualClock() });
        await run.RunAsync().Within();
        Assert.Equal(ResultRating.Error, Assert.Single(run.Events.OfType<StepCompleted>()).Rating);
        Assert.Equal(0, runtime.Invocations);
    }

    [Fact]
    public async Task Same_exported_interpreter_has_same_fingerprint_at_different_paths()
    {
        var root = Temp();
        try
        {
            var a = Catalog(Path.Combine(root, "a"));
            var executable = a.Create();
            var zip = Path.Combine(root, "environment.zip");
            a.Export(executable, zip);
            var b = Catalog(Path.Combine(root, "b"));
            var imported = b.Import(zip);
            using var first = new CodeRuntime(new CodeRuntimeOptions { PythonExecutable = executable });
            using var second = new CodeRuntime(new CodeRuntimeOptions { PythonExecutable = imported });
            var fingerprint = await first.GetRuntimeInfoAsync(CodeLanguage.Python, default);
            Assert.Equal(fingerprint, await second.GetRuntimeInfoAsync(CodeLanguage.Python, default));
            Assert.DoesNotContain(root, fingerprint);
            Assert.Equal(a.EnsureId(executable), b.EnsureId(imported));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Parallel_programs_resolve_distinct_ids_without_changing_the_global_runtime()
    {
        var root = Temp();
        using var runtime = new CodeRuntime(new CodeRuntimeOptions { EnvironmentCatalog = Catalog(root) });
        try
        {
            var catalog = Catalog(root);
            var a = catalog.Create();
            var b = catalog.Create();
            async Task<string> Execute(string executable)
            {
                var step = BlockCatalog.Default.Create(BlockCatalog.Code, "python", "Python", new Dictionary<string, string>
                { ["Sprache"] = "Python", ["Quelltext"] = "import sys\nreturn Pass(sys.executable)" });
                var definition = Test(Group("loop").Add(step));
                definition.PythonEnvironmentId = "id:" + catalog.EnsureId(executable);
                var run = new TestRun(definition, new TestRunOptions { CodeRuntime = runtime, Clock = new VirtualClock() });
                await run.RunAsync().Within();
                var completed = Assert.Single(run.Events.OfType<StepCompleted>());
                Assert.Equal(ResultRating.Pass, completed.Rating);
                return Assert.IsType<string>(completed.Value);
            }
            var results = await Task.WhenAll(Execute(a), Execute(b));
            Assert.Equal(a, results[0]);
            Assert.Equal(b, results[1]);
            Assert.Null(runtime.Options.PythonExecutable);
            Assert.Throws<FileNotFoundException>(() => runtime.WithPythonEnvironment("id:absent", a));
        }
        finally
        {
            runtime.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Release_verification_compares_the_override_to_the_recorded_project_environment()
    {
        var root = Temp();
        try
        {
            var definition = Test(Group("loop").Add(BlockCatalog.Default.Create(BlockCatalog.Code, "python", "Python",
                new Dictionary<string, string> { ["Sprache"] = "Python", ["Quelltext"] = "return Pass()" })));
            definition.PythonEnvironmentId = "id:approved";
            var service = new ReleaseService(new FolderReleaseStore(root)) { RequireRuntimeSnapshot = true, CodeRuntime = new SelectionRuntime() };
            var draft = await service.CreateDraftAsync(new TestProject(definition), "2.0", new Actor("developer", UserRights.Develop));
            Assert.Empty(await service.VerifyEnvironmentAsync(draft));
            Assert.Contains(await service.VerifyEnvironmentAsync(draft, programPython: "id:other"), item => item.Contains("CodeRuntime/Python"));
            var package = await service.OpenAsync(draft);
            Assert.Equal("id:approved", package.Project.Definition.PythonEnvironmentId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_archives_get_a_new_id_and_malformed_manifests_are_rejected(bool malformed)
    {
        var root = Temp();
        try
        {
            var source = Catalog(Path.Combine(root, "source"));
            var executable = Fixture(Path.Combine(root, "source"), source);
            var manifest = Path.Combine(Path.GetDirectoryName(executable)!, PythonEnvironments.ManifestFile);
            File.Delete(manifest);
            if (malformed) File.WriteAllText(manifest, "not json");
            var zip = Path.Combine(root, "old.zip");
            ZipFile.CreateFromDirectory(Path.GetDirectoryName(executable)!, zip);
            var target = Catalog(Path.Combine(root, "target"));
            if (malformed)
            {
                Assert.Throws<InvalidDataException>(() => target.Import(zip));
                Assert.Empty(target.List());
            }
            else
            {
                var imported = target.Import(zip);
                Assert.True(Guid.TryParseExact(target.EnsureId(imported), "N", out _));
                Assert.Single(target.List());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Project_field_treats_bare_missing_id_as_explicit_and_legacy_override_can_fall_through()
    {
        var definition = Test(Group("loop"));
        definition.PythonEnvironmentId = "unknown-id";
        Assert.Equal("id:unknown-id", definition.PythonEnvironmentId);
        Assert.Equal("id:unknown-id", PythonEnvironmentResolver.ProgramSelection("missing-legacy-folder", definition.PythonEnvironmentId));
        Assert.Equal("id:missing", PythonEnvironmentResolver.ProgramSelection("id:missing", "bundled"));
    }

    private sealed class SelectionRuntime(string selection = "default", SelectionRuntime? owner = null) : ICodeRuntime
    {
        public int Invocations { get; private set; }
        public ICodeRuntime WithPythonEnvironment(string? programVenv, string? variableVenv) => new SelectionRuntime(programVenv ?? variableVenv ?? "default", this);
        public Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken) => Task.FromResult(selection);
        public Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeResult> InvokeAsync(CodeRequest request, IReadOnlyDictionary<string, object?> values, ICodeHost host, TimeSpan abortTimeout, CancellationToken cancellationToken)
        {
            (owner ?? this).Invocations++;
            throw new InvalidOperationException("Must reject before invocation");
        }
    }
}
