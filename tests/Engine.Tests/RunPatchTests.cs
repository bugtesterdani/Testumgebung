using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>D06 vollständig: Code/Parameter noch nicht ausgeführter Elemente im pausierten Lauf ändern.</summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public class RunPatchTests
{
    private static StepDefinition C(string type, string id, params (string K, string V)[] p)
        => BlockCatalog.Default.Create(type, id, id, p.ToDictionary(x => x.K, x => x.V));

    private static (TestRun Run, DebugController Dbg) Create()
    {
        var group = new GroupDefinition("g", "Prüfling").Var("Spannung", VarType.Double, 249.0).Add(
            C(BlockCatalog.SetRating, "rate", ("Bewertung", "PASS")),
            C(BlockCatalog.Evaluate, "eval", ("Bedingung", "Spannung <= 240")),
            new GroupDefinition("later", "Später") { EntryCondition = "false" }.Add(Steps.Rate("later.step", ResultRating.Pass)));
        var dbg = new DebugController();
        dbg.SetBreakpoint("rate");
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg });
        return (run, dbg);
    }

    [Fact]
    public async Task D06_Patch_fuer_noch_nicht_ausgefuehrte_Elemente_wird_geprueft_uebernommen_und_protokolliert()
    {
        var (run, dbg) = Create();
        Assert.False(run.ValidatePatch([new PatchChange("eval", "param:Bedingung", "true")]).IsValid); // nicht pausiert

        var paused = run.WaitFor<Paused>();
        var task = run.RunAsync();
        await paused;

        // Ungültig: Syntaxfehler, aktive Gruppe, unbekanntes Element
        var bad = run.ValidatePatch([new PatchChange("eval", "param:Bedingung", "Spannung <=")]);
        Assert.False(bad.IsValid);
        Assert.Contains(bad.Diagnostics, d => d.Contains("Bedingung"));
        Assert.Contains(run.ValidatePatch([new PatchChange("g", "condition", "true")]).Diagnostics, d => d.Contains("aktiv"));
        Assert.Contains(run.ValidatePatch([new PatchChange("nix", "name", "x")]).Diagnostics, d => d.Contains("existiert nicht"));
        Assert.False(run.ApplyPatch([new PatchChange("eval", "param:Bedingung", "Spannung <=")], "M. Weber").Applied);
        Assert.Equal("Spannung <= 240", ((StepDefinition)run.Definition.Find("eval")!).Parameters["Bedingung"]); // nichts geändert

        // Gültig: Bedingung des nächsten Schritts und einer späteren Gruppe
        var ok = run.ApplyPatch(
        [
            new PatchChange("eval", "param:Bedingung", "Spannung <= 250"),
            new PatchChange("later", "condition", "true"),
        ], "M. Weber");
        Assert.True(ok.Applied);
        Assert.Equal(1, ok.PatchNumber);
        Assert.True(run.IsDevelopmentRun);
        Assert.Contains(run.Events.OfType<DevelopmentEvent>(), e => e.Kind == "Run-Patch 1 angewendet" && e.User == "M. Weber");

        dbg.Continue();
        var status = await task.Within();
        Assert.Equal(ResultRating.Pass, run.Steps("eval").Single().Rating);   // gepatchte Bedingung
        Assert.Single(run.Steps("later.step"));                               // gepatchte Gruppe betreten
        Assert.Equal(ResultRating.Pass, status.Rating);
    }

    [Fact]
    public async Task D06_Patch_des_pausierten_Schritts_wirkt_bei_Erneut_ausfuehren_und_nicht_rueckwirkend()
    {
        var (run, dbg) = Create();
        var first = run.WaitFor<Paused>(p => p.Info.ExecutionCount == 1);
        var second = run.WaitFor<Paused>(p => p.Info.ExecutionCount == 2);
        var task = run.RunAsync();
        Assert.Equal(ResultRating.Pass, (await first).Info.PendingRating);

        Assert.True(run.ApplyPatch([new PatchChange("rate", "param:Bewertung", "FAIL")], "M. Weber").Applied);
        Assert.Single(run.Steps("rate")); // kein automatisches Wiederholen
        dbg.Rerun();
        Assert.Equal(ResultRating.Fail, (await second).Info.PendingRating);
        dbg.Continue();
        await task.Within();
        Assert.Equal(ResultRating.Fail, run.GroupResult("g").Rating);
    }

    [Fact]
    public async Task Patch_Editor_im_Debugger_aendert_nur_den_Lauf_nicht_den_Entwurf()
    {
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: SampleProjects.AcPruefung(), clock: new VirtualClock());
        shell.Login();
        var s = shell.SelectedStation;
        shell.Editor.ToggleBreakpoint(shell.Editor.FindNode("kennung"));
        _ = s.StartAsync();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true }, "Kennung");
        s.ActiveContent!.Input = "SN-P";
        s.ActiveContent.Confirm();
        await ui.Until(() => s.Debug.IsPaused, "Pause");

        var patch = s.Debug.Patch;
        Assert.Contains("Kennung erfassen", patch.Title);           // pausiertes Element vorausgewählt
        shell.Editor.SelectedNode = shell.Editor.FindNode("spannung");
        Assert.Contains("Messung Spannung", patch.Title);
        var upper = patch.Rows.Single(r => r.Property == "param:Obergrenze");
        upper.Value = "GrenzwertMax +";
        Assert.False(patch.Validate());
        upper.Value = "200";
        Assert.True(patch.Validate());
        Assert.True(patch.Apply());
        Assert.Equal(1, patch.PatchCount);
        Assert.Equal("GrenzwertMax", ((StepDefinition)shell.Editor.Project.Definition.Find("spannung")!).Parameters["Obergrenze"]); // Entwurf unverändert

        s.Debug.ContinueCommand.Execute(null);
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Sichtprüfung" }, "Sicht");
        s.ActiveContent!.ChooseCommand.Execute("i. O.");
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Gehäusetemperatur erfassen" }, "Temp");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.ActiveContent is { IsAwaiting: true, Title: "Prüfling anschließen" }, "Anschluss");
        s.ActiveContent!.Confirm();
        await ui.Until(() => s.Protocol.Any(p => p.Text == "Messung Spannung"), "Messung");
        Assert.Equal(ResultRating.Fail, s.Protocol.First(p => p.Text == "Messung Spannung").Rating); // gepatchte Grenze
        s.RequestAbort();
        await ui.Until(() => !s.IsRunning && s.LastStatus is not null, "Ende");
    }

    [Fact]
    public async Task D06_Teil2_Struktur_nicht_aktiver_Gruppen_loeschen_und_verschieben()
    {
        var group = new GroupDefinition("g", "Prüfling").Add(
            Steps.Rate("a", ResultRating.Pass, "A"),
            new GroupDefinition("spaeter", "Später").Add(
                Steps.Rate("s1", ResultRating.Pass, "S1"),
                Steps.Rate("s2", ResultRating.Fail, "S2"),
                Steps.Rate("s3", ResultRating.Pass, "S3")));
        var dbg = new DebugController();
        dbg.SetBreakpoint("a");
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg });
        var paused = run.WaitFor<Paused>(p => p.Info.NodeId == "a");
        var task = run.RunAsync();
        await paused;

        // s2 (FAIL) löschen und s3 vor s1 ziehen – „spaeter“ ist noch nicht aktiv
        Assert.Contains(run.ValidatePatch([new PatchChange("g", "delete", null)]).Diagnostics, d => d.Contains("aktiv"));  // g ist aktiv
        var ok = run.ApplyPatch(
        [
            new PatchChange("s2", "delete", null),
            new PatchChange("s3", "move", "-2"),
        ], "M. Weber");
        Assert.True(ok.Applied, string.Join(";", ok.Diagnostics));

        dbg.Continue();
        await task.Within();
        Assert.Empty(run.Steps("s2"));                                   // gelöscht → lief nicht
        Assert.Equal(ResultRating.Pass, run.GroupResult("spaeter").Rating); // ohne den FAIL-Schritt
        var order = run.Events.OfType<StepCompleted>().Where(e => e.NodeId is "s1" or "s3").Select(e => e.NodeId).ToList();
        Assert.Equal(["s3", "s1"], order);                               // s3 vorgezogen
    }

    [Fact]
    public async Task D06_Teil2_Bibliotheksbaustein_Quelltext_patchen_wirkt_auf_folgende_Aufrufe()
    {
        var block = new Testumgebung.Domain.Code.CodeBlockDefinition("lib", "Prüfen")
        {
            Language = Testumgebung.Domain.Code.CodeLanguage.Python,
            Source = "return Fail(\"alt\")",
        };
        block.Ports.Add(new Testumgebung.Domain.Code.CodePort("Wert", VarType.Double, Testumgebung.Domain.Code.PortDirection.Output));
        var call1 = C(BlockCatalog.LibraryBlock, "c1", ("Baustein", "lib"), ("out.Wert", "A"));
        var call2 = C(BlockCatalog.LibraryBlock, "c2", ("Baustein", "lib"), ("out.Wert", "B"));
        var def = Test(Group("loop").Add(new GroupDefinition("g", "G").Var("A", VarType.Double).Var("B", VarType.Double).Add(call1, call2)));
        def.Library.Add(block);

        var dbg = new DebugController();
        dbg.SetBreakpoint("c1");   // Haltepunkte halten nach der Ausführung – c1 lief noch mit der alten Quelle
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg, CodeRuntime = CodeBlockTests.SharedRuntime.Value });
        var paused = run.WaitFor<Paused>(p => p.Info.NodeId == "c1");
        var task = run.RunAsync();
        await paused.Within();

        Assert.Equal(ResultRating.Fail, run.Steps("c1").Single().Rating);          // erster Aufruf: alte Quelle
        Assert.True(run.ApplyPatch([new PatchChange("lib", "code", "return Pass(42)")], "M. Weber").Applied);
        Assert.Contains(run.Events.OfType<DevelopmentEvent>(), e => e.Kind == "Run-Patch 1 angewendet");
        dbg.Continue();
        await task.Within();
        Assert.Equal(ResultRating.Pass, run.Steps("c2").Single().Rating);          // zweiter Aufruf: gepatchte Quelle
    }

}

