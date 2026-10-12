using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Grafische Bausteine aus Gruppen: Schnittstelle, Bausteingrenze, Abbruch, Umwandeln, Speicherung.</summary>
public class GroupBlockTests
{
    private static StepDefinition C(string type, string id, params (string K, string V)[] p)
        => BlockCatalog.Default.Create(type, id, id, p.ToDictionary(x => x.K, x => x.V));

    private static GroupBlockDefinition Bewertung(Probe probe)
    {
        var body = new GroupDefinition("bew.koerper", "Spannungsbewertung").Var("Delta", VarType.Double).Add(
            C(BlockCatalog.SetValue, "bew.delta", ("Ziel", "Delta"), ("Ausdruck", "Spannung - Sollwert")),
            C(BlockCatalog.SetValue, "bew.ok", ("Ziel", "IstWertOK"), ("Ausdruck", "Delta <= Toleranz && Delta >= -Toleranz")),
            C(BlockCatalog.Evaluate, "bew.rating", ("Bedingung", "IstWertOK")),
            probe.Gate("bew.gate"));
        body.ExitGroup = Group("bew.exit", "Exit Baustein").Add(probe.Mark("bew.exit.step", "exit:baustein"));
        var block = new GroupBlockDefinition("bewertung", "Spannungsbewertung", body) { Version = "1.0" };
        block.Ports.AddRange(
        [
            new CodePort("Spannung", VarType.Double, PortDirection.Input, "V"),
            new CodePort("Sollwert", VarType.Double, PortDirection.Input, "V"),
            new CodePort("Toleranz", VarType.Double, PortDirection.Input, "V"),
            new CodePort("IstWertOK", VarType.Bool, PortDirection.Output),
        ]);
        return block;
    }

