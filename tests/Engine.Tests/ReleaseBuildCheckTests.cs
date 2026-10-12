using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Presentation.Releases;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ReleaseBuildCheckTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("no-runtime")]
    [InlineData("success")]
    [InlineData("errors")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task Release_review_builds_every_code_artifact_and_handles_failures(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-build-review-" + Guid.NewGuid().ToString("N"));
        var hub = new ReleaseHub(new ReleaseService(new FolderReleaseStore(directory)));
        try
        {
            var loop = Group("loop"); var project = new TestProject(Test(loop));
            if (mode != "empty")
            {
                var library = new CodeBlockDefinition("library", "Auswertung") { Source = "library-source", Language = CodeLanguage.Python };
                library.Ports.Add(new CodePort("Auftrag", VarType.String, PortDirection.Input)); project.Definition.Library.Add(library);
                loop.Add(BlockCatalog.Default.Create(BlockCatalog.Code, "code", "Testcode", new Dictionary<string, string> { ["Quelltext"] = "flow-source" }));
                loop.Add(BlockCatalog.Default.Create(BlockCatalog.Code, "default", "Defaultcode", new Dictionary<string, string> { ["Sprache"] = "Python" }));
                var fallback = (StepDefinition)loop.Children[1]; fallback.Parameters.Remove("Quelltext");
                ((StepDefinition)loop.Children[0]).Parameters.Remove("Sprache");
                project.Definition.Assemblies.Add(new ManagedAssembly("fixture.dll", [1, 2, 3]));
            }
            var runtime = new ReviewRuntime(mode);
            var shell = new ShellViewModel(new QueueDispatcher(), project: project, codeRuntime: mode == "no-runtime" ? null : runtime, releases: hub);
            var method = typeof(ReleasesViewModel).GetMethod("BuildCheckAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var cancellation = new CancellationTokenSource();
            var task = (Task<IReadOnlyList<ReviewCheck>>)method.Invoke(shell.ReleasesPage, [project, cancellation.Token])!;
            if (mode == "cancel") { await Assert.ThrowsAsync<OperationCanceledException>(() => task); Assert.Single(runtime.Requests); return; }
            var checks = await task;
            if (mode is "empty" or "no-runtime")
            {
                Assert.Single(checks); Assert.Empty(runtime.Requests); Assert.Equal(mode == "empty", checks[0].Passed); return;
            }
            Assert.Equal(3, checks.Count); Assert.Equal(3, runtime.Requests.Count);
            Assert.Equal(new[] { CodeLanguage.Python, CodeLanguage.CSharp, CodeLanguage.Python }, runtime.Requests.Select(r => r.Language));
            Assert.Equal(new[] { "library-source", "flow-source", "" }, runtime.Requests.Select(r => r.Source));
            Assert.All(runtime.Requests, r => Assert.Same(project.Definition.Assemblies, r.Assemblies));
            Assert.Equal("Auftrag", Assert.Single(runtime.Requests[0].Variables).Name);
            Assert.All(checks, check => Assert.Equal(mode == "success", check.Passed));
            if (mode == "success") Assert.All(checks, check => Assert.Contains("Build OK (fixture)", check.Detail));
            if (mode == "exception") Assert.All(checks, check => Assert.Equal("compiler unavailable", check.Detail));
            if (mode == "errors") Assert.All(checks, check => { Assert.Contains("error-2", check.Detail); Assert.DoesNotContain("warning", check.Detail); Assert.DoesNotContain("error-3", check.Detail); });
        }
        finally { await hub.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class ReviewRuntime(string mode) : ICodeRuntime
    {
        public List<CodeRequest> Requests { get; } = [];
        public Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (mode == "exception") throw new IOException("compiler unavailable");
            if (mode == "cancel") throw new OperationCanceledException(cancellationToken);
            var diagnostics = new[] { new CodeDiagnostic(1, 1, "warning", "warning") }.Concat(Enumerable.Range(0, 4).Select(i => new CodeDiagnostic(i + 1, 2, "error", "error-" + i))).ToArray();
            return Task.FromResult(new CompileResult(mode == "success", diagnostics, "fixture"));
        }
        public Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken) => Task.FromResult("fixture");
        public Task<CodeResult> InvokeAsync(CodeRequest request, IReadOnlyDictionary<string, object?> values, ICodeHost host, TimeSpan abortTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
