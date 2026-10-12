using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// OPC UA (ADR 0015, zweiter Teil): Lesen/Schreiben von Variablenknoten, Methodenaufrufe und Abonnements gegen den
/// in-Prozess-Server <see cref="SimulatedOpcUaServer"/> – über den Baustein „OPC UA“ und über CodeBlocks.
/// Der reale Desktop-Client (OPC-Foundation-Stack) wird per <c>OpcUaDevice.ClientFactory</c> eingehängt und ist
/// hier nicht Gegenstand der Tests (nur in der Windows-Anwendung verfügbar).
/// </summary>
#if DB_PROVIDERS
[Collection("Shared code workers")]
#endif
public sealed class OpcUaTests
{
    private const string Rules = """
        ns=2;s=Spannung = 230.5
        ns=2;s=Schalter = false
        methode ns=2;s=Rechner ns=2;s=Summe => summe
        methode ns=2;s=Rechner ns=2;s=Konst => konstante:42
        methode ns=2;s=Rechner ns=2;s=Spiegel => spiegeln
        """;

    private static StepDefinition S(string id, params (string Key, string Value)[] p)
        => BlockCatalog.Default.Create(BusBlocks.OpcUaAccess, id, id, p.ToDictionary(x => x.Key, x => x.Value));

    private static TestProject OpcProject(GroupDefinition loop)
    {
        var project = new TestProject(Test(loop));
        project.SimulationComm.Add(new SimulatedComm("Pruefling_OPC", "opc", CommKind.OpcUa, Rules));
        return project;
    }

