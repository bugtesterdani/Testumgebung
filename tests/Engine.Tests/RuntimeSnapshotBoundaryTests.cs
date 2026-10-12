using Testumgebung.Domain.Code;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Engine.Tests;

public sealed class RuntimeSnapshotBoundaryTests
{
    private sealed class Runtime : ICodeRuntime
    {
        public List<CodeLanguage> Languages = [];
        public Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken) { Languages.Add(language); return Task.FromResult("fixture-" + language); }
        public Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CodeResult> InvokeAsync(CodeRequest request, IReadOnlyDictionary<string, object?> values, ICodeHost host, TimeSpan abortTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Runtime_snapshot_binds_worker_files_and_every_used_code_language()
    {
        var project = SampleProjects.AcPruefung();
        foreach (var language in Enum.GetValues<CodeLanguage>()) project.Definition.Library.Add(new CodeBlockDefinition(language.ToString(), language.ToString()) { Language = language, Source = "fixture source" });
        var runtime = new Runtime(); var snapshot = await RuntimeSnapshot.CaptureAsync(project, runtime, CancellationToken.None);
        Assert.Equal(Enum.GetValues<CodeLanguage>().Order(), runtime.Languages.Order());
        Assert.Contains(snapshot.Components, c => c.Name == ".NET" && c.Version == Environment.Version.ToString());
        foreach (var language in Enum.GetValues<CodeLanguage>()) Assert.Contains(snapshot.Components, c => c.Name == "CodeRuntime/" + language && c.Version == "fixture-" + language);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "workers"), "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            Assert.Contains(snapshot.Components, c => c.Name == Path.GetRelativePath(AppContext.BaseDirectory, file).Replace('\\', '/') && c.Sha256?.Length == 64);
        Assert.Empty(snapshot.Compare(snapshot));
        await Assert.ThrowsAsync<ReleaseRuleException>(() => RuntimeSnapshot.CaptureAsync(project, null, CancellationToken.None));
    }
}
