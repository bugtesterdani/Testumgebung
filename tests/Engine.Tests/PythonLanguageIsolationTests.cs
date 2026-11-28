using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PythonLanguageIsolationTests
{
    [Theory]
    [InlineData(CodeLanguage.CSharp, false)]
    [InlineData(CodeLanguage.CSharp, true)]
    [InlineData(CodeLanguage.Cpp, false)]
    [InlineData(CodeLanguage.Cpp, true)]
    [InlineData(CodeLanguage.LabView, false)]
    [InlineData(CodeLanguage.LabView, true)]
    [InlineData(CodeLanguage.Python, false)]
    [InlineData(CodeLanguage.Python, true)]
    public async Task Only_python_execution_resolves_the_missing_python_selection(CodeLanguage language, bool libraryCall)
    {
        var runtime = new MissingPythonRuntime();
        var block = new CodeBlockDefinition("library", "Library") { Language = language };
        IStepAction action = libraryCall
            ? new LibraryBlockAction(block.Id, new Dictionary<string, string>(), new Dictionary<string, string>(), TimeSpan.FromSeconds(5))
            : new CodeBlockAction(language, "", TimeSpan.FromSeconds(5));
        var definition = Test(Group("loop").Add(new StepDefinition("code", "Code", action)));
        definition.Library.Add(block);
        definition.PythonEnvironmentId = "id:removed";
        var run = new TestRun(definition, new TestRunOptions { CodeRuntime = runtime, Clock = new VirtualClock(), ExpectedPythonFingerprint = "released" });
        await run.RunAsync().Within();
        var completed = Assert.Single(run.Events.OfType<StepCompleted>());
        Assert.Equal(language == CodeLanguage.Python ? ResultRating.Error : ResultRating.Pass, completed.Rating);
        Assert.Equal(language == CodeLanguage.Python ? 1 : 0, runtime.PythonSelections);
        Assert.Equal(language == CodeLanguage.Python ? 0 : 1, runtime.Invocations);
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("C++")]
    [InlineData("Python")]
    public async Task Inline_editor_build_resolves_python_only_for_python(string language)
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Code, "code", "Code", new Dictionary<string, string> { ["Sprache"] = language });
        var project = new TestProject(Test(Group("loop").Add(step)));
        project.Definition.PythonEnvironmentId = "id:removed";
        var runtime = new MissingPythonRuntime();
        var editor = new EditorViewModel(project) { CodeRuntime = runtime };
        var tools = new CodeToolsViewModel(editor, editor.FindNode("code")!);
        await tools.BuildAsync();
        Assert.Equal(language != "Python", tools.BuildOk);
        Assert.Equal(language == "Python" ? 1 : 0, runtime.PythonSelections);
        Assert.Equal(language == "Python" ? 0 : 1, runtime.Compilations);
    }

    [Theory]
    [InlineData(CodeLanguage.CSharp)]
    [InlineData(CodeLanguage.Cpp)]
    [InlineData(CodeLanguage.LabView)]
    [InlineData(CodeLanguage.Python)]
    public async Task Library_editor_build_and_test_resolve_python_only_for_python(CodeLanguage language)
    {
        var project = new TestProject(Test(Group("loop")));
        project.Definition.PythonEnvironmentId = "id:removed";
        var runtime = new MissingPythonRuntime();
        var editor = new EditorViewModel(project) { CodeRuntime = runtime };
        var block = new CodeBlockDefinition("library", "Library") { Language = language };
        project.Definition.Library.Add(block);
        var tools = new LibraryBlockViewModel(editor, block);
        await tools.BuildAsync();
        await tools.TestAsync();
        Assert.Equal(language != CodeLanguage.Python, tools.BuildOk);
        Assert.StartsWith(language == CodeLanguage.Python ? "ERROR" : "PASS", tools.TestResult);
        Assert.Equal(language == CodeLanguage.Python ? 2 : 0, runtime.PythonSelections);
        Assert.Equal(language == CodeLanguage.Python ? 0 : 1, runtime.Compilations);
        Assert.Equal(language == CodeLanguage.Python ? 0 : 1, runtime.Invocations);
    }

    [Fact]
    public async Task Real_csharp_worker_runs_when_the_saved_python_environment_is_missing()
    {
        using var runtime = new Testumgebung.Workers.CodeRuntime(new Testumgebung.Workers.CodeRuntimeOptions
        { PythonSelection = "id:removed-review-regression" });
        var step = BlockCatalog.Default.Create(BlockCatalog.Code, "code", "C#", new Dictionary<string, string>
        { ["Sprache"] = "C#", ["Quelltext"] = "return Pass();" });
        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions
        { CodeRuntime = runtime, Clock = new VirtualClock(), ExpectedPythonFingerprint = "released-python" });
        await run.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Events.OfType<StepCompleted>()).Rating);
    }

    private sealed class MissingPythonRuntime : ICodeRuntime
    {
        public int PythonSelections { get; private set; }
        public int Compilations { get; private set; }
        public int Invocations { get; private set; }
        public ICodeRuntime WithPythonEnvironment(string? programVenv, string? variableVenv)
        {
            PythonSelections++;
            throw new FileNotFoundException("Selected Python environment was removed.");
        }
        public Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Non-Python execution must not inspect the Python fingerprint.");
        public Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken)
        {
            Compilations++;
            return Task.FromResult(new CompileResult(true, [], "fixture"));
        }
        public Task<CodeResult> InvokeAsync(CodeRequest request, IReadOnlyDictionary<string, object?> values,
            ICodeHost host, TimeSpan abortTimeout, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(new CodeResult(ResultRating.Pass, values));
        }
    }
}
