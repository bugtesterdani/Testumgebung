using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Engine.Blocks;
using Testumgebung.Presentation.ViewModels;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class PythonEnvironmentTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "tu-python-env-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("missing")]
    [InlineData("broken")]
    [InlineData("relative")]
    [InlineData("removed")]
    [InlineData("valid")]
    [InlineData("null")]
    public void Saved_selection_is_validated_and_can_be_reset(string mode)
    {
        var root = Temp(); Directory.CreateDirectory(root);
        var config = Path.Combine(root, "selection.json"); var executable = Path.Combine(root, "python.exe");
        var service = new PythonEnvironments(environmentRoot: root, selectionFile: config);
        try
        {
            File.WriteAllText(executable, "fixture");
            if (mode == "broken") File.WriteAllText(config, "bad json");
            if (mode == "relative") File.WriteAllText(config, "\"python.exe\"");
            if (mode is "removed" or "valid") service.SaveSelection(executable);
            if (mode == "removed") File.Delete(executable);
            if (mode == "null") service.SaveSelection(null);
            Assert.Equal(mode == "valid" ? executable : null, service.LoadSelection());
            Assert.Throws<FileNotFoundException>(() => service.SaveSelection("relative.exe"));
            Assert.Throws<FileNotFoundException>(() => service.SaveSelection(Path.Combine(root, "missing.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_bundle_and_console_fail_with_status_without_switching_runtime()
    {
        var root = Temp(); Directory.CreateDirectory(root);
        try
        {
            var service = new PythonEnvironments(root, root, Path.Combine(root, "selection.json"));
            Assert.Throws<FileNotFoundException>(service.Create);
            Assert.Throws<FileNotFoundException>(() => PythonEnvironments.ConsoleStartInfo(Path.Combine(root, "missing.exe")));
            var vm = new PythonEnvironmentViewModel(null, () => false, service, _ => throw new InvalidOperationException("must not open"));
            Assert.False(vm.UseEnvironmentCommand.CanExecute(null));
            Assert.False(vm.UseBundledCommand.CanExecute(null));
            Assert.Contains("Keine", vm.ActiveInterpreter);
            vm.OpenConsole(); vm.Select(null);
            await vm.CreateAsync();
            Assert.Contains("konnte nicht", vm.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Writable_copy_supports_offline_pip_and_package_use_in_real_worker()
    {
        var root = Temp(); Directory.CreateDirectory(root);
        var service = new PythonEnvironments(environmentRoot: root, selectionFile: Path.Combine(root, "selection.json"));
        using var runtime = new CodeRuntime();
        try
        {
            var executable = service.Create();
            var directory = Path.GetDirectoryName(executable)!;
            Assert.True(File.Exists(Path.Combine(directory, "BEFEHLE.txt")));
            Assert.Contains("import site", File.ReadAllText(Path.Combine(directory, "python314._pth")));
            var start = PythonEnvironments.ConsoleStartInfo(executable);
            Assert.Equal(directory, start.WorkingDirectory);
            Assert.Contains(Path.Combine(directory, "Konsole.ps1"), start.ArgumentList);
            Assert.False(start.CreateNoWindow);
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.ArgumentList.Remove("-NoExit");
            using (var console = Process.Start(start)!)
            {
                var consoleOutput = console.StandardOutput.ReadToEndAsync();
                var consoleError = console.StandardError.ReadToEndAsync();
                await console.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(console.ExitCode == 0, await consoleError);
                Assert.Contains("26.2.1", await consoleOutput);
            }
            Assert.Contains("26.2.1", await Python(executable, "-m", "pip", "--version"));
            var wheel = Path.Combine(directory, "tu_console_probe-1.0-py3-none-any.whl");
            using (var zip = ZipFile.Open(wheel, ZipArchiveMode.Create))
            {
                Add(zip, "tu_console_probe.py", "VALUE = 42\n");
                Add(zip, "tu_console_probe-1.0.dist-info/METADATA", "Metadata-Version: 2.1\nName: tu-console-probe\nVersion: 1.0\n");
                Add(zip, "tu_console_probe-1.0.dist-info/WHEEL", "Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
                Add(zip, "tu_console_probe-1.0.dist-info/RECORD", "tu_console_probe.py,,\ntu_console_probe-1.0.dist-info/METADATA,,\ntu_console_probe-1.0.dist-info/WHEEL,,\ntu_console_probe-1.0.dist-info/RECORD,,\n");
            }
            Assert.Contains("Successfully installed", await Python(executable, "-m", "pip", "install", "--no-index", "--disable-pip-version-check", wheel));
            service.SaveSelection(executable);
            Assert.Equal(executable, service.LoadSelection());
            await runtime.GetRuntimeInfoAsync(CodeLanguage.Python, CancellationToken.None);
            runtime.SelectPython(executable);
            var step = BlockCatalog.Default.Create(BlockCatalog.Code, "probe", "Probe", new Dictionary<string, string>
            { ["Sprache"] = "Python", ["Quelltext"] = "import tu_console_probe\nreturn Pass(tu_console_probe.VALUE)" });
            var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { CodeRuntime = runtime, Clock = new VirtualClock() });
            await run.RunAsync().Within();
            var completed = Assert.Single(run.Events.OfType<StepCompleted>());
            Assert.Equal(ResultRating.Pass, completed.Rating);
            Assert.Equal(42L, System.Convert.ToInt64(completed.Value));
            runtime.SelectPython(null);
            Assert.Null(runtime.Options.PythonExecutable);
        }
        finally { runtime.Dispose(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Viewmodel_creates_console_guards_switch_and_returns_to_bundle()
    {
        var root = Temp(); Directory.CreateDirectory(root);
        var service = new PythonEnvironments(environmentRoot: root, selectionFile: Path.Combine(root, "selection.json"));
        using var runtime = new CodeRuntime();
        var running = true; var opened = new List<ProcessStartInfo>();
        var vm = new PythonEnvironmentViewModel(runtime, () => running, service, opened.Add);
        try
        {
            await vm.CreateAsync();
            Assert.Single(opened);
            Assert.Contains("pip install", vm.Commands);
            vm.UseEnvironmentCommand.Execute(null);
            Assert.Contains("zuerst", vm.Status);
            Assert.Null(runtime.Options.PythonExecutable);
            running = false;
            vm.UseEnvironmentCommand.Execute(null);
            Assert.Equal(service.LoadSelection(), runtime.Options.PythonExecutable);
            Assert.Equal(runtime.Options.PythonExecutable, vm.ActiveInterpreter);
            vm.UseEnvironmentCommand.Execute(null);
            vm.UseBundledCommand.Execute(null);
            Assert.Null(service.LoadSelection());
            Assert.Null(runtime.Options.PythonExecutable);
            Assert.Contains("mitgelieferte", vm.Status);
            var interpreter = Path.Combine(vm.EnvironmentPath, "python.exe");
            File.Delete(interpreter);
            vm.UseEnvironmentCommand.Execute(null); Assert.Contains("entfernt", vm.Status);
            vm.OpenConsole(); Assert.Contains("nicht geöffnet", vm.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Interpreter_switch_rejects_leased_worker()
    {
        using var runtime = new CodeRuntime();
        await runtime.GetRuntimeInfoAsync(CodeLanguage.Python, CancellationToken.None);
        var lease = typeof(CodeRuntime).GetMethod("Lease", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _ = lease.Invoke(runtime, [CodeLanguage.Python]);
        Assert.Throws<InvalidOperationException>(() => runtime.SelectPython(null));
        runtime.Dispose();
        Assert.Throws<ObjectDisposedException>(() => runtime.SelectPython(null));
    }

    [Fact]
    public async Task Dead_idle_worker_does_not_hide_an_active_worker_during_switch()
    {
        using var runtime = new CodeRuntime();
        await runtime.GetRuntimeInfoAsync(CodeLanguage.CSharp, CancellationToken.None);
        var lease = typeof(CodeRuntime).GetMethod("Lease", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var giveBack = typeof(CodeRuntime).GetMethod("Return", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dead = lease.Invoke(runtime, [CodeLanguage.CSharp])!;
        giveBack.Invoke(runtime, [CodeLanguage.CSharp, dead]);
        ((IDisposable)dead).Dispose();
        var all = typeof(CodeRuntime).GetField("_all", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
        var count = all.GetType().GetProperty("Count")!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((int)count.GetValue(all)! != 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(0, (int)count.GetValue(all)!);
        _ = lease.Invoke(runtime, [CodeLanguage.Python]);
        Assert.Throws<InvalidOperationException>(() => runtime.SelectPython(null));
    }

    [Theory]
    [InlineData("save-failure")]
    [InlineData("console-io")]
    [InlineData("console-win32")]
    [InlineData("loaded")]
    public async Task Environment_ui_reports_failures_and_restores_previous_selection(string mode)
    {
        var root = Temp(); Directory.CreateDirectory(root);
        var workers = Path.Combine(root, "workers"); var source = Path.Combine(workers, "python", "runtime");
        Directory.CreateDirectory(Path.Combine(source, "pip-bootstrap"));
        foreach (var file in new[] { "python.exe", "python314.dll", "python314.zip", "LICENSE.txt", "pip-bootstrap/pip.whl" }) File.WriteAllText(Path.Combine(source, file), "fixture");
        var config = Path.Combine(root, "config.json");
        var service = new PythonEnvironments(workers, root, config);
        using var runtime = new CodeRuntime(new CodeRuntimeOptions { PythonExecutable = Path.Combine(source, "python.exe") });
        try
        {
            if (mode == "loaded") service.SaveSelection(service.Create());
            if (mode == "save-failure") Directory.CreateDirectory(config);
            var vm = new PythonEnvironmentViewModel(runtime, () => false, service, _ =>
            {
                if (mode == "console-io") throw new IOException("console fixture");
                if (mode == "console-win32") throw new System.ComponentModel.Win32Exception("console fixture");
            });
            if (mode == "loaded") Assert.DoesNotContain("Noch keine", vm.EnvironmentPath);
            await vm.CreateAsync();
            if (mode.StartsWith("console")) Assert.Contains("nicht geöffnet", vm.Status);
            vm.UseEnvironmentCommand.Execute(null);
            if (mode == "save-failure")
            {
                Assert.Equal(Path.Combine(source, "python.exe"), runtime.Options.PythonExecutable);
                Assert.Contains("nicht gewechselt", vm.Status);
            }
            else Assert.Equal(service.LoadSelection(), runtime.Options.PythonExecutable);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content);
    }

    private static async Task<string> Python(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = "";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
