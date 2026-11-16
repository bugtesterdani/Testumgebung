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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restored_file_overrides_newer_registry_selection(bool reset)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Temp();
        Directory.CreateDirectory(root);
        var registry = @"Software\Testumgebung.Tests\" + Guid.NewGuid().ToString("N");
        var config = Path.Combine(root, "selection.json");
        var old = Path.Combine(root, "old", "python.exe");
        var current = Path.Combine(root, "current", "python.exe");
        try
        {
            foreach (var executable in new[] { old, current })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
                File.WriteAllText(executable, "fixture");
            }
            var service = new PythonEnvironments(selectionFile: config, registryKey: registry);
            service.SaveSelection(reset ? null : old);
            var backup = File.ReadAllBytes(config);
            service.SaveSelection(current);
            File.Delete(config);
            Assert.Equal(current, service.LoadSelection());
            File.WriteAllBytes(config, backup);
            Assert.Equal(reset ? null : old, service.LoadSelection());
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry, false);
            Directory.Delete(root, true);
        }
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
        finally { await DeleteTestDirectoryAsync(root); }
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
            Assert.Contains("import site", File.ReadAllText(Path.Combine(directory, "python315._pth")));
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
                // A fresh CI runner may still scan/load the copied interpreter and PowerShell.
                Assert.Contains("26.3", await CompleteTestProcessAsync(console, TimeSpan.FromSeconds(60)));
            }
            Assert.Contains("26.3", await Python(executable, "-m", "pip", "--version"));
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
            var archive = Path.Combine(root, "environment.zip");
            var vm = new PythonEnvironmentViewModel(runtime, () => false, service);
            await vm.ExportAsync(archive);
            Assert.True(File.Exists(archive), vm.Status);
            await vm.ImportAsync(archive);
            Assert.Contains("importiert", vm.Status);
            Assert.NotEqual(directory, vm.EnvironmentPath);
            Assert.Equal(executable, service.LoadSelection());
            Assert.Null(runtime.Options.PythonExecutable);
            var imported = Path.Combine(vm.EnvironmentPath, "python.exe");
            Assert.Equal(vm.EnvironmentPath, PythonEnvironments.ConsoleStartInfo(imported).WorkingDirectory);

            await runtime.GetRuntimeInfoAsync(CodeLanguage.Python, CancellationToken.None);
            vm.UseEnvironmentCommand.Execute(null);
            Assert.Equal(imported, runtime.Options.PythonExecutable);
            Assert.Equal(imported, service.LoadSelection());
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
        finally { runtime.Dispose(); await DeleteTestDirectoryAsync(root); }
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
        finally { await DeleteTestDirectoryAsync(root); }
    }

    [Fact]
    public async Task Invalid_zip_does_not_leave_a_partial_environment_or_change_selection()
    {
        var root = Temp();
        Directory.CreateDirectory(root);
        try
        {
            var environments = Path.Combine(root, "environments");
            var service = new PythonEnvironments(environmentRoot: environments, selectionFile: Path.Combine(root, "selection.json"));
            var vm = new PythonEnvironmentViewModel(null, () => false, service);
            var archive = Path.Combine(root, "invalid.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                Add(zip, "../escape.txt", "outside");
            await vm.ImportAsync(archive);
            Assert.Contains("konnte nicht", vm.Status);
            Assert.Empty(Directory.GetDirectories(environments));
            Assert.False(File.Exists(Path.Combine(environments, "escape.txt")));
            Assert.Null(service.LoadSelection());
            await vm.ExportAsync(Path.Combine(root, "export.zip"));
            Assert.Contains("konnte nicht", vm.Status);
        }
        finally { await DeleteTestDirectoryAsync(root); }
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
        foreach (var file in new[] { "python.exe", "python315.dll", "python315.zip", "LICENSE.txt", "pip-bootstrap/pip.whl" }) File.WriteAllText(Path.Combine(source, file), "fixture");
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
        finally { await DeleteTestDirectoryAsync(root); }
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
        return await CompleteTestProcessAsync(process, TimeSpan.FromSeconds(30));
    }

    /// <summary>Every test-owned process is reaped, including the timeout path, before its working directory is removed.</summary>
    private static async Task<string> CompleteTestProcessAsync(Process process, TimeSpan timeout)
    {
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task DeleteTestDirectoryAsync(string directory, TimeSpan? retryWindow = null)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(full);
        if (!string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("tu-python-env-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Nur den eigenen temporären Python-Testordner bereinigen.");
        var timer = Stopwatch.StartNew(); var limit = retryWindow ?? TimeSpan.FromSeconds(5);
        while (true)
        {
            try { Directory.Delete(full, true); return; }
            catch (DirectoryNotFoundException) { return; }
            // Retry only real Windows sharing/lock violations; timeout and unrelated IO errors stay visible.
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && timer.Elapsed < limit)
            { await Task.Delay(25); }
        }
    }

    [Fact]
    public async Task Timed_out_test_process_is_dead_before_its_working_directory_is_removed()
    {
        var root = Temp(); Directory.CreateDirectory(root);
        var executable = Path.Combine(AppContext.BaseDirectory, "workers", "python", "runtime", "python.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("import time; time.sleep(60)");
        try
        {
            using var process = Process.Start(start)!;
            await Assert.ThrowsAsync<TimeoutException>(() => CompleteTestProcessAsync(process, TimeSpan.FromMilliseconds(100)));
            Assert.True(process.HasExited);
            await DeleteTestDirectoryAsync(root); Assert.False(Directory.Exists(root));
        }
        finally { await DeleteTestDirectoryAsync(root); }
    }

    [Fact]
    public async Task Cleanup_waits_for_a_sharing_lock_and_does_not_hide_a_permanent_lock_or_delete_other_paths()
    {
        var root = Temp(); Directory.CreateDirectory(root);
        try
        {
            Task cleanup;
            using (var locked = new FileStream(Path.Combine(root, "locked"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => DeleteTestDirectoryAsync(root, TimeSpan.Zero));
                cleanup = DeleteTestDirectoryAsync(root); Assert.False(cleanup.IsCompleted);
            }
            await cleanup.Within(); Assert.False(Directory.Exists(root));
            await DeleteTestDirectoryAsync(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteTestDirectoryAsync(Path.GetTempPath()));
        }
        finally { await DeleteTestDirectoryAsync(root); }
    }

}
