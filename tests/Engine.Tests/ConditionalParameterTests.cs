using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ConditionalParameterTests
{
    private static Dictionary<string, ParameterRowViewModel> Select(string type, out StepPropertiesViewModel properties)
    {
        var step = BlockCatalog.Default.Create(type, "step");
        var editor = new EditorViewModel(new TestProject(Test(Group("loop").Add(step))));
        editor.SelectedNode = editor.FindNode("step");
        properties = Assert.IsType<StepPropertiesViewModel>(editor.Properties);
        return properties.Parameters.ToDictionary(row => row.Descriptor.Name);
    }

    [Fact]
    public void Uds_fields_follow_transport_and_reveal_an_invalid_hidden_can_field()
    {
        var rows = Select(BusBlocks.UdsRequest, out var properties);
        Assert.True(properties.HasAdvancedParameters);
        Assert.False(rows["AnfrageId"].IsHidden);
        Assert.True(rows["TesterAdresse"].IsHidden);
        Assert.True(rows["Fuellbyte"].IsHidden);
        properties.ShowAdvanced = true;
        Assert.False(rows["Fuellbyte"].IsHidden);
        rows["AnfrageId"].Value = "0x701";

        rows["Transport"].Value = BusBlocks.DiagTransports[1];
        Assert.True(rows["AnfrageId"].IsHidden);
        Assert.False(rows["TesterAdresse"].IsHidden);
        rows["Transport"].Value = BusBlocks.DiagTransports[2];
        Assert.False(rows["Nad"].IsHidden);
        Assert.True(rows["TesterAdresse"].IsHidden);
        Assert.Equal("0x701", rows["AnfrageId"].Value);

        rows["AnfrageId"].Value = "ungültig";
        Assert.True(properties.HasDiagnostics);
        Assert.False(rows["AnfrageId"].IsHidden);
        rows["AnfrageId"].Value = "0x701";
        Assert.False(properties.HasDiagnostics);
        Assert.True(rows["AnfrageId"].IsHidden);
    }

    [Fact]
    public void Process_fields_follow_call_type_and_retain_saved_input()
    {
        var rows = Select(ProcessBlocks.Start, out var properties);
        rows["Datei"].Value = "example.exe";
        Assert.False(properties.HasDiagnostics);
        Assert.True(rows["Typ"].IsHidden);
        Assert.True(rows["Python"].IsHidden);

        rows["Art"].Value = "Python";
        Assert.True(rows["Python"].IsHidden);
        properties.ShowAdvanced = true;
        Assert.False(rows["Python"].IsHidden);
        rows["Python"].Value = "python.exe";

        rows["Art"].Value = "DLL-Main";
        Assert.True(properties.HasDiagnostics);
        Assert.False(rows["Typ"].IsHidden);
        rows["Typ"].Value = "Example.Program";
        Assert.False(properties.HasDiagnostics);
        Assert.True(rows["Python"].IsHidden);
        Assert.Equal("python.exe", rows["Python"].Value);
    }
}
