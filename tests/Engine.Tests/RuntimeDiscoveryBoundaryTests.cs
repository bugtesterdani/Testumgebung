using System.Reflection;
using Testumgebung.Domain.Code;
using Testumgebung.Presentation.Pilot;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class RuntimeDiscoveryBoundaryTests
{
    [Theory]
    [InlineData(null, "clang++", true)]
    [InlineData("", "g++", false)]
    [InlineData(" ", "missing", true)]
    [InlineData("fixture-cl.exe", "configured", false)]
    [InlineData("cl.exe", "configured", true)]
    [InlineData("fixture-cxx.exe", "clang++", false)]
    public async Task System_check_uses_configured_compiler_and_falls_back_when_unavailable(string? compiler, string available, bool pythonAvailable)
    {
        var previous = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CXX");
        var calls = new List<(string File, string Arguments)>();
        try
        {
            Environment.SetEnvironmentVariable("TESTUMGEBUNG_CXX", compiler);
            var checks = await SystemCheck.RunAsync(new SystemCheckContext
            {
                PythonExecutable = "fixture-python",
                VisaAvailable = () => true,
                DaqmxAvailable = () => true,
                RunProcess = (file, arguments) =>
                {
                    calls.Add((file, arguments));
                    if (file == "fixture-python") return pythonAvailable ? "Python fixture" : null;
                    if (available == "configured" && file == compiler || file == available) return "compiler fixture";
                    return null;
                }
            });
            Assert.Equal(pythonAvailable ? CheckLevel.Ok : CheckLevel.Warning, checks.Single(c => c.Name == "Python").Level);
            var cxx = checks.Single(c => c.Name == "C++-Toolchain");
            Assert.Equal(available == "missing" ? CheckLevel.Warning : CheckLevel.Ok, cxx.Level);
            if (available == "configured")
            {
                Assert.Contains("TESTUMGEBUNG_CXX", cxx.Detail);
                Assert.Contains(calls, c => c.File == compiler && c.Arguments == (compiler == "cl.exe" ? "" : "--version"));
                Assert.DoesNotContain(calls, c => c.File == "clang++");
            }
            if (available == "g++") Assert.Contains(calls, c => c.File == "clang++");
        }
        finally { Environment.SetEnvironmentVariable("TESTUMGEBUNG_CXX", previous); }
    }

    [Theory]
    [InlineData("configured")]
    [InlineData("host")]
    [InlineData("root")]
    [InlineData("invalid")]
    [InlineData("empty")]
    public void Dotnet_host_resolution_respects_priority_and_file_existence(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var host = Path.Combine(directory, "host.exe"); var rootHost = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(host, "fixture"); File.WriteAllText(rootHost, "fixture");
        var oldHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"); var oldRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", mode is "configured" or "host" ? host : mode == "empty" ? "" : host + ".missing");
            Environment.SetEnvironmentVariable("DOTNET_ROOT", mode is "configured" or "host" or "root" ? directory : mode == "empty" ? "" : directory + ".missing");
            using var runtime = new CodeRuntime(new CodeRuntimeOptions { DotnetHost = mode == "configured" ? "explicit-host" : null });
            var resolved = (string)typeof(CodeRuntime).GetMethod("DotnetHost", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(runtime, null)!;
            Assert.Equal(mode == "configured" ? "explicit-host" : mode == "host" ? host : mode == "root" ? rootHost : "dotnet", resolved);
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", oldHost); Environment.SetEnvironmentVariable("DOTNET_ROOT", oldRoot); Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(CodeLanguage.CSharp)]
    [InlineData(CodeLanguage.Python)]
    [InlineData(CodeLanguage.Cpp)]
    [InlineData(CodeLanguage.LabView)]
    [InlineData((CodeLanguage)99)]
    public async Task Missing_worker_reports_language_without_starting_process(CodeLanguage language)
    {
        using var runtime = new CodeRuntime(new CodeRuntimeOptions { WorkerDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        var exception = await Assert.ThrowsAsync<CodeExecutionException>(() => runtime.GetRuntimeInfoAsync(language, CancellationToken.None));
        Assert.Contains(language == (CodeLanguage)99 ? "nicht unterstützt" : "nicht gefunden", exception.Message);
    }

    [Fact]
    public async Task Missing_python_interpreter_has_actionable_error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-python-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(directory, "python"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "python", "worker.py"), "");
            using var runtime = new CodeRuntime(new CodeRuntimeOptions { WorkerDirectory = directory, PythonExecutable = Path.Combine(directory, "missing-python.exe") });
            var exception = await Assert.ThrowsAsync<CodeExecutionException>(() => runtime.GetRuntimeInfoAsync(CodeLanguage.Python, CancellationToken.None));
            Assert.Contains("Python-Interpreter", exception.Message); Assert.Contains("konnte nicht gestartet", exception.Message);
        }
        finally { Directory.Delete(directory, true); }
    }
}
