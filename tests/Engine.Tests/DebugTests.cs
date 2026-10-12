using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Abnahmetests D01–D06 (Abschnitt 6: Haltepunkte und gezielte Ausführung).</summary>
public class DebugTests
{
    private static (TestRun Run, DebugController Dbg) Create(TestDefinition def)
    {
        var dbg = new DebugController();
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg });
        return (run, dbg);
    }

    private static GroupDefinition MeasureGroup(Func<int, StepOutcome> outcome, Action<IStepContext>? sideEffect = null)
    {
        var count = 0;
        return Group("dut", "Prüfling")
            .Var("Spannung", VarType.Double, 230.0)
            .Var("GrenzwertMin", VarType.Double, 220.0)
            .Var("GrenzwertMax", VarType.Double, 240.0)
            .Add(
                Steps.Code("measure", "Messung Spannung", ctx =>
                {
                    count++;
                    ctx.Set("Spannung", count == 1 ? 249.0 : 231.0);
                    sideEffect?.Invoke(ctx);
                    return outcome(count);
                }),
                Steps.Code("next", "Messung Strom", _ => StepOutcome.Unrated()));
    }

    [Fact]
    public async Task D01_Haltepunkt_nach_FAIL_Schritt_Ausgaenge_wirksam_Elternbewertung_unveraendert()
    {
        var hardwareWrites = 0;
        var (run, dbg) = Create(Test(Group("loop").Add(MeasureGroup(_ => StepOutcome.Fail("außerhalb"), _ => hardwareWrites++))));
        dbg.SetBreakpoint("measure", "M. Weber");
        var paused = run.WaitFor<Paused>();
        var task = run.RunAsync();

        var info = (await paused).Info;
        Assert.Equal(EngineState.PausedBeforeResultReturn, run.State);
        Assert.Equal(ResultRating.Fail, info.PendingRating);      // ausstehende Bewertung
        Assert.Equal(ResultRating.NResult, info.ParentRating);    // Elternbewertung noch unverändert
        Assert.Equal(1, hardwareWrites);                          // Hardwareaktion bereits erfolgt
        Assert.Equal("loop > Prüfling > Messung Spannung", info.Path);
        var spannung = dbg.ActiveVariables().Single(v => v.Name == "Spannung");
        Assert.Equal(249.0, spannung.Value);                      // Ausgänge bereits wirksam
        Assert.Equal("Prüfling", spannung.OriginGroup);
        Assert.Empty(run.Steps("next"));                          // nächstes Element noch nicht gestartet

        dbg.Continue();
        var status = await task.Within();

        Assert.Equal(ResultRating.Fail, run.GroupResult("dut").Rating);
        Assert.Equal(ResultRating.Fail, status.Rating);
    }

    [Fact]
    public async Task D02_Wiederholung_am_Haltepunkt_uebernimmt_ausschliesslich_die_letzte_Bewertung()
    {
        var (run, dbg) = Create(Test(Group("loop").Add(MeasureGroup(n => n == 1 ? StepOutcome.Fail() : StepOutcome.Pass()))));
        dbg.SetBreakpoint("measure");
        var first = run.WaitFor<Paused>(p => p.Info.ExecutionCount == 1);
        var second = run.WaitFor<Paused>(p => p.Info.ExecutionCount == 2);
        var task = run.RunAsync();

        Assert.Equal(ResultRating.Fail, (await first).Info.PendingRating);
        dbg.Rerun();
        var info = (await second).Info;
        Assert.Equal(ResultRating.Pass, info.PendingRating);
        Assert.Equal(ResultRating.NResult, info.ParentRating);    // FAIL wurde nicht aggregiert
        Assert.Equal(231.0, dbg.ActiveVariables().Single(v => v.Name == "Spannung").Value);
        dbg.Continue();
        await task.Within();

        Assert.Equal(2, run.Steps("measure").Count());
        Assert.Equal(ResultRating.Pass, run.GroupResult("dut").Rating); // keine doppelte Aggregation
        Assert.True(run.IsDevelopmentRun);
    }

    [Fact]
    public async Task D03_Gruppenhaltepunkt_Gruppe_inklusive_Exit_fertig_Elternbewertung_wartet()
    {
        var probe = new Probe();
        var group = Group("g", "Messgruppe").Var("Lokal", VarType.Int, 0).Add(
            Steps.SetValue("inc", "Lokal", "Lokal + 1"),
            Steps.Rate("fail", ResultRating.Fail));
        group.Repetition = Repetition.Times(2);
        group.ExitGroup = Group("g.exit").Add(probe.Mark("exit"));
        var loop = Group("loop").Add(group, probe.Mark("after"));
        var (run, dbg) = Create(Test(loop));
        dbg.SetBreakpoint("g");
        var first = run.WaitFor<Paused>(p => p.Info.ExecutionCount == 1);
        var second = run.WaitFor<Paused>(p => p.Info.ExecutionCount == 2);
        var task = run.RunAsync();

        var info = (await first).Info;
        Assert.True(info.IsGroup);
        Assert.Equal(["exit"], probe.Marks);                      // Exit bereits gelaufen
        Assert.Equal(ResultRating.Fail, info.PendingRating);
        Assert.Equal(ResultRating.NResult, info.ParentRating);    // Elternbewertung wartet
        Assert.Equal(2L, info.LeftScopeSnapshot!.Variables.Single(v => v.Name == "Lokal").Value); // Debug-Schnappschuss
        Assert.DoesNotContain(dbg.ActiveVariables(), v => v.Name == "Lokal");                     // kein aktiver Scope

        dbg.Rerun();                                              // neuer Eintritt, lokale Variablen neu initialisiert
        Assert.Equal(2L, (await second).Info.LeftScopeSnapshot!.Variables.Single(v => v.Name == "Lokal").Value);
        dbg.Continue();
        await task.Within();

        Assert.Equal(["exit", "exit", "after"], probe.Marks);
        Assert.Equal(ResultRating.Fail, run.GroupResult("loop").Rating);
    }

    [Fact]
    public async Task D04_Einzelner_Schritt_in_Gruppe_mit_falscher_Bedingung_gezielt_ausfuehrbar()
    {
        var group = new GroupDefinition("g", "Nur bei Variante B") { EntryCondition = "Variante == \"B\"" }
            .Var("Spannung", VarType.Double)
            .Add(Steps.Code("step", "Bewertung Spannung", ctx =>
                (double)ctx.Get("Spannung") > (double)ctx.Get("../GrenzwertMax") ? StepOutcome.Fail() : StepOutcome.Pass()));
        var loop = Group("loop").Var("Variante", VarType.String, "A").Var("GrenzwertMax", VarType.Double, 240.0).Add(group);
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });

        var ok = await run.ExecuteStepTargetedAsync("step", new Dictionary<string, object> { ["Spannung"] = 230.0 }, "M. Weber").Within();
        var bad = await run.ExecuteStepTargetedAsync("step", new Dictionary<string, object> { ["Spannung"] = 249.0 }).Within();

        Assert.True(ok.Executed);
        Assert.Equal(ResultRating.Pass, ok.Rating);
        Assert.Equal(ResultRating.Fail, bad.Rating);
        Assert.True(run.IsDevelopmentRun);
        Assert.Contains(run.Events.OfType<DevelopmentEvent>(), e => e.User == "M. Weber" && e.Kind == "Schritt gezielt ausgeführt");
    }

    [Fact]
    public async Task D05_Gruppe_mit_falscher_Bedingung_gezielt_starten_wird_verweigert()
    {
        var probe = new Probe();
        var group = new GroupDefinition("g", "Nur bei Variante B") { EntryCondition = "Variante == \"B\"" }.Add(
            probe.Mark("inside"),
            new GroupDefinition("sub", "Sub") { EntryCondition = "false" }.Add(probe.Mark("sub")));
        var loop = Group("loop").Var("Variante", VarType.String, "A").Add(group);
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock() });

        var refused = await run.ExecuteGroupTargetedAsync("g").Within();
        Assert.False(refused.Executed);
        Assert.Contains("Variante == \"B\"", refused.RefusalReason);
        Assert.Empty(probe.Marks);

        var executed = await run.ExecuteGroupTargetedAsync("g", new Dictionary<string, object> { ["Variante"] = "B" }).Within();
        Assert.True(executed.Executed);
        Assert.Equal(["inside"], probe.Marks);                    // Bedingungen enthaltener Gruppen gelten weiter
    }

    [Fact]
    public async Task D06_Variable_im_pausierten_Lauf_typgeprueft_aendern_und_protokolliert_fortsetzen()
    {
        var group = MeasureGroup(_ => StepOutcome.Unrated());
        group.Add(Steps.Code("eval", "Bewertung", ctx =>
            (double)ctx.Get("Spannung") < (double)ctx.Get("GrenzwertMin") || (double)ctx.Get("Spannung") > (double)ctx.Get("GrenzwertMax")
                ? StepOutcome.Fail("Spannung außerhalb Toleranz")
                : StepOutcome.Pass()));
        var (run, dbg) = Create(Test(Group("loop").Var("Spannung", VarType.Double, 230.0).Add(group)));
        dbg.SetBreakpoint("measure", "M. Weber");
        var paused = run.WaitFor<Paused>();
        var task = run.RunAsync();
        await paused;

        var ex = Assert.Throws<TypeMismatchException>(() => dbg.SetVariable("Spannung", "abc", "M. Weber"));
        Assert.Equal("Wert muss Zahl sein", ex.Message);
        Assert.Throws<Testumgebung.Domain.Scopes.VariableNotFoundException>(() => dbg.SetVariable("Gibts", "1", "M. Weber"));

        dbg.SetVariable("Spannung", "235,5", "M. Weber");
        var outer = dbg.ActiveVariables().Single(v => v.LookupPath == "../Spannung");
        Assert.Equal("loop", outer.OriginGroup);                  // verdeckte äußere Variable mit Herkunft sichtbar
        dbg.Continue();
        await task.Within();

        Assert.Equal(ResultRating.Pass, run.GroupResult("dut").Rating);  // Bewertung mit geändertem Wert
        Assert.True(run.IsDevelopmentRun);
        var change = Assert.Single(run.Events.OfType<DevelopmentEvent>(), e => e.Kind == "Variable geändert");
        Assert.Equal("M. Weber", change.User);
        Assert.Contains("vorher: 249", change.Details);
    }

    [Fact]
    public async Task Werkerabbruch_waehrend_Pause_setzt_fort_und_startet_Exit_Folge()
    {
        var probe = new Probe();
        var dut = MeasureGroup(_ => StepOutcome.Fail());
        dut.ExitGroup = Group("dut.exit").Add(probe.Mark("exit"));
        var loop = new GroupDefinition("loop", "Loop") { Repetition = Repetition.Endless }.Add(dut);
        var (run, dbg) = Create(Test(loop));
        dbg.SetBreakpoint("measure");
        var paused = run.WaitFor<Paused>();
        var task = run.RunAsync();
        await paused;

        run.RequestAbort();
        var status = await task.Within();

        Assert.Equal(["exit"], probe.Marks);
        Assert.Empty(run.Steps("next"));
        Assert.Equal(new RunStatus(ResultRating.Fail, true), status);
    }
}
