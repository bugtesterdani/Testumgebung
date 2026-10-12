using System.Collections.Concurrent;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Werker, der Antworten nach Schritt-ID liefert (für Tests).</summary>
internal sealed class ScriptedOperator : IOperatorInterface
{
    private readonly ConcurrentDictionary<string, Func<OperatorPrompt, object?>> _answers = new();
    public ConcurrentQueue<OperatorPrompt> Asked { get; } = new();
    public Action<OperatorPrompt>? OnAsk { get; set; }

    public ScriptedOperator Answer(string stepName, Func<OperatorPrompt, object?> answer)
    {
        _answers[stepName] = answer;
        return this;
    }

    public Task<object?> AskAsync(string runId, OperatorPrompt prompt, CancellationToken cancellationToken)
    {
        Asked.Enqueue(prompt);
        OnAsk?.Invoke(prompt);
        return Task.FromResult(_answers.TryGetValue(prompt.StepName, out var f) ? f(prompt) : null);
    }
}

public class ProjectAndOperatorTests
{
    private readonly ProjectSerializer _serializer = new();

    [Fact]
    public void Projektdatei_Rundreise_erhaelt_Struktur_IDs_und_Parameter()
    {
        var project = SampleProjects.AcPruefung();
        var json = _serializer.Serialize(project);
        var loaded = _serializer.Deserialize(json);

        Assert.Equal(json, _serializer.Serialize(loaded));
        Assert.Contains("\"art\": \"gruppe\"", json);
        Assert.Contains("\"block\": \"analog.messen\"", json);
        var original = project.Definition.Root.Descendants().Select(n => n.Id).ToList();
        Assert.Equal(original, loaded.Definition.Root.Descendants().Select(n => n.Id).ToList());
        var belastung = (GroupDefinition)loaded.Definition.Find("belastung")!;
        Assert.Equal(3, belastung.Repetition.Count);
        Assert.Equal(RepeatResultPolicy.ResetToNResult, belastung.ResultBeforeRepeat);
        Assert.Equal(["AC_Quelle"], belastung.Resources);
        Assert.Equal(TimeSpan.FromSeconds(30), belastung.ResourceTimeout);
        Assert.Equal("Exit – Abschalten", belastung.ExitGroup!.Name);
        Assert.True(((GroupDefinition)loaded.Definition.Find("pruefling")!).IsDutRun);
        Assert.Equal(230.0, loaded.Definition.Root.Variables.Single(v => v.Name == "Nennspannung").Initial);
        Assert.Equal(7, loaded.Simulation.Count);
        Assert.Empty(loaded.Definition.Validate());
    }

