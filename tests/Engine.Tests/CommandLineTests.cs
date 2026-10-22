using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Testumgebung.Cli;

namespace Testumgebung.Engine.Tests;

/// <summary>Kommandozeile ohne Oberfläche: prüfen, bauen, Szenarien simulieren, JUnit-Bericht (docs/kommandozeile.md).</summary>
[Collection("Konsole")]
public sealed class CommandLineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tu-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string Examples()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        return Path.Combine(root.FullName, "docs", "beispiele", "kommandozeile");
    }

    private async Task<(int Exit, XDocument Junit)> RunAsync(params string[] args)
    {
        var junit = Path.Combine(_dir, $"junit-{Guid.NewGuid():N}.xml");
        var exit = await Cli.Cli.MainAsync([.. args, "--junit", junit]).WaitAsync(TimeSpan.FromMinutes(3));
        return (exit, XDocument.Load(junit));
    }

    private static IEnumerable<(string Suite, string Name, string? Failure)> Cases(XDocument junit)
        => junit.Descendants("testcase").Select(c => ((string)c.Attribute("classname")!, (string)c.Attribute("name")!, c.Element("failure")?.Value));

    private string Copy(string name, Action<JsonNode>? change = null, string? target = null)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(Examples(), name)))!;
        change?.Invoke(node);
        var path = Path.Combine(_dir, target ?? name);
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static JsonArray DutSteps(JsonNode project) => project["loop"]!["children"]![0]!["children"]!.AsArray();

    [Fact]
    public async Task Beispiele_bestehen_check_build_und_alle_Szenarien()
    {
        var (exit, junit) = await RunAsync("test", Examples());
        Assert.True(exit == 0, string.Join(Environment.NewLine, Cases(junit).Where(c => c.Failure is not null)));
        var cases = Cases(junit).ToList();
        Assert.Contains(cases, c => c.Name == "Build CodeBlock p (Python)");
        Assert.Contains(cases, c => c.Suite.StartsWith("unterspannung.tsim.json", StringComparison.Ordinal) && c.Name == "Prüfling 2");
        Assert.Contains(cases, c => c.Suite.StartsWith("abbruch.tsim.json", StringComparison.Ordinal) && c.Name == "Gesamtergebnis");
        Assert.All(cases, c => Assert.Null(c.Failure));
    }

    [Fact]
    public async Task Bausteinfehler_und_Codefehler_werden_in_einem_Durchgang_gemeldet()
    {
        var project = Copy("netzteil.tproj.json", p =>
        {
            DutSteps(p)[1]!["parameters"]!["Ressource"] = "";
            DutSteps(p)[3]!["parameters"]!["Quelltext"] = "Leistung = Spannung *\nreturn Pass()";
        });
        var (exit, junit) = await RunAsync("build", project);
        Assert.Equal(1, exit);
        var failures = Cases(junit).Where(c => c.Failure is not null).ToList();
        Assert.Contains(failures, c => c.Name == "Bausteinkonfiguration" && c.Failure!.Contains("Schritt u (analog.messen)", StringComparison.Ordinal));
        Assert.Contains(failures, c => c.Name == "Build CodeBlock p (Python)" && c.Failure!.Contains("SyntaxError", StringComparison.Ordinal));
        // Katalogauszug zum fehlerhaften Baustein im Bericht, damit der nächste Versuch die richtigen Parameter kennt.
        Assert.Contains("Ressource (Resource, Pflicht", junit.Descendants("system-out").Single().Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Abweichende_Erwartung_faellt_auf()
    {
        Copy("netzteil.tproj.json");
        var scenario = Copy("normal.tsim.json", s => s["simulation"]!["Spannung_Pruefling"] = 245.0);
        var (exit, junit) = await RunAsync("test", scenario);
        Assert.Equal(1, exit);
        var failures = Cases(junit).Where(c => c.Failure is not null).Select(c => (c.Name, c.Failure)).ToList();
        Assert.Contains(("Gesamtergebnis", "erwartet PASS, erhalten FAIL"), failures);
        Assert.Contains(failures, f => f.Name == "Prüfling 1" && f.Failure!.Contains("Ergebnis erwartet PASS, erhalten FAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fehlende_Werkerantwort_beendet_den_Lauf_mit_klarer_Meldung()
    {
        Copy("netzteil.tproj.json");
        var scenario = Copy("normal.tsim.json", s => s["answers"] = new JsonArray());
        var (exit, junit) = await RunAsync("test", scenario);
        Assert.Equal(1, exit);
        var lauf = Cases(junit).Single(c => c.Name == "Lauf");
        Assert.Contains("Keine Antwort im Szenario für Werkereingabe „sn“", lauf.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aufgebrauchte_Antworten_und_Tippfehler_im_Szenario_werden_gemeldet()
    {
        Copy("netzteil.tproj.json");
        var used = Copy("normal.tsim.json", s => s["duts"] = 3, "drei.tsim.json");
        var typo = Copy("normal.tsim.json", s => s["dutz"] = 2, "tippfehler.tsim.json");
        var (exit, junit) = await RunAsync("test", _dir);
        Assert.Equal(1, exit);
        var failures = Cases(junit).Where(c => c.Failure is not null).ToList();
        Assert.Contains(failures, c => c.Suite.StartsWith(Path.GetFileName(used), StringComparison.Ordinal)
                                      && c.Failure!.Contains("Antworten für „sn“ aufgebraucht", StringComparison.Ordinal));
        Assert.Contains(failures, c => c.Suite == Path.GetFileName(typo) && c.Name == "Szenario laden");
    }

    [Fact]
    public async Task CSharp_CodeBlock_wird_gebaut_und_im_Ablauf_ausgefuehrt()
    {
        var project = Copy("netzteil.tproj.json", p =>
        {
            var code = DutSteps(p)[3]!["parameters"]!;
            code["Sprache"] = "C#";
            code["Quelltext"] = "Leistung = Math.Round(Spannung * Strom, 3);\nreturn Measured(Leistung, 100, 1000, \"W\");";
        });
        var scenario = Copy("normal.tsim.json");
        var (exit, junit) = await RunAsync("test", project);
        Assert.True(exit == 0, string.Join(Environment.NewLine, Cases(junit).Where(c => c.Failure is not null)));
        Assert.Contains(Cases(junit), c => c.Name == "Build CodeBlock p (C#)");
        Assert.Contains(Cases(junit), c => c.Suite.StartsWith(Path.GetFileName(scenario), StringComparison.Ordinal) && c.Name == "Schritt p #2");
    }

    [Fact]
    public async Task Projekt_ohne_Szenario_wird_nur_geprueft_und_als_uebersprungen_gemeldet()
    {
        var project = Copy("netzteil.tproj.json");
        var (exit, junit) = await RunAsync("test", project);
        Assert.Equal(0, exit);
        Assert.Single(junit.Descendants("skipped"));
        Assert.Contains(Cases(junit), c => c.Name == "Szenarien");
    }

    [Fact]
    public void Katalog_liefert_Parameter_und_Uebersicht()
    {
        Assert.Contains("- Ressource (Resource, Pflicht", CatalogText.Search("analog.messen"), StringComparison.Ordinal);
        Assert.Contains("werker – Werkerinteraktion", CatalogText.Overview(), StringComparison.Ordinal);
        Assert.Contains("Kein Baustein passt", CatalogText.Search("gibtesnicht"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ungueltige_Aufrufe_liefern_Exitcode_2()
    {
        Assert.Equal(2, await Cli.Cli.MainAsync([]));
        Assert.Equal(2, await Cli.Cli.MainAsync(["test"]));
        Assert.Equal(2, await Cli.Cli.MainAsync(["test", Path.Combine(_dir, "fehlt.tproj.json")]));
        Assert.Equal(2, await Cli.Cli.MainAsync(["simulate", Examples()]));
    }
    [Theory]
    [InlineData("--unknown")]
    [InlineData("--junit")]
    [InlineData("--junit --unknown")]
    [InlineData("--junit report.xml --junit other.xml")]
    [InlineData("another.tproj.json")]
    public async Task Falsche_Optionen_werden_nicht_stillschweigend_ignoriert(string options)
    {
        var project = Copy("netzteil.tproj.json");
        Assert.Equal(2, await Cli.Cli.MainAsync(["check", project, .. options.Split(' ')]));
    }

    [Theory]
    [InlineData("{\"answers\":null}")]
    [InlineData("{\"answers\":[null]}")]
    [InlineData("{\"simulation\":null}")]
    [InlineData("{\"simulation\":{\"x\":1e999}}")]
    [InlineData("{\"expect\":null}")]
    [InlineData("{\"expect\":{\"steps\":null}}")]
    [InlineData("{\"expect\":{\"steps\":[{\"step\":\"p\",\"count\":-1}]}}")]
    [InlineData("{\"expect\":{\"steps\":[{\"step\":\"p\",\"value\":5,\"tolerance\":-1}]}}")]
    [InlineData("{\"expect\":{\"status\":\"PASS + Tippfehler\"}}")]
    [InlineData("{\"answers\":[{\"step\":\"sn\",\"value\":{}}]}")]
    [InlineData("{\"answers\":[{\"step\":\"sn\",\"value\":1},{\"step\":\"sn\",\"value\":2}]}")]
    public void Fehlerhafte_Szenariofelder_geben_einen_Dateifehler(string overrides)
    {
        var path = Copy("normal.tsim.json", node =>
        {
            foreach (var entry in JsonNode.Parse(overrides)!.AsObject()) node[entry.Key] = entry.Value?.DeepClone();
        });
        Assert.Throws<InvalidDataException>(() => Scenario.Load(path));
    }

    [Fact]
    public async Task Defektes_Nachbarszenario_verhindert_Check_und_Build_nicht()
    {
        var project = Copy("netzteil.tproj.json");
        Copy("normal.tsim.json", s => s["dutz"] = 2);
        Assert.Equal(0, (await RunAsync("check", project)).Exit);
        Assert.Equal(0, (await RunAsync("build", project)).Exit);
        var result = await RunAsync("test", project);
        Assert.Equal(1, result.Exit);
        Assert.Contains(Cases(result.Junit), c => c.Name == "Szenario laden");
    }

    [Fact]
    public async Task Junit_Option_darf_vor_dem_Projektpfad_stehen()
    {
        var project = Copy("netzteil.tproj.json");
        var report = Path.Combine(_dir, "frueh.xml");
        Assert.Equal(0, await Cli.Cli.MainAsync(["check", "--junit", report, project]));
        Assert.All(Cases(XDocument.Load(report)), c => Assert.Null(c.Failure));
    }

    [Fact]
    public async Task Simulation_zeigt_Protokoll_und_unbekannte_Ressource_schlaegt_fehl()
    {
        Copy("netzteil.tproj.json");
        var scenario = Copy("normal.tsim.json");
        Assert.Equal(0, await Cli.Cli.MainAsync(["simulate", scenario]));
        scenario = Copy("normal.tsim.json", s => s["simulation"]!["falsch"] = 12);
        var result = await RunAsync("test", scenario);
        Assert.Equal(1, result.Exit);
        Assert.Contains(Cases(result.Junit), c => c.Name == "Simulation" && c.Failure!.Contains("falsch"));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("katalog")]
    [InlineData("anleitung")]
    public async Task Informationen_sind_ohne_Projekt_verfuegbar(string command)
        => Assert.Equal(0, await Cli.Cli.MainAsync([command]));

    [Fact]
    public void Bericht_erhaelt_Sonderzeichen_und_Skips()
    {
        var report = new Report();
        report.Fail("A & B", "<Test>", new string('x', 400), string.Join("\n", Enumerable.Repeat("<Ausgabe>\0 😀", 65)));
        report.Skip("A & B", "nicht ausgeführt", "kein Szenario");
        report.WriteSummary();
        report.WriteJUnit(_dir);
        var xml = XDocument.Load(Path.Combine(_dir, "testumgebung.xml"));
        Assert.Equal("A & B", (string?)xml.Descendants("testsuite").Single().Attribute("name"));
        Assert.Equal(400, xml.Descendants("failure").Single().Value.Length);
        Assert.Single(xml.Descendants("skipped"));
        Assert.Contains("\\u0000 😀", xml.Descendants("system-out").Single().Value);
    }

    [Theory]
    [InlineData("return Pass(true);", "true", 0)]
    [InlineData("return Pass(false);", "false", 0)]
    [InlineData("return Pass(true);", "false", 1)]
    [InlineData("return Pass(7);", "7", 0)]
    [InlineData("return Pass(\"Text\");", "\"Text\"", 0)]
    [InlineData("return Pass(\"7\");", "7", 1)]
    public async Task Erwartungen_vergleichen_Wert_und_Datentyp(string source, string expected, int exit)
    {
        Copy("netzteil.tproj.json", p =>
        {
            var code = DutSteps(p)[3]!["parameters"]!;
            code["Sprache"] = "C#";
            code["Quelltext"] = source;
        });
        var scenario = Copy("normal.tsim.json", s => s["expect"] = new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject { ["step"] = "p", ["value"] = JsonNode.Parse(expected) })
        });
        var result = await RunAsync("test", scenario);
        Assert.Equal(exit, result.Exit);
        Assert.Contains(Cases(result.Junit), c => c.Name == "Schritt p" && (c.Failure is null) == (exit == 0));
    }

}
