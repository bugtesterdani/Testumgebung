using System.Reflection;
using System.Text.Json;

namespace Testumgebung.Engine.Tests;

public sealed class NativeToolchainProcessTests
{
    private static readonly Type Toolchain = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "workers", "native", "Testumgebung.Workers.Native.dll")).GetType("Testumgebung.Workers.Native.Toolchain")!;
    [Theory]
    [InlineData("cl.exe", "success")]
    [InlineData("clang++.exe", "success")]
    [InlineData("g++.exe", "success")]
    [InlineData("cl.exe", "diagnostic")]
    [InlineData("clang++.exe", "diagnostic")]
    [InlineData("g++.exe", "warning")]
    [InlineData("g++.exe", "silent")]
    [InlineData("g++.exe", "no-output")]
    [InlineData("g++.exe", "linker")]
    public void Compiler_process_receives_correct_flags_and_build_failures_are_diagnosed(string name, string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-cxx-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CXX");
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "compiler"))) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            var compiler = Path.Combine(directory, name); File.Copy(Path.Combine(directory, "FakeCompiler.exe"), compiler);
            var scenario = new Dictionary<string, string> { ["create"] = mode == "success" ? "yes" : "no", ["exit"] = mode is "success" or "no-output" ? "0" : "2" };
            if (mode == "diagnostic") scenario["stderr"] = name == "cl.exe" ? "CodeBlock(3,4): error C1234: invalid expression" : "CodeBlock:3:4: fatal error: invalid expression";
            if (mode == "warning") scenario["stdout"] = "CodeBlock:2:1: warning: unused value";
            if (mode == "linker") scenario["stderr"] = "linker ERROR: unresolved symbol";
            File.WriteAllText(Path.Combine(directory, "scenario.json"), JsonSerializer.Serialize(scenario));
            Environment.SetEnvironmentVariable("TESTUMGEBUNG_CXX", compiler);
            object?[] detectArgs = [null]; var tool = Toolchain.GetMethod("Detect")!.Invoke(null, detectArgs)!;
            Assert.NotNull(tool); Assert.Null(detectArgs[0]); Assert.Contains("14.2.1", tool.ToString()); Assert.DoesNotContain("sha256=system", tool.ToString());
            var source = Path.Combine(directory, "input.cpp"); File.WriteAllText(source, "fixture"); var output = Path.Combine(directory, "output.dll");
            var result = Toolchain.GetMethod("Build")!.Invoke(tool, [source, output])!;
            Assert.Equal(mode == "success", Property<bool>(result, "Success")); Assert.Equal(mode == "success" ? output : null, Property<string?>(result, "LibraryPath"));
            var arguments = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, "arguments.json")))!;
            Assert.Contains(name == "cl.exe" ? "/std:c++17" : "-std=c++17", arguments); Assert.Contains("input.cpp", arguments);
            if (name == "g++.exe") { Assert.Contains("-static-libgcc", arguments); Assert.Contains("-static-libstdc++", arguments); }
            var diagnostics = ((System.Collections.IEnumerable)result.GetType().GetProperty("Diagnostics")!.GetValue(result)!).Cast<object>().ToList();
            if (mode == "success") Assert.Empty(diagnostics);
            else
            {
                Assert.Contains(diagnostics, d => Property<string>(d, "Severity") == "error");
                if (mode == "diagnostic") { var diagnostic = Assert.Single(diagnostics); Assert.Equal(3, Property<int>(diagnostic, "Line")); Assert.Equal(4, Property<int>(diagnostic, "Column")); Assert.Equal("invalid expression", Property<string>(diagnostic, "Message")); }
                if (mode == "warning") Assert.Contains(diagnostics, d => Property<string>(d, "Severity") == "warning");
                if (mode == "linker") Assert.Contains(diagnostics, d => Property<string>(d, "Message").Contains("unresolved symbol"));
            }
        }
        finally { Environment.SetEnvironmentVariable("TESTUMGEBUNG_CXX", previous); Directory.Delete(directory, true); }
    }

    [Fact]
    public void Configured_compiler_that_cannot_start_has_a_precise_error()
    {
        var previous = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CXX");
        try
        {
            Environment.SetEnvironmentVariable("TESTUMGEBUNG_CXX", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"));
            object?[] args = [null]; Assert.Null(Toolchain.GetMethod("Detect")!.Invoke(null, args)); Assert.Contains("konnte nicht gestartet", Assert.IsType<string>(args[0]));
        }
        finally { Environment.SetEnvironmentVariable("TESTUMGEBUNG_CXX", previous); }
    }
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
}
