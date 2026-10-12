using System.Diagnostics;
using Testumgebung.Domain.Code;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class BundledPythonTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("configured-python", true)]
    [InlineData("configured-python", false)]
    public void Interpreter_resolution_respects_explicit_configuration_and_bundle(string? configured, bool bundled)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-python-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(directory, "python", "runtime", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        try
        {
            if (bundled) File.WriteAllText(executable, "fixture");
            var result = CodeRuntimeOptions.ResolvePython(directory, configured);
            Assert.Equal(configured == "configured-python" ? configured : bundled && OperatingSystem.IsWindows() ? executable : OperatingSystem.IsWindows() ? "python" : "python3", result);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Bundled_interpreter_runs_standard_library_without_PATH_or_external_Python_environment()
    {
        var python = CodeRuntimeOptions.DefaultPython;
        Assert.True(Path.IsPathFullyQualified(python));
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import sys,json,ssl,ctypes,sqlite3,importlib.metadata; assert sys.version_info[:3] == (3,14,8); assert sys.flags.isolated; print(json.dumps({'version':sys.version.split()[0]}))");
        start.Environment["PATH"] = "";
        start.Environment["PYTHONHOME"] = "C:/missing-python-home";
        start.Environment["PYTHONPATH"] = "C:/missing-python-path";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("", await error);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("3.14.8", await output);
    }

    [Fact]
    public async Task Default_worker_compiles_using_bundled_Python()
    {
        using var runtime = new CodeRuntime();
        var compiled = await runtime.CompileAsync(new CodeRequest(CodeLanguage.Python, "import json\nreturn Pass(json.loads('42'))", []), CancellationToken.None);
        Assert.True(compiled.Success);
        Assert.Contains("Python 3.14.8", compiled.RuntimeInfo);
    }
}
