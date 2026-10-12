using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Abnahmetests E07–E09 (Abschnitt 2.4: Exit-Gruppen und Abbruch).</summary>
public class AbortAndExitTests
{
    /// <summary>
    /// Hauptablauf wie im Plan: Init, Test Loop (endlos) &gt; Prüfling (Exit) &gt; Messgruppe (Exit) &gt; Gate.
    /// </summary>
    private static (TestDefinition Def, Probe Probe) BuildMain(Action<GroupDefinition, GroupDefinition, Probe>? customize = null)
    {
        var probe = new Probe();
        var inner = Group("inner", "Messgruppe – Belastung").Var("Strom", VarType.Double, 4.2).Add(
            Steps.Rate("fail", ResultRating.Fail, "Messung unter Last"),
            probe.Gate("gate"),
            probe.Mark("after-gate"));
        inner.ExitGroup = Group("inner.exit", "Exit – Abschalten").Add(
            Steps.Code("inner.exit.read", "Strom lesen", ctx =>
            {
                ctx.Log($"Strom={ctx.Get("Strom")}"); // Variablen der umgebenden Gruppe im Exit verfügbar
                return StepOutcome.Unrated();
            }),
            probe.Mark("inner.exit.step", "exit:inner"));
        // Bedingung darf das Betreten in der Abbruchfolge nicht verhindern
        inner.ExitGroup.EntryCondition = "false";
        // … Bedingungen enthaltener Untergruppen bleiben wirksam
        inner.ExitGroup.Add(new GroupDefinition("inner.exit.sub", "Bedingte Untergruppe") { EntryCondition = "false" }.Add(probe.Mark("never")));

        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }
            .Var("Kennung", VarType.String, "SN-10482")
            .Add(probe.Mark("dut.start"), inner, probe.Mark("dut.after"));
        dut.ExitGroup = Group("dut.exit", "Exit Prüfling").Add(probe.Mark("dut.exit.step", "exit:dut"), Steps.Rate("dut.exit.pass", ResultRating.Pass));

        var loop = new GroupDefinition("loop", "Test Loop / Testing") { Repetition = Repetition.Endless }.Add(dut);
        loop.ExitGroup = Group("loop.exit", "Exit Loop").Add(probe.Mark("loop.exit.step", "exit:loop"));
        var init = Group("init", "Test Start / Init").Add(probe.Mark("init.step", "init"));
        var fin = Group("fin", "Test End / Finalization").Add(probe.Mark("fin.step", "finalization"));

