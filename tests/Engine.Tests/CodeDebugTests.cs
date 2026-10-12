using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Zeilen-Debugger für CodeBlocks (ADR 0017): einheitliche Schnittstelle für alle Sprachen, volle Zeilen-Haltepunkte und
/// Variablen-Inspektion bei Python und C#, reduzierter Modus (nur Eintritt/Austritt) bei C++. Getestet über echte Worker.
/// </summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class CodeDebugTests
{
    private static StepDefinition Code(string id, string language, string source)
        => BlockCatalog.Default.Create(BlockCatalog.Code, id, id, new Dictionary<string, string>
        {
            ["Sprache"] = language, ["Quelltext"] = source, ["Timeout"] = "30",
        });

    private static async Task<(TestRun Run, List<CodeLinePause> Pauses)> DebugRunAsync(
        string language, string source, Action<Testumgebung.Engine.DebugController> setup,
        Func<CodeLinePause, int, string> decide)
    {
        var dbg = new Testumgebung.Engine.DebugController();
        setup(dbg);
        var loop = Group("loop").Var("N", VarType.Int).Add(Code("code", language, source));
        var project = new TestProject(Test(loop));
        var (_, registry) = SampleProjects.CreateSimulation(project);

        var pauses = new List<CodeLinePause>();
        dbg.CodeLinePaused += p =>
        {
            int index;
            lock (pauses) { pauses.Add(p); index = pauses.Count; }
            switch (decide(p, index))
            {
                case "step": dbg.CodeStepLine(); break;
                case "stop": dbg.CodeStop(); break;
                default: dbg.CodeContinue(); break;
            }
        };

        var run = new TestRun(project.Definition, new TestRunOptions
        {
            Clock = new VirtualClock(), CodeRuntime = CodeBlockTests.SharedRuntime.Value, Hardware = registry, Debugger = dbg,
        });
        await run.RunAsync().WaitAsync(TimeSpan.FromMinutes(2));
        return (run, pauses);
    }

    private static Dictionary<string, object?> Vars(CodeLinePause p)
        => p.Variables.ToDictionary(v => v.Name, v => v.Value);

    [Fact]
    public async Task Python_Haltepunkte_Einzelschritt_und_Variablen()
    {
        const string source = "a = 1\nb = a + 2\nc = b * 3\nreturn Pass(c)";
        // Haltepunkt auf Zeile 2; dort Einzelschritt auf Zeile 3; dann fortsetzen.
        var (run, pauses) = await DebugRunAsync("Python", source,
            dbg => dbg.SetCodeBreakpoint("code", 2),
            (p, i) => i == 1 ? "step" : "continue");

        Assert.Equal(2, pauses.Count);
        Assert.Equal((2, "line"), (pauses[0].Line, pauses[0].Kind));
        Assert.Equal(CodeDebugCapability.FullLineStepping, pauses[0].Capability);
        Assert.Equal(1L, Convert.ToInt64(Vars(pauses[0])["a"]));            // b noch nicht gesetzt
        Assert.DoesNotContain("b", Vars(pauses[0]).Keys);
        Assert.Equal(3, pauses[1].Line);                                    // Einzelschritt 2 → 3
        Assert.Equal(3L, Convert.ToInt64(Vars(pauses[1])["b"]));

        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
        Assert.Equal(9L, Convert.ToInt64(step.Value));
    }

    [Fact]
    public async Task Python_Stopp_bricht_den_Code_ab()
    {
        const string source = "a = 1\nb = 2\nreturn Pass(a + b)";
        var (run, pauses) = await DebugRunAsync("Python", source,
            dbg => dbg.SetCodeBreakpoint("code", 1),
            (p, i) => "stop");

        Assert.Single(pauses);
        // „Stopp“ bricht den Code ab; die Engine wertet den abgebrochenen Lauf als ERROR.
        var step = run.Steps("code").Single();
        Assert.Equal(ResultRating.Error, step.Rating);
        Assert.Contains("abgebrochen", step.Message);
    }

    [Fact]
    public async Task CSharp_Haltepunkte_Einzelschritt_und_Variablen()
    {
        const string source = "long a = 1;\nlong b = a + 2;\nlong c = b * 3;\nreturn Pass(c);";
        var (run, pauses) = await DebugRunAsync("C#", source,
            dbg => dbg.SetCodeBreakpoint("code", 2),
            (p, i) => i == 1 ? "step" : "continue");

        Assert.Equal(2, pauses.Count);
        Assert.Equal((2, "line"), (pauses[0].Line, pauses[0].Kind));
        Assert.Equal(CodeDebugCapability.FullLineStepping, pauses[0].Capability);
        Assert.Equal(1L, Convert.ToInt64(Vars(pauses[0])["a"]));
        Assert.Equal(3, pauses[1].Line);
        Assert.Equal(3L, Convert.ToInt64(Vars(pauses[1])["b"]));

        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
        Assert.Equal(9L, Convert.ToInt64(step.Value));
    }

    [CppFact]
    public async Task Cpp_Zeilen_Stepping_und_Variablen_per_Instrumentierung()
    {
        var info = await CodeBlockTests.SharedRuntime.Value.GetRuntimeInfoAsync(CodeLanguage.Cpp, CancellationToken.None);
        Assert.DoesNotContain("Keine C++-Toolchain", info);

        const string source = "long long a = 1;\nlong long b = a + 2;\nlong long c = b * 3;\nreturn Pass((double)c);";
        var (run, pauses) = await DebugRunAsync("C++", source,
            dbg => dbg.SetCodeBreakpoint("code", 2),
            (p, i) => i == 1 ? "step" : "continue");

        Assert.Equal(2, pauses.Count);
        Assert.Equal((2, "line"), (pauses[0].Line, pauses[0].Kind));
        Assert.Equal(CodeDebugCapability.FullLineStepping, pauses[0].Capability);
        Assert.Equal(1L, Convert.ToInt64(Vars(pauses[0])["a"]));            // per Instrumentierung erfasst
        Assert.Equal(3, pauses[1].Line);                                    // Einzelschritt 2 → 3

        if (Testumgebung.Workers.BundledToolchain.Debugger is not null)
        {
            var native = Vars(pauses[0]);
            Assert.True(native.ContainsKey("GDB: a"), string.Join("; ", native.Select(v => v.Key + "=" + v.Value)));
            Assert.Equal("1", native["GDB: a"]);
            Assert.Contains("CodeBlock", native["GDB: Callstack"]?.ToString());
        }
        Assert.Equal(3L, Convert.ToInt64(Vars(pauses[1])["b"]));

        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
        Assert.Equal(9.0, Convert.ToDouble(step.Value));
    }

    [GdbFact]
    public async Task Gdb_liest_nativen_Struct_ohne_Streamoperator_und_setzt_Lauf_fort()
    {
        const string source = "struct Payload { int value; };\nPayload item = {42};\nreturn Pass((double)item.value);";
        var (run, pauses) = await DebugRunAsync("C++", source,
            dbg => dbg.SetCodeBreakpoint("code", 3), (_, _) => "continue");
        var pause = Assert.Single(pauses);
        var values = Vars(pause);
        Assert.True(values.ContainsKey("GDB: item"), string.Join("; ", values.Select(v => v.Key + "=" + v.Value)));
        Assert.Contains("value = 42", values["GDB: item"]?.ToString());
        Assert.Contains("CodeBlock", values["GDB: Callstack"]?.ToString());
        Assert.Equal(ResultRating.Pass, Assert.Single(run.Steps("code")).Rating);
    }

    [CppFact]
    public async Task Cpp_haelt_bei_Eintritt_wenn_gewuenscht()
    {
        var info = await CodeBlockTests.SharedRuntime.Value.GetRuntimeInfoAsync(CodeLanguage.Cpp, CancellationToken.None);
        Assert.DoesNotContain("Keine C++-Toolchain", info);

        const string source = "long long a = 5;\nreturn Pass((double)a);";
        var (run, pauses) = await DebugRunAsync("C++", source,
            dbg => dbg.StopOnCodeEntry = true,
            (p, i) => "continue"); // nur beim Eintritt halten, dann durchlaufen

        Assert.Equal("entry", pauses[0].Kind);
        Assert.Equal(ResultRating.Pass, run.Steps("code").Single().Rating);
    }

    [Fact]
    public async Task Ohne_Haltepunkt_laeuft_der_Code_ohne_Pause()
    {
        const string source = "a = 5\nreturn Pass(a)";
        var (run, pauses) = await DebugRunAsync("Python", source, _ => { }, (p, i) => "continue");
        Assert.Empty(pauses);
        Assert.Equal(ResultRating.Pass, run.Steps("code").Single().Rating);
    }
}

