using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CodeDependencySyntaxBoundaryTests
{
    [Theory]
    [InlineData(CodeLanguage.CSharp, "// Hidden")]
    [InlineData(CodeLanguage.CSharp, "/* Hidden */")]
    [InlineData(CodeLanguage.CSharp, "/* Hidden")]
    [InlineData(CodeLanguage.Cpp, "// Hidden\n")]
    [InlineData(CodeLanguage.Cpp, "/* Hidden */")]
    [InlineData(CodeLanguage.Python, "# Hidden")]
    [InlineData(CodeLanguage.Python, "# Hidden\n")]
    [InlineData(CodeLanguage.CSharp, "\"Hidden\\\" Hidden\"")]
    [InlineData(CodeLanguage.CSharp, "'Hidden'")]
    [InlineData(CodeLanguage.CSharp, "\"Hidden")]
    [InlineData(CodeLanguage.CSharp, "\"Hidden\n")]
    public void Dependency_analysis_ignores_strings_and_comments(CodeLanguage language, string suffix)
    {
        var analysis = CodeBlockConverter.Analyze(language, "Visible = Visible + 1;\n" + suffix, [("Visible", VarType.Int), ("Hidden", VarType.Int), ("bad/name", VarType.Int)]);
        var port = Assert.Single(analysis.Ports); Assert.Equal("Visible", port.Name); Assert.Equal(PortDirection.InOut, port.Direction); Assert.Empty(analysis.Warnings);
    }

    [Theory]
    [InlineData(PortDirection.Input, "Eingang", "Ausdruck")]
    [InlineData(PortDirection.Output, "Ausgang", "Zielvariable")]
    [InlineData(PortDirection.InOut, "Ein-/Ausgang", "zurückgeschrieben")]
    public void Binding_editor_explains_direction_and_stores_expression_or_target(PortDirection direction, string label, string hint)
    {
        var shell = new ShellViewModel(new QueueDispatcher(), project: SampleProjects.AcPruefung()); var owner = new StepPropertiesViewModel(shell.Editor, shell.Editor.FindNode("spannung")!);
        foreach (var unit in new string?[] { null, "V" })
        {
            var row = new PortBindingViewModel(owner, new CodePort("Port", VarType.Double, direction, unit)); Assert.Equal("Port", row.Name); Assert.Equal("double", row.TypeText); Assert.Equal(unit ?? "", row.Unit); Assert.Equal(label, row.DirectionText); Assert.Contains(hint, row.Hint);
            row.Value = "../Voltage"; Assert.Equal("../Voltage", row.Value); row.Value = row.Value;
            Assert.Equal("../Voltage", owner.Step.Parameters[(direction == PortDirection.Output ? BlockCatalog.OutputBindingPrefix : BlockCatalog.InputBindingPrefix) + "Port"]);
            row.Value = ""; Assert.Equal("", row.Value);
        }
    }
}
