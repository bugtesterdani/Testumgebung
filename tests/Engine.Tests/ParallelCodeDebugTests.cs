using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class ParallelCodeDebugTests
{
    [Fact]
    public async Task Simultaneous_code_breakpoints_do_not_overwrite_the_active_pause()
    {
        var debugger = new DebugController { StopOnCodeEntry = true };
        var seen = new ConcurrentBag<string>();
        debugger.CodeLinePaused += pause => { seen.Add(pause.NodeId); debugger.CodeContinue(); };
        StepDefinition Code(string id) => BlockCatalog.Default.Create(BlockCatalog.Code, id, id,
            new Dictionary<string, string> { ["Sprache"] = "C#", ["Quelltext"] = "return Pass();" });
        var parallel = Group("p").Add(Code("a"), Code("b"));
        parallel.Parallel = true;
        var run = new TestRun(Test(Group("loop").Add(parallel)), new TestRunOptions { Debugger = debugger, CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        var task = run.RunAsync();
        try
        {
            Assert.Equal(ResultRating.Pass, (await task.WaitAsync(TimeSpan.FromMinutes(2))).Rating);
            Assert.Equal(new[] { "a", "b" }, seen.Order());
        }
        finally { run.RequestAbort(); await task.WaitAsync(TimeSpan.FromSeconds(30)); }
    }
}
