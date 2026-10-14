using System.Text.Json;
using System.Text.Json.Nodes;
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;

namespace Testumgebung.Engine.Tests;

/// <summary>Checks the AI reference against the application and runs its example across limits.</summary>
public sealed class AiProjectGuideTests
{
    private static string Documentation(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Testumgebung.sln"))) root = root.Parent;
        return Path.Combine(root.FullName, "docs", "ki", name);
    }

    [Fact]
    public void Exported_catalog_matches_every_current_block_and_parameter_contract()
    {
        var blocks = BlockCatalog.Default.All.OrderBy(b => b.Type, StringComparer.Ordinal).Select(b => new
        {
            b.Type, b.DisplayName, b.Category, b.Description,
            Parameters = b.Parameters.Select(p => new { p.Name, p.Label, Kind = p.Kind.ToString(), p.Default, p.Required, p.Choices, p.Hint })
        });
        var expected = JsonSerializer.SerializeToNode(new { Format = ProjectSerializer.FormatName, FormatVersion = ProjectSerializer.FormatVersion, Blocks = blocks },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var shipped = JsonNode.Parse(File.ReadAllText(Documentation("bausteinkatalog.json")));
        Assert.True(JsonNode.DeepEquals(expected, shipped), "Regenerate docs/ki/bausteinkatalog.json using build/project-format-reference.ps1.");
    }

    [Theory]
    [InlineData(220, ResultRating.Pass)]
    [InlineData(230, ResultRating.Pass)]
    [InlineData(240, ResultRating.Pass)]
    [InlineData(219.9, ResultRating.Fail)]
    [InlineData(240.1, ResultRating.Fail)]
    public async Task Example_records_distinct_serial_numbers_and_real_simulator_measurements(double measured, ResultRating expected)
    {
        var serializer = new ProjectSerializer();
        var project = serializer.Load(Documentation("konvertierungsbeispiel.tproj.json"));
        Assert.Empty(project.Definition.Validate());
        foreach (var step in project.Definition.AllNodes().OfType<StepDefinition>()) Assert.Empty(BlockCatalog.Default.Rebuild(step));
        Assert.Equal(serializer.Serialize(project), serializer.Serialize(serializer.Clone(project)));
        Assert.Equal(RepeatMode.Endless, project.Definition.Loop.Repetition.Mode);

        // Bound only the test harness; the shipped worker example stays endless.
        project.Definition.Loop.Repetition = Repetition.Times(2);
        var simulation = new SimulationAdapter();
        var resource = Assert.Single(project.Simulation);
        simulation.SetInput(resource.Channel, measured);
        var hardware = new HardwareRegistry().Bind(resource.Name, simulation, resource.Channel);
        var serial = 0;
        var op = new ScriptedOperator().Answer("Seriennummer erfassen", _ => "SN-" + ++serial);
        var run = new TestRun(project.Definition, new TestRunOptions { Hardware = hardware, Operator = op, Clock = new VirtualClock() });
        await run.RunAsync().Within();
        var completed = run.Events.OfType<DutRunCompleted>().ToList();
        Assert.Equal(new[] { "SN-1", "SN-2" }, completed.Select(d => d.DutId));
        Assert.All(completed, d => Assert.Equal(expected, d.Status.Rating));
        Assert.Equal(2, run.Steps("a-mess").Count());
        Assert.All(run.Steps("a-mess"), step => { Assert.Equal(measured, step.Value); Assert.Equal(expected, step.Rating); });
    }
}
