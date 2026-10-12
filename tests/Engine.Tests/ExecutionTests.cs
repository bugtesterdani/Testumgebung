using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Abnahmetests E01–E06 und Wiederholungsarten (Abschnitte 2.1–2.3).</summary>
public class ExecutionTests
{
    private static TestRun Run(TestDefinition def) => new(def, new TestRunOptions { Clock = new VirtualClock() });

    [Fact]
    public async Task E01_Variablen_im_Ablauf_lesen_aendern_und_verdecken()
    {
        var seen = new List<string>();
        var inner = Group("inner", "Messgruppe").Var("Spannung", VarType.Double, 1.0).Add(
            Steps.Code("read", "Lesen", ctx =>
            {
                seen.Add($"{ctx.Get("Spannung")}|{ctx.Get("../Spannung")}|{ctx.Get("Zaehler")}");
                ctx.Set("Zaehler", (long)ctx.Get("Zaehler") + 10);   // äußere Variable ändern
                ctx.Set("../Spannung", 231.0);                         // verdeckte äußere Variable gezielt ändern
                return StepOutcome.Unrated();
            }));
        var loop = Group("loop", "Test Loop").Var("Spannung", VarType.Double, 230.0).Var("Zaehler", VarType.Int, 0).Add(
            inner,
            Steps.Code("check", "Prüfen", ctx =>
            {
                seen.Add($"{ctx.Get("Spannung")}|{ctx.Get("Zaehler")}");
                return StepOutcome.Unrated();
            }));

        var run = Run(Test(loop));
        await run.RunAsync().Within();

        Assert.Equal(["1|230|0", "231|10"], seen);
    }

    [Fact]
    public async Task E02_Variablen_bleiben_ueber_Wiederholungen_und_werden_bei_neuem_Eintritt_initialisiert()
    {
        var values = new List<long>();
        var inner = Group("inner").Var("Versuch", VarType.Int, 0).Add(
            Steps.Code("inc", "Versuch++", ctx =>
            {
                var v = (long)ctx.Get("Versuch") + 1;
                ctx.Set("Versuch", v);
                values.Add(v);
                return StepOutcome.Unrated();
            }));
        inner.Repetition = Repetition.Times(3);
        var outer = Group("outer").Add(inner);
        outer.Repetition = Repetition.Times(2);

        var run = Run(Test(Group("loop").Add(outer)));
        await run.RunAsync().Within();

        Assert.Equal([1L, 2L, 3L, 1L, 2L, 3L], values);
        Assert.Equal(2, run.Events.OfType<GroupEntered>().Count(e => e.NodeId == "inner"));
        // Jeder Eintritt erzeugt einen neuen Scope
        var scopes = run.Events.OfType<GroupEntered>().Where(e => e.NodeId == "inner").Select(e => e.ScopeId).Distinct();
        Assert.Equal(2, scopes.Count());
    }

    [Fact]
    public async Task E03_Unbewertete_Schritte_und_NRESULT_Untergruppe_ergeben_NRESULT()
    {
        var probe = new Probe();
        var group = Group("g").Add(
            probe.Mark("a"),
            Steps.SetValue("set", "X", "1 + 1"),
            Group("sub").Add(probe.Mark("b.step", "b")));
        group.Var("X", VarType.Int);

        var run = Run(Test(Group("loop").Add(group)));
        var status = await run.RunAsync().Within();

        Assert.Equal(ResultRating.NResult, run.GroupResult("g").Rating);
        Assert.Equal(ResultRating.NResult, run.GroupResult("sub").Rating);
        Assert.Equal(new RunStatus(ResultRating.NResult, false), status);
    }

    [Fact]
    public async Task E04_FAIL_Schritt_und_PASS_Untergruppe_ergeben_FAIL()
    {
        var group = Group("g").Add(
            Steps.Rate("fail", ResultRating.Fail),
            Group("sub").Add(Steps.Rate("pass", ResultRating.Pass)),
            Steps.Rate("pass2", ResultRating.Pass)); // spätere niedrigere Bewertung ändert nichts

        var run = Run(Test(Group("loop").Add(group)));
        var status = await run.RunAsync().Within();

        Assert.Equal(ResultRating.Pass, run.GroupResult("sub").Rating);
        Assert.Equal(ResultRating.Fail, run.GroupResult("g").Rating);
        Assert.Equal(ResultRating.Fail, status.Rating);
    }

    [Fact]
    public async Task E05_ERROR_stoppt_weder_Gruppe_noch_Folgeschritt()
    {
        var probe = new Probe();
        var group = Group("g").Add(
            Steps.Fail("err", "Gerät antwortet nicht"),
            probe.Mark("next"),
            Steps.Rate("fail", ResultRating.Fail));
        group.Repetition = Repetition.Times(2);

        var run = Run(Test(Group("loop").Add(group)));
        await run.RunAsync().Within();

        Assert.Equal(["next", "next"], probe.Marks);
        var error = run.Steps("err").First();
        Assert.Equal(ResultRating.Error, error.Rating);
        Assert.Equal("Gerät antwortet nicht", error.Message);
        Assert.Equal(ResultRating.Error, run.GroupResult("g").Rating);
    }

