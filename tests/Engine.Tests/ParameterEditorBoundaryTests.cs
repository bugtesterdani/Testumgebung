using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ParameterEditorBoundaryTests
{
    [Theory]
    [InlineData("Verbindungsoptionen", ParameterKind.Text, false)]
    [InlineData("Header", ParameterKind.Text, false)]
    [InlineData("Body", ParameterKind.Text, false)]
    [InlineData("Value", ParameterKind.Expression, false)]
    [InlineData("Choice", ParameterKind.Choice, false)]
    [InlineData("Flag", ParameterKind.Bool, false)]
    [InlineData("Code", ParameterKind.Code, false)]
    [InlineData("Hidden", ParameterKind.Text, true)]
    public void Editor_parameter_contract_selects_correct_control_and_preserves_changes(string name, ParameterKind kind, bool hidden)
    {
        var shell = new ShellViewModel(new QueueDispatcher(), project: SampleProjects.AcPruefung()); var node = shell.Editor.FindNode("spannung")!;
        var owner = new StepPropertiesViewModel(shell.Editor, node);
        var row = new ParameterRowViewModel(owner, new ParameterDescriptor(name, "Label", kind, "default", Choices: kind == ParameterKind.Choice ? ["A", "B"] : null, Hint: "Hint")) { IsHidden = hidden };
        Assert.Equal("Label", row.Label); Assert.Equal("Hint", row.Hint); Assert.Equal(kind, row.Kind);
        Assert.Equal(kind == ParameterKind.Choice, row.IsChoice); Assert.Equal(kind == ParameterKind.Bool, row.IsBool); Assert.Equal(kind == ParameterKind.Code, row.IsCode); Assert.Equal(kind == ParameterKind.Expression, row.IsExpression);
        Assert.Equal(kind is not (ParameterKind.Choice or ParameterKind.Bool or ParameterKind.Code) && !hidden, row.IsText);
        Assert.Equal(name is "Verbindungsoptionen" or "Header" or "Body", row.IsMultiline);
        Assert.Equal(kind == ParameterKind.Choice ? 2 : 0, row.Choices.Count); Assert.Equal("default", row.Value);
        row.Value = "changed"; Assert.Equal("changed", owner.Step.Parameters[name]); row.Value = "changed"; row.BoolValue = true; Assert.True(row.BoolValue); row.BoolValue = false; Assert.False(row.BoolValue);
        Assert.Equal("false", owner.Step.Parameters[name]);
    }
}
