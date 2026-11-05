using System.Text.Json.Nodes;

namespace Testumgebung.Engine.Tests;

public sealed partial class CommandLineTests
{
    [Theory]
    [InlineData("ABORT")]
    [InlineData("PASS + WERROR")]
    [InlineData("PASS+WERROR")]
    public async Task Abort_scenarios_support_explicit_outcome_and_legacy_diagnostic_expectations(string expected)
    {
        var project = Copy("netzteil.tproj.json");
        var scenario = Copy("abbruch.tsim.json", node =>
        {
            node["project"] = project; node["expect"]!["status"] = expected;
            node["expect"]!["duts"] = new JsonArray(new JsonObject { ["dutId"] = "SN-020", ["rating"] = "ABORT" });
        });
        var (exit, junit) = await RunAsync("test", scenario);
        Assert.Equal(0, exit); Assert.Contains("ABORT", junit.ToString()); Assert.All(Cases(junit), c => Assert.Null(c.Failure));
    }
}