    [Theory]
    [InlineData(RepeatResultPolicy.ResetToNResult, ResultRating.Pass)]
    [InlineData(RepeatResultPolicy.Keep, ResultRating.Fail)]
    public async Task E06_Ergebnisreset_vor_Wiederholung_setzt_nur_Bewertung_zurueck(RepeatResultPolicy policy, ResultRating expected)
    {
        var group = Group("g").Var("Versuch", VarType.Int, 0).Add(
            Steps.Code("try", "Versuch", ctx =>
            {
                var v = (long)ctx.Get("Versuch") + 1;
                ctx.Set("Versuch", v);
                return v == 1 ? StepOutcome.Fail() : StepOutcome.Pass();
            }));
        group.Repetition = Repetition.Until("Versuch >= 3");
        group.ResultBeforeRepeat = policy;
        long final = 0;
        group.ExitGroup = Group("g.exit").Add(Steps.Code("read", "lesen", ctx =>
        {
            final = (long)ctx.Get("Versuch");
            return StepOutcome.Unrated();
        }));

        var run = Run(Test(Group("loop").Add(group)));
        await run.RunAsync().Within();

        Assert.Equal(expected, run.GroupResult("g").Rating);
        Assert.Equal(3L, final); // Variablen nicht zurückgesetzt
        Assert.Equal(3, run.GroupResult("g").Iterations);
    }

    [Fact]
    public async Task Bedingungen_existieren_an_Gruppen_und_steuern_den_Eintritt()
    {
        var probe = new Probe();
        var loop = Group("loop").Var("Modus", VarType.String, "B").Add(
            new GroupDefinition("a", "A") { EntryCondition = "Modus == \"A\"" }.Add(probe.Mark("a.step", "a")),
            new GroupDefinition("b", "B") { EntryCondition = "Modus == \"B\"" }.Add(probe.Mark("b.step", "b")),
            new GroupDefinition("bad", "Fehlerhaft") { EntryCondition = "Unbekannt == 1" }.Add(probe.Mark("bad.step", "bad")));

        var run = Run(Test(loop));
        var status = await run.RunAsync().Within();

        Assert.Equal(["b"], probe.Marks);
        Assert.Contains(run.Events.OfType<GroupNotEntered>(), e => e.NodeId == "a");
        Assert.Contains(run.Events.OfType<Diagnostic>(), d => d.NodeId == "bad" && d.Message.Contains("Unbekannt"));
        Assert.Equal(ResultRating.Error, status.Rating); // fehlerhafte Bedingung ist eine Diagnose (ERROR)
    }

    [Fact]
    public async Task Wiederholung_Solange_prueft_vor_jeder_Iteration()
    {
        var group = Group("g").Var("N", VarType.Int, 0).Add(Steps.SetValue("inc", "N", "N + 1"));
        group.Repetition = Repetition.While("N < 4");
        var run = Run(Test(Group("loop").Add(group)));
        await run.RunAsync().Within();
        Assert.Equal(4, run.GroupResult("g").Iterations);

        var never = Group("never").Var("N", VarType.Int, 10).Add(Steps.SetValue("inc2", "N", "N + 1"));
        never.Repetition = Repetition.While("N < 4");
        var run2 = Run(Test(Group("loop").Add(never)));
        await run2.RunAsync().Within();
        Assert.Equal(0, run2.GroupResult("never").Iterations);
    }

    [Fact]
    public async Task Wiederholung_Zeitspanne_prueft_Frist_zwischen_Iterationen()
    {
        var clock = new VirtualClock(autoAdvance: true);
        var group = Group("g").Add(Steps.Wait("w", TimeSpan.FromSeconds(4)));
        group.Repetition = Repetition.For(TimeSpan.FromSeconds(10));
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Clock = clock });
        await run.RunAsync().Within();
        // 0 s → 4 s → 8 s → 12 s: drei Iterationen, Frist wird erst zwischen Iterationen geprüft
        Assert.Equal(3, run.GroupResult("g").Iterations);
    }

    [Fact]
    public async Task Pruefling_ist_eigener_Run_mit_verpflichtender_Kennung()
    {
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }
            .Var("Kennung", VarType.String)
            .Var("Nr", VarType.Int)
            .Add(Steps.SetValue("id", "Kennung", "\"SN-\" + \"10482\""), Steps.Rate("p", ResultRating.Pass));
        var noId = new GroupDefinition("dut2", "Prüfling ohne Kennung") { IsDutRun = true }
            .Var("Kennung", VarType.String)
            .Add(Steps.Rate("p2", ResultRating.Pass));

        var run = Run(Test(Group("loop").Add(dut, noId)));
        await run.RunAsync().Within();

        var completed = run.Events.OfType<DutRunCompleted>().ToList();
        Assert.Equal(2, completed.Count);
        Assert.Equal("SN-10482", completed[0].DutId);
        Assert.Equal(new RunStatus(ResultRating.Pass, false), completed[0].Status);
        Assert.Equal("1.0", completed[0].TestVersion);
        Assert.Equal(ResultRating.Error, completed[1].Status.Rating);
        Assert.NotEqual(completed[0].DutRunId, completed[1].DutRunId);
    }

    [Fact]
    public async Task Zustaende_werden_gemeldet()
    {
        var run = Run(Test(Group("loop").Add(Steps.Rate("p", ResultRating.Pass))));
        await run.RunAsync().Within();
        var states = run.Events.OfType<StateChanged>().Select(s => s.State).ToList();
        Assert.Equal(EngineState.Initializing, states.First());
        Assert.Contains(EngineState.Testing, states);
        Assert.Contains(EngineState.Executing, states);
        Assert.Contains(EngineState.Finalizing, states);
        Assert.Equal(EngineState.Completed, states.Last());
        Assert.Equal(EngineState.Completed, run.State);
    }

    [Fact]
    public void Ungueltige_Definitionen_werden_statisch_erkannt()
    {
        var loop = Group("loop").Add(Group("dup"), Group("dup"));
        loop.Repetition = Repetition.Times(0);
        var diagnostics = Test(loop).Validate();
        Assert.Contains(diagnostics, d => d.Contains("dup"));
        Assert.Contains(diagnostics, d => d.Contains("Anzahl"));
    }
}
