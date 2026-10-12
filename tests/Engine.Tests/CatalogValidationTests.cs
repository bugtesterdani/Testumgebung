using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

/// <summary>Even incomplete or malformed block settings must stay editable and
/// survive a project save/reload without losing the user's original input.</summary>
public sealed class CatalogValidationTests
{
    public static IEnumerable<object[]> ParameterCases()
    {
        foreach (var block in BlockCatalog.Default.All)
            foreach (var parameter in block.Parameters)
            {
                yield return [block.Type, parameter.Name, ""];
                yield return [block.Type, parameter.Name, "  "];
                yield return [block.Type, parameter.Name, "invalid(\"\"\"\n<&> !"];
                if (parameter.Choices is { } choices)
                    foreach (var choice in choices.Where(c => c != "" && c != "  ").Distinct()) yield return [block.Type, parameter.Name, choice];
                if (parameter.Kind == ParameterKind.Number)
                    foreach (var value in new[] { "-1", "0", "1", "1.25", "1,25", "1e3" }) yield return [block.Type, parameter.Name, value];
                if (parameter.Kind == ParameterKind.Bool)
                    foreach (var value in new[] { "true", "false", "ja", "1" }) yield return [block.Type, parameter.Name, value];
            }
    }

    [Theory, MemberData(nameof(ParameterCases))]
    public void Block_parameter_input_survives_save_reload_with_consistent_diagnostics(string type, string parameter, string value)
    {
        var catalog = BlockCatalog.Default;
        var step = catalog.Create(type, "step");
        var loop = new GroupDefinition("loop", "Loop");
        loop.Children.Add(step);
        var project = new TestProject(Kit.Test(loop));
        var editor = new EditorViewModel(project);
        editor.SelectedNode = editor.FindNode("step");
        var properties = Assert.IsType<StepPropertiesViewModel>(editor.Properties);
        var row = Assert.Single(properties.Parameters, p => p.Descriptor.Name == parameter);
        row.Value = value;
        Assert.Equal(value, row.Value);
        row.Value = value; // Re-entering the same input must preserve the edited value.
        Assert.Equal(value.Equals("true", StringComparison.OrdinalIgnoreCase), row.BoolValue);
        var diagnostics = catalog.Rebuild(step);
        if (diagnostics.Count > 0)
        {
            Assert.IsType<ErrorAction>(step.Action);
            Assert.All(diagnostics, d => Assert.False(string.IsNullOrWhiteSpace(d)));
        }
        var serializer = new ProjectSerializer();
        var copy = serializer.Clone(project);
        var copiedStep = Assert.IsType<StepDefinition>(copy.Definition.Find("step"));
        Assert.Equal(value, copiedStep.Parameters[parameter]);
        Assert.Equal(type, copiedStep.BlockType);
        Assert.Equal(diagnostics, catalog.Rebuild(copiedStep));
        Assert.Equal(serializer.Serialize(project), serializer.Serialize(copy));
    }
}
