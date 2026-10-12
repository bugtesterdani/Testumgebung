using System.Text.Json;
using Testumgebung.Domain.Code;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class GdbProcessContractTests
{
    private static async Task<(NativeDebugSnapshot Snapshot, string Commands)> Capture(Dictionary<string, string> responses,
        string? frame = null, string? pc = null, string? thread = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-gdb-peer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "gdb")))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(folder, "scenario.json"), JsonSerializer.Serialize(responses));
            var snapshot = await GdbInspector.CaptureAsync(Path.Combine(folder, "FakeGdb.exe"), 123, CancellationToken.None, frame, pc, thread);
            return (snapshot, File.ReadAllText(Path.Combine(folder, "commands.txt")));
        }
        finally
        {
            // Windows may keep the terminated apphost mapped briefly after process exit.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(folder, true); break; }
                catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                { await Task.Delay(50); }
            }
        }
    }

    [Theory]
    [InlineData("file=\"CodeBlock\"")]
    [InlineData("fullname=\"C:/source/CodeBlock\"")]
    [InlineData("fullname=\"C:\\\\source\\\\CodeBlock\"")]
    public async Task Stack_fallback_selects_user_frame_and_filters_internal_variables(string source)
    {
        var (snapshot, commands) = await Capture(new()
        {
            ["-thread-info"] = "^done,threads=[{},{id=\"invalid\"},{id=\"1\"},{id=\"2\"}]",
            ["-stack-list-frames --thread 1"] = "^error,msg=\"thread unavailable\"",
            ["-stack-list-frames --thread 2"] = $"^done,stack=[frame={{level=\"3\",func=\"invoke\",{source},line=\"12\"}}]",
            ["-stack-list-variables"] = "^done,variables=[{name=\"value\",value=\"42\"},{name=\"__internal\",value=\"0\"},{name=\"missing\"},{},{name=\"value\",value=\"99\"}]"
        });
        Assert.Null(snapshot.Error);
        Assert.Equal("42", snapshot.Variables["value"]);
        Assert.Equal("<nicht verfügbar>", snapshot.Variables["missing"]);
        Assert.Equal(2, snapshot.Variables.Count);
        Assert.Contains("invoke", Assert.Single(snapshot.Frames));
        Assert.Contains("--frame 3", commands);
        Assert.Contains("-target-detach", commands);
        Assert.Contains("-gdb-exit", commands);
    }

    [Theory]
    [InlineData("^done")]
    [InlineData("^done,threads=[{id=\"1\"}]")]
    [InlineData("^done,threads=[{id=\"invalid\"}]")]
    public async Task Missing_user_frames_are_reported_and_target_is_detached(string threads)
    {
        var (snapshot, commands) = await Capture(new() { ["-thread-info"] = threads });
        Assert.Empty(snapshot.Variables);
        Assert.Empty(snapshot.Frames);
        Assert.Contains("No user code", snapshot.Error);
        Assert.Contains("-target-detach", commands);
    }

    [Theory]
    [InlineData("^error,msg=\"attach rejected\"")]
    [InlineData("^error")]
    [InlineData("^done,broken")]
    [InlineData("$close")]
    public async Task Invalid_process_responses_produce_snapshot_diagnostics(string response)
    {
        var (snapshot, _) = await Capture(new() { ["-target-attach"] = response });
        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Variables);
    }

    [Theory]
    [InlineData("Thread 123.7", true)]
    [InlineData("Thread 123.0x7", true)]
    [InlineData("Thread 123.7", false)]
    public async Task Explicit_native_frame_reads_variables_without_modifying_process_registers(string target, bool hasFrame)
    {
        var (snapshot, commands) = await Capture(new()
        {
            ["-thread-info"] = $"^done,threads=[{{id=\"1\",target-id=\"{target}\"}}]",
            ["-stack-list-variables"] = "^done,variables=[{name=\"value\",value=\"42\"},{name=\"__internal\"},{name=\"missing\"},{}]",
            ["-stack-info-frame"] = hasFrame ? "^done,frame={func=\"invoke\",file=\"CodeBlock\",line=\"4\"}" : "^done",
            ["-interpreter-exec console \"up-silently\""] = "^error,msg=\"end of stack\""
        }, "abc", "def", "7");
        Assert.Null(snapshot.Error);
        Assert.Equal("42", snapshot.Variables["value"]);
        Assert.Equal(2, snapshot.Variables.Count);
        Assert.Equal(hasFrame ? 1 : 0, snapshot.Frames.Count);
        Assert.Contains("frame view 0xabc 0xdef", commands);
        Assert.DoesNotContain("-data-write-register", commands);
        Assert.Contains("-target-detach", commands);
    }

    [Theory]
    [InlineData("0", "def", "7")]
    [InlineData("invalid", "def", "7")]
    [InlineData("abc", "0", "7")]
    [InlineData("abc", "invalid", "7")]
    [InlineData("abc", "def", "invalid")]
    [InlineData("abc", "def", "8")]
    public async Task Invalid_native_frame_context_falls_back_to_stack_search(string frame, string pc, string thread)
    {
        var (snapshot, commands) = await Capture(new()
        {
            ["-thread-info"] = "^done,threads=[{id=\"1\",target-id=\"Thread 123.7\"}]",
            ["-stack-list-frames"] = "^done,stack=[frame={level=\"invalid\",file=\"CodeBlock\"}]"
        }, frame, pc, thread);
        Assert.Contains("No user code", snapshot.Error);
        Assert.DoesNotContain("frame view", commands);
        Assert.Contains("-stack-list-frames", commands);
    }
}
