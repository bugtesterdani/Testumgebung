using System.Text.Json;
using Microsoft.Win32;
using System.Reflection;
using Testumgebung.Hardware;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PythonPriorityTests
{
    [Fact]
    public async Task Default_switch_is_blocked_by_active_worker_in_run_environment()
    {
        using var runtime = new CodeRuntime();
        var scoped = runtime.WithPythonEnvironment(null, null);
        await scoped.GetRuntimeInfoAsync(CodeLanguage.Python, CancellationToken.None);
        var children = (Dictionary<string, CodeRuntime>)typeof(CodeRuntime).GetField("_pythonEnvironments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
        var python = Assert.Single(children).Value;
        var lease = typeof(CodeRuntime).GetMethod("Lease", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _ = lease.Invoke(python, [CodeLanguage.Python]);
        Assert.Throws<InvalidOperationException>(() => runtime.SelectPython(null));
    }
    [Fact]
    public void Registry_default_supports_venv_folder_legacy_file_and_reset()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "tu-python-registry-" + Guid.NewGuid().ToString("N"));
        var keyPath = @"Software\Testumgebung.Tests\" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        var executable = Path.GetFullPath(Path.Combine(root, "Scripts/python.exe")); File.WriteAllText(executable, "fixture");
        var file = Path.Combine(root, "selection.json");
        var service = new PythonEnvironments(selectionFile: file, registryKey: keyPath);
        try
        {
            Assert.Null(service.LoadSelection());
            service.SaveSelection(executable);
            Assert.Equal(executable, service.LoadSelection());
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true)!)
            {
                Assert.Equal(executable, key.GetValue(PythonEnvironments.RegistryValue));
                key.SetValue(PythonEnvironments.RegistryValue, root);
                Assert.Equal(executable, service.LoadSelection());
                key.SetValue(PythonEnvironments.RegistryValue, Path.Combine(root, "missing"));
            }
            Assert.Equal(executable, service.LoadSelection()); // Legacy file when registry folder was removed.
            service.SaveSelection(null);
            Assert.Null(service.LoadSelection());
            using var cleared = Registry.CurrentUser.OpenSubKey(keyPath)!;
            Assert.Null(cleared.GetValue(PythonEnvironments.RegistryValue));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("program")]
    [InlineData("variable")]
    [InlineData("saved")]
    [InlineData("bundled")]
    [InlineData("empty")]
    [InlineData("relative")]
    [InlineData("no-interpreter")]
    [InlineData("executable")]
    [InlineData("unix")]
    public void Priority_requires_existing_interpreter_and_falls_through(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tu-python-priority-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Candidate(string name, string relative)
            {
                var path = Path.Combine(root, name, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
                return Path.Combine(root, name);
            }
            var program = Candidate("program", "Scripts/python.exe");
            var variable = Candidate("variable", "python.exe");
            var saved = Candidate("saved", "Scripts/python.exe");
            Candidate("workers", "python/runtime/python.exe");
            var missing = Path.Combine(root, "missing");
            var noInterpreter = Path.Combine(root, "empty"); Directory.CreateDirectory(noInterpreter);
            var first = mode switch
            {
                "program" => program, "empty" => "  ", "relative" => "venv",
                "no-interpreter" => noInterpreter, "executable" => Path.Combine(program, "Scripts/python.exe"),
                "unix" => Candidate("unix", "bin/python"), _ => missing,
            };
            var second = mode is "saved" or "bundled" ? missing : variable;
            var third = mode == "bundled" ? null : saved;
            var expected = mode switch
            {
                "program" or "executable" => Path.Combine(program, "Scripts/python.exe"),
                "unix" => Path.Combine(root, "unix/bin/python"),
                "saved" => Path.Combine(saved, "Scripts/python.exe"),
                "bundled" => Path.Combine(root, "workers/python/runtime/python.exe"),
                _ => Path.Combine(variable, "python.exe"),
            };
            Assert.Equal(Path.GetFullPath(expected), PythonEnvironmentResolver.Resolve(Path.Combine(root, "workers"), first, second, third));
            Assert.Null(PythonEnvironmentResolver.Interpreter(null));
            Assert.Null(PythonEnvironmentResolver.Interpreter(missing));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("C#")]
    [InlineData("Python")]
    [InlineData("Baustein")]
    public async Task Selection_transmits_python_environment_without_changing_parameters(string language)
    {
        const string path = @"C:\venvs\Programm A";
        var step = language == "Baustein"
            ? BlockCatalog.Default.Create(BlockCatalog.SelectTest, "select", "Auswahl", new Dictionary<string, string>
            { ["Testkennung"] = "\"Programm A\"", ["PythonVenv"] = "Umgebung" })
            : BlockCatalog.Default.Create(BlockCatalog.Code, "select", "Auswahl", new Dictionary<string, string>
            { ["Sprache"] = language, ["Quelltext"] = language == "C#"
                ? "SelectTest(\"Programm A\", order: Auftrag, pythonVenv: Umgebung); return Pass();"
                : "select_test('Programm A', order=Auftrag, python_venv=Umgebung)\nreturn Pass()" });
        var project = new TestProject(Test(Group("loop").Var("Auftrag", VarType.String).Var("Umgebung", VarType.String, path).Add(step)));
        using var runtime = new CodeRuntime();
        var (value, error) = await TestSelector.RunCustomAsync(project, "A123", "A123", new TestRunOptions { CodeRuntime = runtime, Clock = new VirtualClock() });
        Assert.Null(error); Assert.NotNull(value); Assert.Equal(path, value.PythonVenv);
        var tests = new[] { new AvailableTest("Programm A", "1.0", true, "Test") };
        var outcome = TestSelector.Select(new SelectionConfig { UseCustom = true }, "A123", "A123", tests, value);
        Assert.True(outcome.CanStart); Assert.Equal(path, outcome.PythonVenv);
        Assert.Empty(outcome.Parameters);
    }

    [Fact]
    public async Task Concurrent_runs_keep_environments_separate_and_init_can_set_variable()
    {
        var root = Path.Combine(Path.GetTempPath(), "tu-python-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var runtime = new CodeRuntime();
        try
        {
            var service = new PythonEnvironments(environmentRoot: root, selectionFile: Path.Combine(root, "selection.json"));
            var a = service.Create(); var b = service.Create();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(a)!, "Lib/site-packages/tu_environment.py"), "VALUE='A'");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(b)!, "Lib/site-packages/tu_environment.py"), "VALUE='B'");
            async Task<string> Run(string? program, string? variable, bool init = false, bool wrongType = false)
            {
                var step = BlockCatalog.Default.Create(BlockCatalog.Code, "probe", "Probe", new Dictionary<string, string>
                { ["Sprache"] = "Python", ["Quelltext"] = "import sys\nreturn Pass(sys.executable)" });
                var setup = init ? Group("init").Add(Steps.Code("set", "Set", ctx => { ctx.Set("PythonVenv", variable); return StepOutcome.Pass(); })) : null;
                var definition = Test(Group("loop").Add(step), setup);
                definition.Root.Var("PythonVenv", wrongType ? VarType.Int : VarType.String, wrongType ? 123 : init ? "" : variable);
                var run = new TestRun(definition, new TestRunOptions { CodeRuntime = runtime, PythonVenv = program, Clock = new VirtualClock() });
                await run.RunAsync();
                var result = Assert.Single(run.Steps("probe")); Assert.Equal(ResultRating.Pass, result.Rating);
                return Assert.IsType<string>(result.Value);
            }
            var results = await Task.WhenAll(Run(Path.GetDirectoryName(a), Path.GetDirectoryName(b)), Run(null, Path.GetDirectoryName(b), init: true));
            Assert.Equal(a, results[0], ignoreCase: true); Assert.Equal(b, results[1], ignoreCase: true);
            Assert.Null(runtime.Options.PythonExecutable);
            runtime.SelectPython(a);
            Assert.Equal(a, await Run(Path.Combine(root, "deleted"), "", wrongType: true), ignoreCase: true);
            runtime.SelectPython(null);
            Assert.Equal(CodeRuntimeOptions.DefaultPython, await Run(null, null), ignoreCase: true);
            var stationProject = new TestProject(Test(Group("loop").Add(BlockCatalog.Default.Create(BlockCatalog.Code, "station-probe", "Probe", new Dictionary<string, string>
            { ["Sprache"] = "Python", ["Quelltext"] = "import tu_environment\nreturn Pass(tu_environment.VALUE)" }))));
            var station = new StationViewModel("Station", () => stationProject, new ResourceManager(), clock: new VirtualClock(), codeRuntime: runtime);
            var stationStart = (Task)typeof(StationViewModel).GetMethod("RunProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(station, [stationProject, null, null, null, null, true, false, Path.GetDirectoryName(a)])!;
            await stationStart;
            Assert.Equal(Path.GetDirectoryName(a), station.Run!.Options.PythonVenv);
            Assert.Equal("A", Assert.Single(station.Run.Steps("station-probe")).Value);
            // A package from one environment must never appear in the bundled runtime.
            var bundled = runtime.WithPythonEnvironment(null, null);
            var compile = await bundled.CompileAsync(new CodeRequest(CodeLanguage.Python, "return Pass()", []), CancellationToken.None);
            Assert.True(compile.Success);
            Assert.Contains("Python", await bundled.GetRuntimeInfoAsync(CodeLanguage.Python, CancellationToken.None));
            Assert.True((await bundled.CompileAsync(new CodeRequest(CodeLanguage.CSharp, "return Pass();", []), CancellationToken.None)).Success);
            Assert.Contains(".NET", await bundled.GetRuntimeInfoAsync(CodeLanguage.CSharp, CancellationToken.None));
            runtime.Dispose();
            Assert.Throws<ObjectDisposedException>(() => runtime.WithPythonEnvironment(null, null));
        }
        finally { runtime.Dispose(); Directory.Delete(root, true); }
    }
}
