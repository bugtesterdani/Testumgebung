using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Identity;
using Testumgebung.Persistence;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Abschnitt 15 („noch zu entscheiden“), jetzt umgesetzt: Zeitspanne innerhalb der Iteration, Einzelschritt/Prozedurschritt/
/// bis Gruppenende, Rechte über Entra-App-Rollen.
/// </summary>
public sealed class OpenPointsTests
{
    private static GroupDefinition Timed(bool within, List<string> seen)
    {
        var g = Group("zeit", "Zeitgruppe").Add(
            Steps.Wait("w1", TimeSpan.FromMilliseconds(600)),
            Steps.Wait("w2", TimeSpan.FromMilliseconds(600)),
            Steps.Code("mark", "Markierung", _ => { lock (seen) seen.Add("mark"); return StepOutcome.Unrated(); }));
        g.Repetition = Repetition.For(TimeSpan.FromSeconds(1)) with { StopWithinIteration = within };
        return g;
    }

    [Fact]
    public async Task Zeitspanne_wahlweise_auch_innerhalb_der_Iteration()
    {
        var seen = new List<string>();
        var standard = new TestRun(Test(Group("loop").Add(Timed(false, seen))), new TestRunOptions { Clock = new VirtualClock() });
        await standard.RunAsync().Within();
        Assert.Equal(["mark"], seen);                       // Frist nur zwischen Iterationen: Iteration läuft zu Ende

        seen.Clear();
        var within = new TestRun(Test(Group("loop").Add(Timed(true, seen))), new TestRunOptions { Clock = new VirtualClock() });
        await within.RunAsync().Within();
        Assert.Empty(seen);                                 // nach 1,2 s: Markierung übersprungen
        Assert.Contains(within.Events.OfType<Diagnostic>(), d => d.Message.Contains("restliche Schritte von Iteration 1 übersprungen"));

        // Projektformat
        var serializer = new ProjectSerializer();
        var zeit = Group("zeit", "Zeitgruppe");
        zeit.Repetition = Repetition.For(TimeSpan.FromSeconds(1)) with { StopWithinIteration = true };
        var project = new TestProject(Test(Group("loop").Add(zeit)));
        var json = serializer.Serialize(project);
        Assert.Contains("stopWithinIteration", json);
        Assert.True(((GroupDefinition)serializer.Deserialize(json).Definition.Find("zeit")!).Repetition.StopWithinIteration);
    }

    private static TestDefinition Nested()
        => Test(Group("loop").Add(
            Steps.Rate("a", ResultRating.Pass, "A"),
            Group("g", "G").Add(Steps.Rate("g1", ResultRating.Pass, "G1"), Steps.Rate("g2", ResultRating.Pass, "G2")),
            Steps.Rate("b", ResultRating.Pass, "B")));

    [Fact]
    public async Task Einzelschritt_Prozedurschritt_und_bis_Gruppenende()
    {
        var dbg = new DebugController();
        var run = new TestRun(Nested(), new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg });
        dbg.SetBreakpoint("a");
        var task = run.RunAsync();
        Assert.Equal("a", (await run.WaitFor<Paused>(p => p.Info.NodeId == "a").Within()).Info.NodeId);

        var nextG1 = run.WaitFor<Paused>(p => p.Info.NodeId == "g1");
        dbg.StepInto();                                     // hinein: erstes fertiges Kind der Gruppe
        await nextG1.Within();
        var nextG = run.WaitFor<Paused>(p => p.Info.NodeId == "g");
        dbg.StepOut();                                      // bis Gruppenende
        await nextG.Within();
        Assert.DoesNotContain(run.Events.OfType<Paused>(), p => p.Info.NodeId == "g2"); // g2 lief ohne Halt
        var nextB = run.WaitFor<Paused>(p => p.Info.NodeId == "b");
        dbg.StepOver();
        await nextB.Within();
        dbg.Continue();
        await task.Within();
        Assert.Equal(["a", "g1", "g", "b"], run.Events.OfType<Paused>().Select(p => p.Info.NodeId));
    }

    [Fact]
    public async Task Prozedurschritt_ueberspringt_Kinder_einer_Gruppe()
    {
        var dbg = new DebugController();
        var run = new TestRun(Nested(), new TestRunOptions { Clock = new VirtualClock(), Debugger = dbg });
        dbg.SetBreakpoint("a");
        var task = run.RunAsync();
        await run.WaitFor<Paused>(p => p.Info.NodeId == "a").Within();
        var nextG = run.WaitFor<Paused>(p => p.Info.NodeId == "g");
        dbg.StepOver();
        await nextG.Within();
        dbg.Continue();
        await task.Within();
        Assert.Equal(["a", "g"], run.Events.OfType<Paused>().Select(p => p.Info.NodeId));
    }

    [Fact]
    public void Rechte_ueber_Entra_App_Rollen_lokal_oder_beide()
    {
        var config = new AccessConfig
        {
            ClientId = "c", TenantId = "t",
            Users = [new RightsEntry { Upn = "lokal@firma.de", Develop = true }],
        };
        var roles = new UserIdentity("rolle@firma.de", "R", "Entra", IsConfirmed: true, "t", Roles: ["Testumgebung.Freigeben"]);
        var local = new UserIdentity("lokal@firma.de", "L", "Entra", IsConfirmed: true, "t", Roles: ["Testumgebung.Entwickeln", "Testumgebung.Freigeben"]);
        var unconfirmed = roles with { IsConfirmed = false };

        Assert.Equal(UserRights.None, config.RightsFor(roles));               // Standard: nur lokale Liste
        Assert.Equal(UserRights.Develop, config.RightsFor(local));
        config.RightsSource = "AppRollen";
        Assert.Equal(UserRights.Release, config.RightsFor(roles));
        Assert.Equal(UserRights.Develop | UserRights.Release, config.RightsFor(local));
        Assert.Equal(UserRights.None, config.RightsFor(unconfirmed));         // Rollen nur aus bestätigtem Token
        config.RightsSource = "Beide";
        config.ReleaseRole = "QS";
        Assert.Equal(UserRights.None, config.RightsFor(roles));
        Assert.Equal(UserRights.Develop, config.RightsFor(local));

        var store = new AccessConfigStore(Path.Combine(Path.GetTempPath(), $"tu-zugang-{Guid.NewGuid():N}.json"));
        store.Save(config);
        Assert.Equal(("Beide", "QS"), (store.Load()!.RightsSource, store.Load()!.ReleaseRole));
        File.Delete(store.Path);
    }
}
