using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>Abnahmetests L01–L02 (Abschnitt 7: Ressourcen und Parallelbetrieb).</summary>
public class ResourceTests
{
    private readonly VirtualClock _clock = new(autoAdvance: false);
    private readonly SimulationAdapter _sim = new SimulationAdapter("AC-Leistungsquelle (Simulation)")
        .Channel("L1", new ChannelCapabilities("V", 0, 300));
    private readonly ResourceManager _resources;
    private readonly HardwareRegistry _hardware;

    public ResourceTests()
    {
        _resources = new ResourceManager(_clock);
        _hardware = new HardwareRegistry()
            .Bind("AC_Quelle", _sim, "L1", requiresLock: true)
            .Bind("Spannung_Pruefling", _sim, "ai0");
    }

    private TestRun Station(string runId, GroupDefinition group, Probe probe)
    {
        var def = new TestDefinition("AC-Prüfung", "1.4", Group($"{runId}.init"), Group($"{runId}.loop", "Test Loop").Add(group), Group($"{runId}.fin"));
        return new TestRun(def, new TestRunOptions { Clock = _clock, Resources = _resources, Hardware = _hardware, StationName = runId }, runId);
    }

    private static GroupDefinition Messgruppe(string prefix, Probe probe, TimeSpan? timeout = null)
    {
        var g = new GroupDefinition($"{prefix}.g", "Messgruppe") { ResourceTimeout = timeout };
        g.Resources.Add("AC_Quelle");
        g.Add(
            Steps.Output($"{prefix}.on", "AC-Ausgabe 230 V", "AC_Quelle", 230),
            probe.Gate($"{prefix}.gate"),
            probe.Mark($"{prefix}.regular"));
        var acquired = (bool?)null;
        g.ExitGroup = Group($"{prefix}.exit", "Exit – Abschalten").Add(
            Steps.Code($"{prefix}.exit.check", "Erwerbsstatus", ctx =>
            {
                acquired = (bool)ctx.Get(TestRun.ResourcesAcquiredVariable);
                probe.Record($"{prefix}.exit:{acquired}");
                return StepOutcome.Unrated();
            }),
            Steps.Output($"{prefix}.off", "AC-Ausgabe stoppen", "AC_Quelle", 0));
        return g;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Kit.Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Bedingung nicht erreicht.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task L01_Zwei_Pruefplaetze_gemeinsame_Hardware_exklusiv_Sperre_bleibt_im_Exit()
    {
        var probe = new Probe();
        var order = new List<string>();
        var a = Station("Run-A", Messgruppe("a", probe), probe);
        var b = Station("Run-B", Messgruppe("b", probe), probe);
        foreach (var run in new[] { a, b })
            run.EventRaised += e =>
            {
                if (e is ResourceAcquired or ResourceReleased) lock (order) order.Add($"{e.RunId}:{e.GetType().Name}");
            };

        var taskA = a.RunAsync();
        await probe.WhenReached("a.gate");
        var taskB = b.RunAsync();
        await b.WaitFor<ResourceWaitStarted>();
        await Until(() => b.State == EngineState.WaitingResource);

        var snapshot = _resources.Snapshot().Single(s => s.Resource == "AC_Quelle");
        Assert.Equal("Run-A", snapshot.OwnerRunId);
        Assert.Equal(["Run-B"], snapshot.WaitingRuns);
        Assert.Empty(b.Steps("b.on"));

        probe.Release("a.gate");
        await probe.WhenReached("b.gate");
        probe.Release("b.gate");
        var statusA = await taskA.Within();
        var statusB = await taskB.Within();

        Assert.Equal(["Run-A:ResourceAcquired", "Run-A:ResourceReleased", "Run-B:ResourceAcquired", "Run-B:ResourceReleased"], order);
        // Exit von A durfte die reservierte Hardware noch verwenden
        Assert.Equal(ResultRating.NResult, a.Steps("a.off").Single().Rating);
        Assert.Equal([230.0, 0.0, 230.0, 0.0], _sim.WriteLog.Select(w => w.Value));
        Assert.Equal(ResultRating.NResult, statusA.Rating);
        Assert.Equal(ResultRating.NResult, statusB.Rating);
        Assert.Contains("a.exit:True", probe.Marks);
    }

    [Fact]
    public async Task L02_Locktimeout_ergibt_ERROR_keine_regulaeren_Schritte_kein_unreservierter_Zugriff()
    {
        var probe = new Probe();
        var a = Station("Run-A", Messgruppe("a", probe), probe);
        var b = Station("Run-B", Messgruppe("b", probe, timeout: TimeSpan.FromSeconds(30)), probe);

        var taskA = a.RunAsync();
        await probe.WhenReached("a.gate");
        var taskB = b.RunAsync();
        await Until(() => _clock.PendingTimers == 1);

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(taskB.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var statusB = await taskB.Within();

        Assert.Equal(ResultRating.Error, statusB.Rating);
        Assert.Contains(b.Events.OfType<ResourceNotAcquired>(), e => e.Reason.Contains("Zeitlimit"));
        Assert.Empty(b.Steps("b.on"));                               // reguläre Schritte übersprungen
        Assert.DoesNotContain("b.regular", probe.Marks);
        Assert.Contains("b.exit:False", probe.Marks);                // Exit läuft, RessourcenErhalten = false
        var off = b.Steps("b.off").Single();
        Assert.Equal(ResultRating.Error, off.Rating);                // kein unreservierter Hardwarezugriff
        Assert.Contains("ohne Reservierung", off.Message);
        Assert.Equal([230.0], _sim.WriteLog.Select(w => w.Value));  // nur A hat geschrieben

        probe.Release("a.gate");
        await taskA.Within();
    }

    [Fact]
    public async Task Abbruch_waehrend_des_Wartens_entfernt_die_Anforderung()
    {
        var probe = new Probe();
        var a = Station("Run-A", Messgruppe("a", probe), probe);
        var b = Station("Run-B", Messgruppe("b", probe), probe);
        var taskA = a.RunAsync();
        await probe.WhenReached("a.gate");
        var taskB = b.RunAsync();
        await Until(() => _resources.Snapshot().Any(s => s.WaitingRuns.Contains("Run-B")));

        b.RequestAbort();
        var statusB = await taskB.Within();

        Assert.True(statusB.OperatorAbort);
        Assert.Empty(_resources.Snapshot().Single(s => s.Resource == "AC_Quelle").WaitingRuns);
        Assert.Contains(b.Events.OfType<ResourceNotAcquired>(), e => e.Reason.Contains("Werkerabbruch"));
        Assert.Equal(ResultRating.Error, b.Steps("b.off").Single().Rating);
        probe.Release("a.gate");
        await taskA.Within();
        Assert.Empty(_resources.Snapshot());
    }

    [Fact]
    public async Task Verschachtelte_Gruppen_desselben_Runs_referenzzaehlen_eigene_Sperren()
    {
        var probe = new Probe();
        var inner = new GroupDefinition("inner", "Innen");
        inner.Resources.Add("AC_Quelle");
        inner.Add(Steps.Output("inner.on", "an", "AC_Quelle", 100));
        var outer = new GroupDefinition("outer", "Außen");
        outer.Resources.Add("AC_Quelle");
        outer.Add(inner, Steps.Output("outer.after", "nach innen", "AC_Quelle", 50));

        var run = Station("Run-A", outer, probe);
        var status = await run.RunAsync().Within();

        Assert.Equal(ResultRating.NResult, status.Rating);
        Assert.Equal(ResultRating.NResult, run.Steps("outer.after").Single().Rating); // Sperre nach innerem Verlassen weiter gehalten
        Assert.Empty(_resources.Snapshot());
    }

    [Fact]
    public async Task Ressourcenvermittlung_erwirbt_atomar_und_fair()
    {
        var x = await _resources.AcquireAsync(new ResourceRequest("A", "s1", ["X"]), null, CancellationToken.None);
        var bTask = _resources.AcquireAsync(new ResourceRequest("B", "s2", ["X", "Y"]), null, CancellationToken.None);
        var cTask = _resources.AcquireAsync(new ResourceRequest("C", "s3", ["Y"]), null, CancellationToken.None);

        Assert.False(bTask.IsCompleted);
        Assert.False(cTask.IsCompleted);                    // C überholt B nicht (FIFO auf Y)
        Assert.False(_resources.IsHeldBy("Y", "B"));        // kein Teilbesitz während des Wartens

        x.Dispose();
        var b = await bTask.Within();
        Assert.True(_resources.IsHeldBy("X", "B") && _resources.IsHeldBy("Y", "B"));
        Assert.False(cTask.IsCompleted);
        b.Dispose();
        using var c = await cTask.Within();
        Assert.True(_resources.IsHeldBy("Y", "C"));
    }
}
