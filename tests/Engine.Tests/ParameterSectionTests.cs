using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ParameterSectionTests
{
    [Theory]
    [InlineData(TraceBlocks.Record)]
    [InlineData(BusBlocks.UdsRequest)]
    [InlineData(ProcessBlocks.Start)]
    public void Large_blocks_have_explicit_sections(string type)
    {
        var fields = BlockCatalog.Default.Get(type).Parameters;
        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Section)));
        Assert.True(fields.Select(field => field.Section).Distinct().Count() >= 3);
    }

    [Fact]
    public void Basic_value_band_capture_has_at_most_twelve_fields_and_only_active_source()
    {
        var descriptor = BlockCatalog.Default.Get(TraceBlocks.Record);
        var step = BlockCatalog.Default.Create(TraceBlocks.Record, "capture");
        var basic = descriptor.Parameters.Where(p => !p.Advanced && p.Name is not ("Einheiten" or "Grenzkurven" or TraceBlocks.AbortCurvesParameter)
            && (p.VisibleWhen?.Matches(name => step.Parameters[name]) ?? true)).ToArray();
        Assert.InRange(basic.Length, 1, 12);
        Assert.Contains(basic, p => p.Name == "Werte");
        Assert.DoesNotContain(basic, p => p.Name == "Ressourcen");
        step.Parameters["Quelle"] = "Geräte";
        Assert.True(descriptor.Parameters.Single(p => p.Name == "Ressourcen").VisibleWhen!.Matches(name => step.Parameters[name]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Legacy_target_settings_survive_loading_and_new_switch_can_disable_them(bool configured)
    {
        var values = new Dictionary<string, string> { [TraceBlocks.EvaluationParameter] = TraceBlocks.PairSlope, ["SteigungMin"] = "-10", ["SteigungMax"] = "10" };
        if (configured) { values["ZielbandMin"] = "4"; values["ZielbandMax"] = "6"; }
        var step = BlockCatalog.Default.Create(TraceBlocks.Start, "capture", parameters: values);
        step.Parameters.Remove(TraceBlocks.TargetEnabledParameter);
        var copy = new ProjectSerializer().Clone(new TestProject(Test(Group("loop").Add(step))));
        var loaded = Assert.IsType<StepDefinition>(copy.Definition.Loop.Children.Single());
        Assert.Equal(configured ? "true" : "false", loaded.Parameters[TraceBlocks.TargetEnabledParameter]);
        loaded.Parameters[TraceBlocks.TargetEnabledParameter] = "false";
        loaded.Parameters["ZielbandMin"] = "retained invalid expression";
        Assert.Empty(BlockCatalog.Default.Rebuild(loaded));
        Assert.Equal("retained invalid expression", loaded.Parameters["ZielbandMin"]);
    }
}