    [Fact]
    public void Schritte_ohne_Katalogbaustein_sind_nicht_speicherbar()
    {
        var def = Test(Group("loop").Add(Steps.Rate("code", ResultRating.Pass)));
        var ex = Assert.Throws<ProjectFormatException>(() => _serializer.Serialize(new TestProject(def)));
        Assert.Contains("code", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("kein json")]
    [InlineData("{\"format\":\"testumgebung.projekt\",\"formatVersion\":99}")]
    public void Ungueltige_Projektdateien_werden_abgelehnt(string json)
        => Assert.Throws<ProjectFormatException>(() => _serializer.Deserialize(json));

    [Fact]
    public async Task Fehlkonfigurierter_Baustein_liefert_Diagnose_und_ERROR()
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Measure, "m", "Messung",
            new Dictionary<string, string> { ["Ressource"] = "X", ["Untergrenze"] = "1 +" });
        var diagnostics = BlockCatalog.Default.Rebuild(step);
        Assert.Contains(diagnostics, d => d.Contains("Untergrenze"));

        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { Clock = new VirtualClock() });
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Error, status.Rating);
        Assert.Contains("Konfigurationsfehler", run.Steps("m").Single().Message);

        step.Parameters["Untergrenze"] = "1";
        Assert.Empty(BlockCatalog.Default.Rebuild(step));
    }

    [Fact]
    public async Task Katalogbausteine_messen_bewerten_und_setzen_Werte()
    {
        var c = BlockCatalog.Default;
        var sim = new Testumgebung.Hardware.SimulationAdapter();
        sim.SetInput("ai0", 249.0);
        var hw = new Testumgebung.Hardware.HardwareRegistry().Bind("U", sim, "ai0").Bind("Q", sim, "ao0").Bind("D", sim, "di0");
        sim.SetInput("di0", 1);
        var loop = Group("loop").Var("Min", VarType.Double, 220.0).Var("U", VarType.Double).Var("Ok", VarType.Bool).Var("N", VarType.Int).Add(
            c.Create(BlockCatalog.Measure, "m", "Messung", new Dictionary<string, string> { ["Ressource"] = "U", ["Ziel"] = "U", ["Untergrenze"] = "Min", ["Obergrenze"] = "240" }),
            c.Create(BlockCatalog.Evaluate, "e", "Bewertung", new Dictionary<string, string> { ["Bedingung"] = "U > 245", ["Wert"] = "U", ["Einheit"] = "V" }),
            c.Create(BlockCatalog.SetValue, "s", "Wert", new Dictionary<string, string> { ["Ziel"] = "N", ["Ausdruck"] = "2 * 21" }),
            c.Create(BlockCatalog.Output, "o", "Ausgabe", new Dictionary<string, string> { ["Ressource"] = "Q", ["Wert"] = "U / 2" }),
            c.Create(BlockCatalog.DigitalRead, "d", "Digital", new Dictionary<string, string> { ["Ressource"] = "D", ["Ziel"] = "Ok", ["Erwartet"] = "true" }),
            c.Create(BlockCatalog.Wait, "w", "Warten", new Dictionary<string, string> { ["Dauer"] = "250" }),
            c.Create(BlockCatalog.SetRating, "r", "Bewertung setzen", new Dictionary<string, string> { ["Bewertung"] = "PASS" }));
        var run = new TestRun(Test(loop), new TestRunOptions { Clock = new VirtualClock(), Hardware = hw });
        var status = await run.RunAsync().Within();

        var m = run.Steps("m").Single();
        Assert.Equal(ResultRating.Fail, m.Rating);
        Assert.Equal(220.0, m.LowLimit);
        Assert.Equal(240.0, m.HighLimit);
        Assert.Equal(ResultRating.Pass, run.Steps("e").Single().Rating);
        Assert.Equal(42L, run.Steps("s").Single().Value);
        Assert.Equal(124.5, sim.GetOutput("ao0"));
        Assert.Equal(ResultRating.Pass, run.Steps("d").Single().Rating);
        Assert.Equal(ResultRating.Fail, status.Rating);
    }

    [Fact]
    public async Task Werkerinteraktion_Eingabe_Auswahl_Anweisung_und_Anzeigedauer()
    {
        var c = BlockCatalog.Default;
        var group = Group("dut", "Prüfling").Var("Temp", VarType.Double).Var("Sicht", VarType.String).Add(
            c.Create(BlockCatalog.Operator, "t", "Temperatur", new Dictionary<string, string>
            {
                ["Art"] = "Eingabe", ["Text"] = "Temperatur", ["Datentyp"] = "Zahl", ["Min"] = "0", ["Max"] = "50", ["Ziel"] = "Temp",
                ["Anzeige"] = BlockCatalog.DisplayModes[1],
            }),
            c.Create(BlockCatalog.Operator, "s", "Sicht", new Dictionary<string, string>
            {
                ["Art"] = "Auswahl", ["Text"] = "Sichtprüfung", ["ErlaubteWerte"] = "i. O.;n. i. O.", ["Ziel"] = "Sicht",
            }),
            c.Create(BlockCatalog.Operator, "hinweis", "Hinweis", new Dictionary<string, string>
            {
                ["Art"] = "Anweisung", ["Text"] = "Nur Hinweis", ["Warten"] = "false",
            }),
            c.Create(BlockCatalog.Operator, "a", "Anschluss", new Dictionary<string, string> { ["Art"] = "Anweisung", ["Text"] = "Anschließen" }));
        var op = new ScriptedOperator()
            .Answer("Temperatur", _ => "22,5")
            .Answer("Sicht", _ => "i. O.")
            .Answer("Anschluss", _ => true);
        var states = new List<EngineState>();
        op.OnAsk = _ => { };
        var run = new TestRun(Test(Group("loop").Add(group)), new TestRunOptions { Clock = new VirtualClock(), Operator = op });
        run.EventRaised += e => { if (e is StateChanged sc) lock (states) states.Add(sc.State); };
        var status = await run.RunAsync().Within();

        Assert.Equal(ResultRating.NResult, status.Rating);
        Assert.Equal(22.5, run.Steps("t").Single().Value);
        Assert.Equal("i. O.", run.Steps("s").Single().Value);
        Assert.Equal(["Temperatur", "Sicht", "Anschluss"], op.Asked.Select(p => p.StepName)); // Hinweis wartet nicht
        Assert.Contains(EngineState.AwaitingOperator, states);

        var shown = run.Events.OfType<OperatorContentShown>().ToList();
        Assert.Equal(4, shown.Count);
        Assert.Equal(OperatorDisplayMode.UntilGroupEnd, shown[0].Prompt.DisplayMode);
        var groupScope = run.Events.OfType<GroupEntered>().Single(e => e.NodeId == "dut").ScopeId;
        Assert.All(shown, s => Assert.Equal(groupScope, s.Prompt.GroupScopeId));
        Assert.Contains(run.Events.OfType<OperatorContentCleared>(), e => e.GroupScopeId == groupScope);
    }

    [Theory]
    [InlineData("abc", "Wert muss Zahl sein")]
    [InlineData("51", "Zulässiger Bereich: 0 – 50 °C")]
    [InlineData("", "Eingabe ist erforderlich")]
    public void Werkereingaben_werden_validiert(string input, string error)
    {
        var prompt = new OperatorPrompt(OperatorPromptKind.Input, "Temperatur", VarType.Double, Required: true, Min: 0, Max: 50, Unit: "°C");
        Assert.Equal(error, prompt.Validate(input).Error);
        Assert.Equal(22.5, prompt.Validate("22.5").Value);
        var choice = new OperatorPrompt(OperatorPromptKind.Choice, "Sicht", VarType.String, AllowedValues: ["i. O.", "n. i. O."]);
        Assert.Equal("Wert ist nicht erlaubt", choice.Validate("egal").Error);
    }

    [Fact]
    public async Task Ungueltige_Werkerantwort_ergibt_ERROR()
    {
        var step = BlockCatalog.Default.Create(BlockCatalog.Operator, "t", "T", new Dictionary<string, string> { ["Art"] = "Eingabe", ["Max"] = "5" });
        var run = new TestRun(Test(Group("loop").Add(step)), new TestRunOptions { Clock = new VirtualClock(), Operator = new ScriptedOperator().Answer("T", _ => "9") });
        var status = await run.RunAsync().Within();
        Assert.Equal(ResultRating.Error, status.Rating);
    }

    [Fact]
    public async Task Beispielprojekt_Abbruch_vor_Ressourcenerwerb_schaltet_keine_unreservierte_Quelle()
    {
        var project = _serializer.Clone(SampleProjects.AcPruefung());
        var (_, registry) = SampleProjects.CreateSimulation(project);
        var op = new ScriptedOperator()
            .Answer("Kennung erfassen", _ => "SN-Abbruch")
            .Answer("Sichtprüfung", _ => "i. O.")
            .Answer("Gehäusetemperatur erfassen", _ => "22,5")
            .Answer("Prüfling anschließen", _ => true);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op });
        run.EventRaised += e =>
        {
            if (e is ResourceWaitStarted { NodeId: "belastung" }) run.RequestAbort();
        };
        var status = await run.RunAsync().Within();
        Assert.True(status.OperatorAbort);
        Assert.Contains(run.Events.OfType<ResourceNotAcquired>(), e => e.NodeId == "belastung");
        Assert.DoesNotContain(run.Events.OfType<StepCompleted>(), e => e.NodeId == "belastung.aus" || e.Rating == ResultRating.Error);
        Assert.Contains(run.Events.OfType<StepCompleted>(), e => e.NodeId == "fin.reset");
    }

    [Fact]
    public async Task Beispielprojekt_laeuft_mit_Simulation_bis_zum_Werkerabbruch()
    {
        var project = _serializer.Clone(SampleProjects.AcPruefung());
        var (_, registry) = SampleProjects.CreateSimulation(project);
        var serial = 10480;
        var op = new ScriptedOperator()
            .Answer("Kennung erfassen", _ => $"SN-{Interlocked.Increment(ref serial)}")
            .Answer("Sichtprüfung", _ => "i. O.")
            .Answer("Gehäusetemperatur erfassen", _ => "22,5")
            .Answer("Prüfling anschließen", _ => true);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op });
        var secondDone = run.WaitFor<DutRunCompleted>(e => e.DutId == "SN-10482");
        var task = run.RunAsync();
        await secondDone;
        run.RequestAbort();
        var status = await task.Within();

        var duts = run.Events.OfType<DutRunCompleted>().ToList();
        Assert.True(duts.Count >= 2);
        Assert.Equal(new RunStatus(ResultRating.Pass, false), duts[0].Status);
        Assert.Equal("SN-10481", duts[0].DutId);
        Assert.True(status.OperatorAbort);
        Assert.Equal(3, run.Events.OfType<GroupCompleted>().First(g => g.NodeId == "belastung").Iterations);
        Assert.DoesNotContain(run.Events.OfType<StepCompleted>(), s => s.Rating == ResultRating.Error);
    }
}
