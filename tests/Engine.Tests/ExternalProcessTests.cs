using System.Diagnostics;
using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Processes;
using Testumgebung.Engine.Blocks;
using Testumgebung.Workers;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ExternalProcessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "process-tests-" + Guid.NewGuid().ToString("N"));
    private static string Workers => Path.Combine(AppContext.BaseDirectory, "workers");
    private static string Probe(string extension = ".exe") => Path.Combine(AppContext.BaseDirectory, "fixtures", "process", "ProcessProbe" + extension);
    public ExternalProcessTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        // Windows may signal process exit just before releasing its cwd handle.
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_directory, recursive: true); return; }
            catch (IOException) when (attempt < 100) { Thread.Sleep(10); }
        }
    }
    private ProcessLaunch Request(string[] args, bool bound = true, ExternalProcessKind kind = ExternalProcessKind.Executable)
        => new(kind, Probe(kind is ExternalProcessKind.ManagedEntryPoint or ExternalProcessKind.ManagedMain ? ".dll" : ".exe"), args, _directory,
            new Dictionary<string, string> { ["PROBE_VALUE"] = "Umgebung ü 漢字" }, Bound: bound);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Executable_arguments_environment_cwd_streams_and_exit_code_are_preserved(bool bound)
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request(["echo", "7", "space and \"quote\"", "Ü漢字", "", "& echo unchanged"], bound), "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.True(result.Completed); Assert.True(result.ExitCode == 7, result.StandardError); Assert.Null(result.HostError); Assert.False(result.Stopped);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(new[] { "space and \"quote\"", "Ü漢字", "", "& echo unchanged" }, json.RootElement.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(_directory, json.RootElement.GetProperty("WorkingDirectory").GetString());
        Assert.Equal("Umgebung ü 漢字", json.RootElement.GetProperty("Environment").GetString());
        Assert.Equal("probe stderr", result.StandardError);
    }
    [Theory]
    [InlineData(ExternalProcessKind.ManagedEntryPoint, "")]
    [InlineData(ExternalProcessKind.ManagedMain, "ProcessProbe.Methods")]
    public async Task Managed_entry_and_public_Main_execute_in_the_bundled_host(ExternalProcessKind kind, string type)
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request(["echo", "9", "dll argument"], kind: kind) with { TypeName = type }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(9, result.ExitCode); Assert.Null(result.HostError); Assert.Contains("dll argument", result.StandardOutput);
    }
    [Theory]
    [InlineData("ProcessProbe.NoArguments", 7, "")]
    [InlineData("ProcessProbe.VoidMain", 0, "void")]
    [InlineData("ProcessProbe.TaskMain", 0, "task")]
    public async Task Supported_parameterless_Main_returns_are_mapped(string type, int code, string text)
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request([], kind: ExternalProcessKind.ManagedMain) with { TypeName = type }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(code, result.ExitCode); Assert.Null(result.HostError); Assert.Equal(text, result.StandardOutput);
    }
    [Theory]
    [InlineData("ProcessProbe.InvalidMain")]
    [InlineData("ProcessProbe.AmbiguousMain")]
    [InlineData("ProcessProbe.NotPresent")]
    public async Task Invalid_or_ambiguous_DLL_methods_report_host_error(string type)
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request([], kind: ExternalProcessKind.ManagedMain) with { TypeName = type }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.NotNull(result.HostError); Assert.Equal(253, result.ExitCode);
    }
    [Fact]
    public async Task DLL_exception_is_distinguished_from_an_application_return_code()
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request(["throw"], kind: ExternalProcessKind.ManagedMain) with { TypeName = "ProcessProbe.Methods" }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal("fixture invocation failed", result.HostError);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streams_are_drained_concurrently_and_retention_is_bounded(bool bound)
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request(["streams", "0"], bound) with { OutputLimit = 128 }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(128, result.StandardOutput.Length); Assert.Equal(128, result.StandardError.Length); Assert.True(result.OutputTruncated);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Capture_can_be_disabled()
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request(["sleep", "1"]) with { CaptureOutput = false, ShowWindow = true }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Empty(result.StandardOutput); Assert.Empty(result.StandardError); Assert.False(result.OutputTruncated);
    }
    [Fact]
    public async Task Owner_cleanup_reaps_the_target_and_its_child_process()
    {
        using var session = new ProcessSession();
        var file = Path.Combine(_directory, "child.txt");
        var snapshot = session.Start(Request(["spawn", file]), "dut", Workers, null);
        await Until(() => File.Exists(file));
        var child = int.Parse(File.ReadAllText(file));
        Assert.True(Alive(child));
        session.CloseOwner("dut");
        await Until(() => !Alive(child) && !Alive(snapshot.ProcessId));
        Assert.Throws<KeyNotFoundException>(() => session.Snapshot(snapshot.Handle));
    }
    [Fact]
    public async Task Independent_process_survives_owner_cleanup_and_session_disposal()
    {
        var file = Path.Combine(_directory, "independent.txt");
        var session = new ProcessSession();
        var snapshot = session.Start(Request(["pid", file], false) with { CaptureOutput = false }, "dut", Workers, null);
        try
        {
            await Until(() => File.Exists(file)); session.CloseOwner("dut");
            Assert.False(session.Snapshot(snapshot.Handle).Completed); session.Dispose();
            Assert.True(Alive(snapshot.ProcessId));
        }
        finally { using var process = Process.GetProcessById(snapshot.ProcessId); process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); session.Dispose(); }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Timeout_and_cancel_only_kill_bound_processes(bool bound)
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request(["sleep", "60000"], bound), "owner", Workers, null);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => session.WaitAsync(snapshot.Handle, TimeSpan.FromMilliseconds(50), CancellationToken.None));
            if (bound) await Until(() => !Alive(snapshot.ProcessId)); else Assert.True(Alive(snapshot.ProcessId));
        }
        finally { session.Stop(snapshot.Handle); }
        var second = session.Start(Request(["sleep", "60000"], bound), "owner", Workers, null);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitAsync(second.Handle, TimeSpan.FromSeconds(20), cancel.Token));
            if (bound) await Until(() => !Alive(second.ProcessId)); else Assert.True(Alive(second.ProcessId));
        }
        finally { session.Stop(second.Handle); }
    }
    [Fact]
    public async Task Python_script_uses_the_bundled_interpreter_and_preserves_arguments()
    {
        var script = Path.Combine(_directory, "script ü.py");
        File.WriteAllText(Path.Combine(_directory, "local_helper.py"), "VALUE = 'local helper'\n");
        File.WriteAllText(script, "import sys, json, local_helper\nprint(local_helper.VALUE)\nprint(json.dumps(sys.argv[1:], ensure_ascii=False))\nprint('python stderr', file=sys.stderr)\nsys.exit(11)\n");
        using var session = new ProcessSession();
        var request = Request(["hello ü 漢字", "space and quote \""], kind: ExternalProcessKind.Python) with { File = script };
        var snapshot = session.Start(request, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(11, result.ExitCode); Assert.Null(result.HostError);
        Assert.Contains("hello ü 漢字", result.StandardOutput); Assert.Contains("python stderr", result.StandardError);
    }
    [Fact]
    public async Task Missing_explicit_interpreter_reports_start_error_without_success()
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request([], kind: ExternalProcessKind.Python) with { PythonExecutable = Path.Combine(_directory, "missing.exe") }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.NotNull(result.HostError);
    }
    [Fact]
    public async Task Bound_children_are_reaped_even_after_the_target_parent_exits()
    {
        using var session = new ProcessSession(); var file = Path.Combine(_directory, "orphan.txt");
        var snapshot = session.Start(Request(["spawn-and-exit", file]) with { CaptureOutput = false }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var child = int.Parse(File.ReadAllText(file)); Assert.True(Alive(child));
        session.CloseOwner("owner"); await Until(() => !Alive(child));
    }
    [Fact]
    public async Task A_managed_library_without_entrypoint_is_not_treated_as_an_executable()
    {
        using var session = new ProcessSession();
        var snapshot = session.Start(Request([], kind: ExternalProcessKind.ManagedEntryPoint) with { File = typeof(OrderHelper.Rules).Assembly.Location }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Contains("Einstiegspunkt", result.HostError);
    }
    [Fact]
    public async Task A_native_or_invalid_DLL_produces_a_host_error()
    {
        var file = Path.Combine(_directory, "native.dll"); File.WriteAllText(file, "not a managed assembly");
        using var session = new ProcessSession();
        var snapshot = session.Start(Request([], kind: ExternalProcessKind.ManagedEntryPoint) with { File = file }, "owner", Workers, null);
        var result = await session.WaitAsync(snapshot.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.NotNull(result.HostError);
    }
    [Fact]
    public void Cancelled_start_does_not_create_a_process()
    {
        using var session = new ProcessSession(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => session.Start(Request(["sleep", "1"]), "owner", Workers, null, cancelled.Token));
    }
    [Fact]
    public void Unknown_handles_cannot_control_other_runs()
    {
        using var one = new ProcessSession(); using var two = new ProcessSession();
        var snapshot = one.Start(Request(["sleep", "60000"]), "owner", Workers, null);
        Assert.Throws<KeyNotFoundException>(() => two.Stop(snapshot.Handle));
        Assert.Throws<KeyNotFoundException>(() => two.Snapshot(snapshot.Handle));
    }
    [Fact]
    public async Task Completed_handles_are_pruned_but_active_capacity_cannot_be_exceeded()
    {
        using var session = new ProcessSession(1);
        var first = session.Start(Request(["sleep", "1"]), "owner", Workers, null);
        await session.WaitAsync(first.Handle, TimeSpan.FromSeconds(20), CancellationToken.None);
        var second = session.Start(Request(["sleep", "60000"]), "owner", Workers, null);
        Assert.Throws<KeyNotFoundException>(() => session.Snapshot(first.Handle));
        Assert.Throws<InvalidOperationException>(() => session.Start(Request(["sleep", "1"]), "owner", Workers, null));
        Assert.False(session.Snapshot(second.Handle).Completed);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WaitAsync(second.Handle, TimeSpan.Zero, CancellationToken.None));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Session_capacity_is_explicitly_bounded(int capacity) => Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessSession(capacity));

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("limit")]
    [InlineData("type")]
    [InlineData("worker")]
    public void Invalid_launch_configuration_is_rejected_before_target_execution(string damage)
    {
        using var session = new ProcessSession();
        var request = Request(["sleep", "1"]);
        request = damage switch
        {
            "file" => request with { File = "relative.exe" },
            "directory" => request with { WorkingDirectory = "relative" },
            "limit" => request with { OutputLimit = -1 },
            "type" => request with { Kind = ExternalProcessKind.ManagedMain },
            _ => request
        };
        Assert.ThrowsAny<Exception>(() => session.Start(request, "owner", damage == "worker" ? _directory : Workers, null));
    }
}
