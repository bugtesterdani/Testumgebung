using System.Text.Json;
using Testumgebung.Presentation.Pilot;

namespace Testumgebung.Engine.Tests;

public sealed class SystemProcessBoundaryTests
{
    [Theory]
    [InlineData("  first\nsecond", "stderr", "0", "first")]
    [InlineData("", "  diagnostic\nmore", "2", "diagnostic")]
    [InlineData("", "", "0", "")]
    [InlineData("", "", "2", null)]
    public void System_process_probe_uses_first_nonempty_output_or_exit_status(string stdout, string stderr, string exit, string? expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "system-process-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "compiler"))) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(directory, "scenario.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["stdout"] = stdout, ["stderr"] = stderr, ["exit"] = exit }));
            Assert.Equal(expected, SystemCheck.FirstOutputLine(Path.Combine(directory, "FakeCompiler.exe"), "probe"));
            Assert.Null(SystemCheck.FirstOutputLine(Path.Combine(directory, "missing.exe"), ""));
        }
        finally { Directory.Delete(directory, true); }
    }
}
