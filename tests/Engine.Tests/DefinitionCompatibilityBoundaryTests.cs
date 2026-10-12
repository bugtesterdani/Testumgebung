using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Code;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Releases;
using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class DefinitionCompatibilityBoundaryTests
{
    private static TestProject Project()
    {
        var loop = new GroupDefinition("loop", "Loop");
        loop.Children.Add(new GroupDefinition("dut", "Prüfling") { IsDutRun = true });
        return new TestProject(Kit.Test(loop));
    }

    [Theory]
    [InlineData("name", "anderer Prüfablauf")]
    [InlineData("dut-id", "Prüflingsgruppe")]
    [InlineData("missing-dut", "Prüflingsgruppe")]
    [InlineData("variables", "Variablen")]
    [InlineData("init", "Init")]
    [InlineData("finalization", "Finalization")]
    [InlineData("loop", "Loop außerhalb")]
    public void Outer_changes_require_new_session_in_both_directions(string change, string reason)
    {
        var a = Project(); var b = new ProjectSerializer().Clone(a);
        switch (change)
        {
            case "name": b = new TestProject(new TestDefinition("Anderer Ablauf", "1", b.Definition.Init, b.Definition.Loop, b.Definition.Finalization)); break;
            case "dut-id": b.Definition.Loop.Children.Clear(); b.Definition.Loop.Children.Add(new GroupDefinition("new-dut", "Prüfling") { IsDutRun = true }); break;
            case "missing-dut": ((GroupDefinition)b.Definition.Find("dut")!).IsDutRun = false; break;
            case "variables": b.Definition.Root.Variables.Add(new VariableDefinition("X", VarType.Bool, true)); break;
            case "init": b.Definition.Init.Children.Add(BlockCatalog.Default.Create("warten", "init-added")); break;
            case "finalization": b.Definition.Finalization.Children.Add(BlockCatalog.Default.Create("warten", "end-added")); break;
            case "loop": b.Definition.Loop.Children.Add(BlockCatalog.Default.Create("warten", "outside")); break;
        }
        Assert.False(DefinitionCompatibility.CanSwapAtDut(a, b, out var forward));
        Assert.Contains(reason, forward);
        Assert.False(DefinitionCompatibility.CanSwapAtDut(b, a, out var reverse));
        Assert.Contains(reason, reverse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Library_updates_depend_on_use_outside_dut(bool outside)
    {
        var a = Project();
        a.Definition.Library.Add(new CodeBlockDefinition("library", "Library") { Source = "return Pass(1);" });
        var step = BlockCatalog.Default.Create("code", "call"); step.Parameters["Baustein"] = "library";
        (outside ? a.Definition.Init : (GroupDefinition)a.Definition.Find("dut")!).Children.Add(step);
        var b = new ProjectSerializer().Clone(a); b.Definition.Library[0].Source = "return Pass(2);";
        Assert.Equal(!outside, DefinitionCompatibility.CanSwapAtDut(a, b, out var reason));
        if (outside) Assert.Contains("Baustein", reason); else Assert.Empty(reason);
        b.Definition.Library.Clear();
        Assert.Equal(!outside, DefinitionCompatibility.CanSwapAtDut(a, b, out _));
    }

    [Fact]
    public void Dut_content_and_documentation_can_change_without_mutating_inputs()
    {
        var a = Project(); var serializer = new ProjectSerializer(); var b = serializer.Clone(a);
        b.Definition.Version = "2"; b.Description = "Dokumentation";
        ((GroupDefinition)b.Definition.Find("dut")!).Children.Add(BlockCatalog.Default.Create("warten", "inside"));
        var beforeA = serializer.Serialize(a); var beforeB = serializer.Serialize(b);
        Assert.True(DefinitionCompatibility.CanSwapAtDut(a, b, out var reason, serializer));
        Assert.Empty(reason);
        Assert.Equal(beforeA, serializer.Serialize(a)); Assert.Equal(beforeB, serializer.Serialize(b));
    }
}