        customize?.Invoke(inner, dut, probe);
        return (new TestDefinition("AC-Prüfung", "1.5", init, loop, fin), probe);
    }

    [Fact]
    public async Task E07_Werkerabbruch_in_innerer_Gruppe_fuehrt_Exits_von_innen_nach_aussen_dann_Finalization_aus()
    {
        var (def, probe) = BuildMain();
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock() });
        var task = run.RunAsync();

        await probe.WhenReached("gate");
        Assert.Equal(EngineState.Executing, run.State);
        Assert.Equal(["Hauptablauf", "Test Loop / Testing", "Prüfling", "Messgruppe – Belastung", "gate"],
            run.ExecutionStack.Select(f => f.Name));

        run.RequestAbort();
        var status = await task.Within();

        Assert.Equal(["init", "dut.start", "gate", "exit:inner", "exit:dut", "exit:loop", "finalization"], probe.Marks);
        Assert.DoesNotContain("after-gate", probe.Marks);  // keine weiteren regulären Schritte
        Assert.DoesNotContain("dut.after", probe.Marks);
        Assert.DoesNotContain("never", probe.Marks);        // Untergruppen-Bedingung bleibt wirksam

        // Laufender Schritt wurde kooperativ abgebrochen
        Assert.True(run.Steps("gate").Single().Cancelled);
        // FAIL und WERROR getrennt geführt
        Assert.Equal(new RunStatus(ResultRating.Fail, OperatorAbort: true), status);
        var dutRun = run.Events.OfType<DutRunCompleted>().Single();
        Assert.Equal("SN-10482", dutRun.DutId);
        Assert.Equal(new RunStatus(ResultRating.Fail, true), dutRun.Status); // begonnener Prüflingstest wird gemeldet/gespeichert
        Assert.Contains(run.Events.OfType<Diagnostic>(), d => d.Message == "Strom=4.2" || d.Message == "Strom=4,2");
        Assert.Equal(EngineState.Completed, run.State);
    }

    [Fact]
    public async Task E07_Dauerhafter_Loop_laeuft_trotz_FAIL_und_ERROR_bis_zum_Werkerabbruch()
    {
        var probe = new Probe();
        var loop = new GroupDefinition("loop", "Test Loop") { Repetition = Repetition.Endless }.Var("N", VarType.Int).Add(
            Steps.Fail("err", "x"),
            Steps.Rate("fail", ResultRating.Fail),
            Steps.SetValue("inc", "N", "N + 1"),
            new GroupDefinition("third", "Dritter Durchlauf") { EntryCondition = "N == 3" }.Add(probe.Gate("gate")));
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });
        var task = run.RunAsync();

        await probe.WhenReached("gate");
        run.RequestAbort();
        var status = await task.Within();

        Assert.Equal(3, run.GroupResult("loop").Iterations);
        Assert.Equal(new RunStatus(ResultRating.Error, true), status);
    }

    [Fact]
    public async Task E08_Erneuter_Abbruch_beendet_nur_die_aktuelle_Exit_Gruppe_aeussere_Exit_laeuft()
    {
        var (def, probe) = BuildMain((inner, _, p) =>
        {
            inner.ExitGroup!.Children.Insert(0, p.Gate("exit.gate"));
            inner.ExitGroup.Repetition = Repetition.Times(3); // gestartete Exit-Gruppe darf ihre Wiederholungen ausführen
        });
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock() });
        var task = run.RunAsync();

        await probe.WhenReached("gate");
        run.RequestAbort();                       // 1. Abbruch: Exit-Folge beginnt
        await probe.WhenReached("exit.gate");
        Assert.Equal(EngineState.Executing, run.State);
        Assert.Contains(run.ExecutionStack, f => f.IsExit && f.NodeId == "inner.exit");

        run.RequestAbort();                       // 2. Abbruch: nur aktuelle Exit-Gruppe beenden
        var status = await task.Within();

        Assert.Equal(["init", "dut.start", "gate", "exit.gate", "exit:dut", "exit:loop", "finalization"], probe.Marks);
        Assert.Equal(1, run.GroupResult("inner.exit").Iterations);   // keine weitere Iteration
        Assert.Contains(run.Events.OfType<ExitGroupAborted>(), e => e.NodeId == "inner.exit");
        Assert.DoesNotContain(run.Events.OfType<ExitGroupAborted>(), e => e.NodeId == "dut.exit");
        var aborts = run.Events.OfType<OperatorAbortRequested>().ToList();
        Assert.Equal([1, 2], aborts.Select(a => a.Number));
        Assert.True(status.OperatorAbort);
    }

    [Fact]
    public async Task E08_Dritter_Abbruch_beendet_die_naechste_aeussere_Exit_Gruppe()
    {
        var (def, probe) = BuildMain((inner, dut, p) =>
        {
            inner.ExitGroup!.Children.Insert(0, p.Gate("exit.gate"));
            dut.ExitGroup!.Children.Insert(0, p.Gate("dut.exit.gate"));
        });
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock() });
        var task = run.RunAsync();

        await probe.WhenReached("gate");
        run.RequestAbort();
        await probe.WhenReached("exit.gate");
        run.RequestAbort();
        await probe.WhenReached("dut.exit.gate");
        run.RequestAbort();
        await task.Within();

        Assert.Equal(["init", "dut.start", "gate", "exit.gate", "dut.exit.gate", "exit:loop", "finalization"], probe.Marks);
    }

    [Fact]
    public async Task E08_Erster_Abbruch_waehrend_regulaerer_Exit_Gruppe_laesst_diese_weiterlaufen()
    {
        var (def, probe) = BuildMain((inner, _, p) =>
        {
            inner.Children.Clear();
            inner.Add(p.Mark("inner.step"));
            inner.ExitGroup!.EntryCondition = null;
            inner.ExitGroup.Children.Insert(0, p.Gate("exit.gate"));
            inner.ExitGroup.Repetition = Repetition.Times(2);
        });
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock() });
        var task = run.RunAsync();

        await probe.WhenReached("exit.gate");     // Exit beim regulären Verlassen
        run.RequestAbort();                       // erster Abbruch: Exit darf Wiederholungen ausführen
        probe.Release("exit.gate");
        await task.Within();

        Assert.Equal(2, run.GroupResult("inner.exit").Iterations);
        Assert.Equal(["init", "dut.start", "inner.step", "exit.gate", "exit:inner", "exit.gate#2", "exit:inner", "exit:dut", "exit:loop", "finalization"], probe.Marks);
        Assert.DoesNotContain("dut.after", probe.Marks);
    }

    [Fact]
    public async Task E09_Exit_bei_regulaerem_Gruppenende_nach_letzter_Iteration_vor_Scopefreigabe()
    {
        var log = new List<string>();
        var group = Group("g").Var("Versuch", VarType.Int, 0).Add(
            Steps.Code("inc", "Versuch++", ctx =>
            {
                ctx.Set("Versuch", (long)ctx.Get("Versuch") + 1);
                log.Add($"iter{ctx.Get("Versuch")}");
                return StepOutcome.Pass();
            }));
        group.Repetition = Repetition.Times(3);
        group.ExitGroup = Group("g.exit").Var("Lokal", VarType.Int, 7).Add(
            Steps.Code("exit", "Exit", ctx =>
            {
                log.Add($"exit Versuch={ctx.Get("Versuch")} Lokal={ctx.Get("Lokal")}");
                return StepOutcome.Fail();
            }));

        var skipped = Group("h").Add(Steps.Rate("h.pass", ResultRating.Pass));
        skipped.ExitGroup = new GroupDefinition("h.exit", "bedingter Exit") { EntryCondition = "false" }.Add(Steps.Rate("h.exit.fail", ResultRating.Fail));

        var run = new TestRun(Test(Group("loop").Add(group, skipped)), new TestRunOptions { Clock = new VirtualClock() });
        var status = await run.RunAsync().Within();

        Assert.Equal(["iter1", "iter2", "iter3", "exit Versuch=3 Lokal=7"], log);
        // Exit-Bewertung zählt wie eine Untergruppe
        Assert.Equal(ResultRating.Fail, run.GroupResult("g").Rating);
        // Exit nach den Iterationen, vor dem Verlassen der Gruppe
        var events = run.Events.Where(e => e is NodeEvent n && (n.NodeId == "g" || n.NodeId == "g.exit")).ToList();
        Assert.IsType<GroupCompleted>(events[^1]);
        Assert.Equal("g", ((NodeEvent)events[^1]).NodeId);
        // Regulärer Exit beachtet seine Eintrittsbedingung
        Assert.Contains(run.Events.OfType<GroupNotEntered>(), e => e.NodeId == "h.exit");
        Assert.Equal(ResultRating.Pass, run.GroupResult("h").Rating);
        Assert.Equal(new RunStatus(ResultRating.Fail, false), status);
    }

    [Fact]
    public async Task ERROR_im_Exit_ueberspringt_keine_folgenden_Exit_Schritte()
    {
        var probe = new Probe();
        var group = Group("g").Add(probe.Gate("gate"));
        group.ExitGroup = Group("g.exit").Add(Steps.Fail("x", "Abschalten fehlgeschlagen"), probe.Mark("after-error"));
        var loop = new GroupDefinition("loop", "Loop") { Repetition = Repetition.Endless }.Add(group);
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });
        var task = run.RunAsync();
        await probe.WhenReached("gate");
        run.RequestAbort();
        var status = await task.Within();

        Assert.Contains("after-error", probe.Marks);
        Assert.Equal(new RunStatus(ResultRating.Error, true), status);
    }
}