    private static async Task<TestRun> RunAsync(TestProject project)
    {
        var (_, registry) = SampleProjects.CreateSimulation(project);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry });
        await run.RunAsync().Within();
        return run;
    }

    [Fact]
    public async Task Baustein_Lesen_Schreiben_Methode_und_Abonnieren()
    {
        var loop = Group("loop").Var("U", VarType.Double).Add(
            S("lesen", ("Ressource", "Pruefling_OPC"), ("Aktion", "Lesen"), ("Knoten", "ns=2;s=Spannung"),
                ("Untergrenze", "230"), ("Obergrenze", "231"), ("Einheit", "V"), ("Ziel", "U")),
            S("schreiben", ("Ressource", "Pruefling_OPC"), ("Aktion", "Schreiben"), ("Knoten", "ns=2;s=Schalter"), ("Wert", "true")),
            S("prüfen", ("Ressource", "Pruefling_OPC"), ("Aktion", "Lesen"), ("Knoten", "ns=2;s=Schalter"), ("Sollwert", "true")),
            S("summe", ("Ressource", "Pruefling_OPC"), ("Aktion", "Methode aufrufen"),
                ("Objekt", "ns=2;s=Rechner"), ("Methode", "ns=2;s=Summe"), ("Argumente", "2;3"), ("Sollwert", "5")),
            S("konst", ("Ressource", "Pruefling_OPC"), ("Aktion", "Methode aufrufen"),
                ("Objekt", "ns=2;s=Rechner"), ("Methode", "ns=2;s=Konst"), ("Sollwert", "42")),
            S("abo", ("Ressource", "Pruefling_OPC"), ("Aktion", "Abonnieren (auf Änderung warten)"),
                ("Knoten", "ns=2;s=Spannung"), ("Zeitlimit", "2000"), ("Sollwert", "230.5")),
            S("unbekannt", ("Ressource", "Pruefling_OPC"), ("Aktion", "Lesen"), ("Knoten", "ns=2;s=Fehlt")));
        var run = await RunAsync(OpcProject(loop));

        var lesen = run.Steps("lesen").Single();
        Assert.Equal(ResultRating.Pass, lesen.Rating);
        Assert.Equal(230.5, (double)lesen.Value!, 1e-9);
        Assert.Equal("V", lesen.Unit);
        Assert.Equal(ResultRating.NResult, run.Steps("schreiben").Single().Rating);     // Schreiben ist unbewertet
        Assert.Equal(ResultRating.Pass, run.Steps("prüfen").Single().Rating);           // Schalter nun true
        Assert.Equal(ResultRating.Pass, run.Steps("summe").Single().Rating);            // 2 + 3 = 5
        Assert.Equal(ResultRating.Pass, run.Steps("konst").Single().Rating);            // konstante 42
        Assert.Equal(ResultRating.Pass, run.Steps("abo").Single().Rating);              // Erstwert 230.5
        var unbekannt = run.Steps("unbekannt").Single();
        Assert.Equal(ResultRating.Error, unbekannt.Rating);
        Assert.Contains("unbekannt", unbekannt.Message);
    }

    [Fact]
    public async Task Python_CodeBlock_nutzt_OPC_UA()
    {
        const string source = """
            u = opcua_read("Pruefling_OPC", "ns=2;s=Spannung")
            opcua_write("Pruefling_OPC", "ns=2;s=Schalter", True)
            s = opcua_read("Pruefling_OPC", "ns=2;s=Schalter")
            summe = opcua_call("Pruefling_OPC", "ns=2;s=Rechner", "ns=2;s=Summe", [2, 3])
            konst = opcua_call("Pruefling_OPC", "ns=2;s=Rechner", "ns=2;s=Konst")
            ok = (abs(float(u) - 230.5) < 1e-6
                  and str(s).lower() in ("true", "1")
                  and abs(float(summe) - 5) < 1e-6
                  and abs(float(konst) - 42) < 1e-6)
            return Pass(u) if ok else Fail(f"{u} {s} {summe} {konst}")
            """;
        var runtime = CodeBlockTests.SharedRuntime.Value;
        var code = BlockCatalog.Default.Create(BlockCatalog.Code, "code", "code", new Dictionary<string, string>
        {
            ["Sprache"] = "Python", ["Quelltext"] = source, ["Timeout"] = "30",
        });
        var project = OpcProject(Group("loop").Add(code));
        var run = new TestRun(project.Definition, new TestRunOptions
        {
            Clock = new VirtualClock(), CodeRuntime = runtime, Hardware = SampleProjects.CreateSimulation(project).Registry,
        });
        await run.RunAsync().Within();
        var step = run.Steps("code").Single();
        Assert.True(step.Rating == ResultRating.Pass, step.Message);
    }

    [Fact]
    public async Task Server_meldet_Datenaenderung_an_Abonnenten()
    {
        await using var server = new SimulatedOpcUaServer("ns=2;s=Wert = 1");
        await server.ConnectAsync(CancellationToken.None);
        await using var sub = await server.SubscribeAsync("ns=2;s=Wert", 50, CancellationToken.None);

        // Erstwert
        Assert.Equal(1L, await sub.WaitForChangeAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(1, sub.Changes);

        // Serverseitige Änderung → Abonnent wird benachrichtigt
        server.Set("ns=2;s=Wert", 2L);
        Assert.Equal(2L, await sub.WaitForChangeAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(2L, sub.Current);
        Assert.Equal(2, sub.Changes);
    }

    [Fact]
    public async Task Server_lehnt_unbekannte_Knoten_und_Methoden_ab()
    {
        await using var server = new SimulatedOpcUaServer("ns=2;s=A = 1\nmethode ns=2;s=O ns=2;s=M => spiegeln");
        await server.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<OpcUaException>(() => server.ReadAsync("ns=2;s=X", CancellationToken.None));
        await Assert.ThrowsAsync<OpcUaException>(() => server.WriteAsync("ns=2;s=X", 5L, CancellationToken.None));
        await Assert.ThrowsAsync<OpcUaException>(() =>
            server.CallAsync("ns=2;s=O", "ns=2;s=Unbekannt", [], CancellationToken.None));

        var spiegel = await server.CallAsync("ns=2;s=O", "ns=2;s=M", [1L, 2L, 3L], CancellationToken.None);
        Assert.Equal([3L, 2L, 1L], spiegel);
    }

    [Fact]
    public async Task Server_verlangt_Verbindung()
    {
        await using var server = new SimulatedOpcUaServer("ns=2;s=A = 1");
        await Assert.ThrowsAsync<OpcUaException>(() => server.ReadAsync("ns=2;s=A", CancellationToken.None));
    }
}