    [Fact]
    public async Task Gruppenbaustein_mit_Ports_Bausteingrenze_und_Ausgaengen()
    {
        var probe = new Probe();
        probe.Release("bew.gate");
        var call = C(BlockCatalog.GroupBlock, "call", ("Baustein", "bewertung"), ("in.Spannung", "U"), ("in.Sollwert", "230"),
            ("in.Toleranz", "U * 0.02"), ("out.IstWertOK", "Ok"));
        var call2 = C(BlockCatalog.GroupBlock, "call2", ("Baustein", "bewertung"), ("in.Spannung", "250"), ("in.Sollwert", "230"),
            ("in.Toleranz", "1"), ("out.IstWertOK", "Ok2"));
        var loop = Group("loop").Var("U", VarType.Double, 233.0).Var("Ok", VarType.Bool).Var("Ok2", VarType.Bool, true).Var("Delta", VarType.Double, 99.0)
            .Add(call, call2, Steps.Code("read", "lesen", ctx => StepOutcome.Unrated($"{ctx.Get("Ok")}|{ctx.Get("Ok2")}|{ctx.Get("Delta")}")));
        var def = Test(loop);
        def.GroupLibrary.Add(Bewertung(probe));
        Assert.Empty(def.Validate());

        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock() });
        var status = await run.RunAsync().Within();

        Assert.True(run.Steps("call").Single().Rating == ResultRating.Pass,
            string.Join(" | ", run.Events.OfType<StepCompleted>().Select(x => $"{x.NodeId} {x.Rating} {x.Message}")));
        Assert.Equal(ResultRating.Fail, run.Steps("call2").Single().Rating);
        Assert.Equal("True|False|99", run.Steps("read").Single().Value); // äußeres Delta unberührt (Bausteingrenze)
        Assert.Equal(2, probe.Marks.Count(m => m == "exit:baustein"));
        Assert.Contains(run.Events.OfType<StepCompleted>(), e => e.NodeId == "bew.delta" && e.Path.Contains("Spannungsbewertung"));
        Assert.Equal(ResultRating.Fail, status.Rating);
    }

    [Fact]
    public async Task Koerper_sieht_keine_aeusseren_Variablen()
    {
        var body = new GroupDefinition("k", "Körper").Add(C(BlockCatalog.SetValue, "k.set", ("Ziel", "Aussen"), ("Ausdruck", "1")));
        var def = Test(Group("loop").Var("Aussen", VarType.Int).Add(C(BlockCatalog.GroupBlock, "call", ("Baustein", "b"))));
        def.GroupLibrary.Add(new GroupBlockDefinition("b", "B", body));
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock() });
        await run.RunAsync().Within();
        var set = run.Steps("k.set").Single();
        Assert.Equal(ResultRating.Error, set.Rating);
        Assert.Contains("Aussen", set.Message);
    }

    [Fact]
    public async Task Werkerabbruch_im_Bausteinkoerper_fuehrt_Exits_von_innen_nach_aussen_aus()
    {
        var probe = new Probe();
        var call = C(BlockCatalog.GroupBlock, "call", ("Baustein", "bewertung"), ("in.Spannung", "230"), ("in.Sollwert", "230"), ("in.Toleranz", "1"));
        var outer = Group("outer").Add(call, probe.Mark("after"));
        outer.ExitGroup = Group("outer.exit").Add(probe.Mark("outer.exit.step", "exit:aussen"));
        var def = Test(new GroupDefinition("loop", "Loop") { Repetition = Repetition.Endless }.Add(outer));
        def.GroupLibrary.Add(Bewertung(probe));
        var dbg = new DebugController();
        dbg.SetBreakpoint("bew.rating"); // Haltepunkte im Körper funktionieren
        var run = new TestRun(def, new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg });
        var paused = run.WaitFor<Paused>();
        var task = run.RunAsync();
        Assert.Equal("bew.rating", (await paused).Info.NodeId);
        dbg.Continue();
        await probe.WhenReached("bew.gate");
        run.RequestAbort();
        var status = await task.Within();

        Assert.Equal(["bew.gate", "exit:baustein", "exit:aussen"], probe.Marks);
        Assert.True(run.Steps("call").Single().Cancelled);
        Assert.True(status.OperatorAbort);
    }

    [Fact]
    public async Task Aus_Gruppe_erstellen_erkennt_Ports_und_verhaelt_sich_gleich()
    {
        var serializer = new ProjectSerializer();
        var original = SampleProjects.AcPruefung();
        var converted = serializer.Clone(original);
        var group = (GroupDefinition)converted.Definition.Find("belastung")!;

        var deps = GroupBlockConverter.Analyze(converted.Definition, group, BlockCatalog.Default);
        Assert.Equal(["Nennspannung:Input"], deps.Ports.Select(p => $"{p.Name}:{p.Direction}"));

        var (block, step, warnings) = GroupBlockConverter.Convert(converted.Definition, group, "belastung-b", BlockCatalog.Default);
        Assert.Empty(warnings);
        Assert.Equal("belastung", step.Id);
        Assert.Equal(BlockCatalog.GroupBlock, step.BlockType);
        Assert.Equal("Nennspannung", step.Parameters["in.Nennspannung"]);
        Assert.Equal(["AC_Quelle"], block.Body.Resources);
        Assert.Empty(converted.Definition.Validate());

        // Speichern und Laden
        var json = serializer.Serialize(converted);
        Assert.Contains("\"groupLibrary\"", json);
        var loaded = serializer.Deserialize(json);
        Assert.Equal(json, serializer.Serialize(loaded));

        async Task<List<string>> RunOnce(TestProject project)
        {
            var (_, registry) = SampleProjects.CreateSimulation(project);
            var op = new ScriptedOperator()
                .Answer("Kennung erfassen", _ => "SN-1").Answer("Sichtprüfung", _ => "i. O.")
                .Answer("Gehäusetemperatur erfassen", _ => "22").Answer("Prüfling anschließen", _ => true);
            var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op });
            var done = run.WaitFor<DutRunCompleted>();
            var task = run.RunAsync();
            await done;
            run.RequestAbort();
            await task.Within();
            // Nur der erste Prüflingsdurchlauf – der Dauerlauf kann vor dem Abbruch bereits den nächsten begonnen haben.
            var events = run.Events.ToList();
            var end = events.FindIndex(e => e is DutRunCompleted);
            return events.Take(end).OfType<StepCompleted>().Where(s => s.NodeId.StartsWith("belastung.")).Select(s => $"{s.NodeId}:{s.Rating}").ToList();
        }

        var a = await RunOnce(serializer.Clone(original));
        var b = await RunOnce(loaded);
        Assert.Equal(a, b);
        Assert.Equal(13, a.Count); // 3 × 4 Schritte + Exit
    }

    [Fact]
    public void Editor_Aus_Gruppe_erstellen_zeigt_Koerper_als_Abschnitt_und_Bindungen()
    {
        var shell = new ShellViewModel(new QueueDispatcher(), project: SampleProjects.AcPruefung(), clock: new VirtualClock());
        shell.Login();
        var e = shell.Editor;
        e.SelectedNode = e.FindNode("belastung");
        var props = (GroupPropertiesViewModel)e.Properties!;
        Assert.True(props.CanCreateBlock);
        props.CreateBlockCommand.Execute(null);

        Assert.Contains("Grafischer Baustein", e.Notice);
        var callNode = e.FindNode("belastung")!;
        Assert.Equal(NodeIcon.LibraryBlock, callNode.Icon);
        Assert.Same(callNode, e.SelectedNode);
        var bodySection = e.Nodes.Last();
        Assert.True(bodySection.IsSection);
        Assert.Contains("Nennspannung", bodySection.Annotation);
        Assert.NotNull(e.FindNode("belastung.messung"));
        Assert.Empty(e.Diagnostics);

        var bindings = ((StepPropertiesViewModel)e.Properties!).Bindings!;
        Assert.Equal("Messgruppe – Belastung", bindings.SelectedBlock!.Name);
        Assert.Equal(["Nennspannung"], bindings.Rows.Select(r => r.Name));

        // Bibliothek zeigt den grafischen Baustein; Navigation zum Körper
        var lib = shell.Library;
        var g = Assert.Single(lib.GroupBlocks);
        Assert.Contains(lib.Items, i => i == g);
        Assert.Single(g.UsedIn);
        lib.SelectedItem = g;
        lib.DeleteCommand.Execute(null);
        Assert.Contains("wird noch verwendet", lib.Message);
        g.OpenInEditorCommand.Execute(null);
        Assert.Equal(ShellPage.Editor, shell.Page);
        Assert.Same(bodySection, e.SelectedNode);

        // Ports im Körper sichtbar (z. B. für CodeBlocks)
        Assert.Contains(("Nennspannung", VarType.Double), CodeBlockConverter.VisibleVariables(e.Project.Definition, "belastung.messung"));
    }
}
