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

    [Theory]
    [InlineData(TraceBlocks.Start)]
    [InlineData(TraceBlocks.Record)]
    public void Startup_direction_follows_evaluation_mode_after_metadata_migration(string type)
    {
        var rows = Select(type, out var properties);
        properties.ShowAdvanced = true;
        rows["SteigungAnlaufAnzahl"].Value = "2";
        rows["SteigungMin"].Value = "-10";
        rows["SteigungMax"].Value = "10";
        rows[TraceBlocks.LowerFormulaParameter].Value = "0";
        rows[TraceBlocks.UpperFormulaParameter].Value = "10";
        var direction = rows["SteigungAnlaufrichtung"];
        foreach (var mode in new[] { TraceBlocks.PairSlope, TraceBlocks.DisjointSlope, TraceBlocks.RollingSlope })
        {
            rows[TraceBlocks.EvaluationParameter].Value = mode;
            Assert.False(direction.IsHidden);
        }
        foreach (var mode in new[] { TraceBlocks.ValueBand, TraceBlocks.FormulaBand })
        {
            rows[TraceBlocks.EvaluationParameter].Value = mode;
            Assert.True(direction.IsHidden);
        }
        Assert.Equal("Steigend", direction.Value);
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

    [Theory]
    [InlineData("Fuellbyte", 1)]
    [InlineData("AdresseTx", 1)]
    [InlineData("AdresseRx", 1)]
    [InlineData("TesterAdresse", 0)]
    [InlineData("SteuergeraetAdresse", 0)]
    [InlineData("Nad", 0)]
    public void Aliased_transport_diagnostics_reveal_the_specific_inactive_field(string name, int transport)
    {
        var rows = Select(BusBlocks.UdsRequest, out var properties);
        properties.ShowAdvanced = true;
        rows["Transport"].Value = BusBlocks.DiagTransports[transport];
        var field = rows[name];
        var original = field.Value;
        Assert.True(field.IsHidden);
        field.Value = "ungültig";
        Assert.True(properties.HasDiagnostics);
        Assert.False(field.IsHidden);
        Assert.Contains(properties.Diagnostics, message => message.Contains(field.Label));
        field.Value = original;
        Assert.False(properties.HasDiagnostics);
        Assert.True(field.IsHidden);
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
