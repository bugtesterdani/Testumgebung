using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ParameterTableTests
{
    private static ParameterRowViewModel Field(string type, string name, string value)
    {
        var step = BlockCatalog.Default.Create(type, "step", parameters: new Dictionary<string, string> { [name] = value });
        var editor = new EditorViewModel(new TestProject(Test(Group("loop").Add(step))));
        editor.SelectedNode = editor.FindNode("step");
        return Assert.Single(Assert.IsType<StepPropertiesViewModel>(editor.Properties).Parameters,
            row => row.Descriptor.Name == name);
    }

    [Fact]
    public void Channel_rows_keep_associated_columns_when_reordered()
    {
        var field = Field(TraceBlocks.Start, "Kanaele", "A,B");
        field.Owner.ApplyTableValues(new Dictionary<string, string> { ["Einheiten"] = "V,A", ["Grenzkurven"] = "voltage@1,current@2" });
        var table = ParameterTable.Read(field);
        field.Owner.ApplyTableValues(ParameterTable.Write(field, table.Rows.Reverse().ToArray()));
        Assert.Equal("B,A", field.Value);
        Assert.Equal("A,V", field.Owner.Step.Parameters["Einheiten"]);
        Assert.Equal("current@2,voltage@1", field.Owner.Step.Parameters["Grenzkurven"]);
        Assert.Empty(field.Owner.Diagnostics);
    }

    [Theory]
    [InlineData(ProcessBlocks.Start, "Argumente", "[\"a,b\",\"\"]")]
    [InlineData(ProcessBlocks.Start, "Umgebung", "{\"LABEL\":\"a=b\"}")]
    [InlineData(LiveLogBlocks.Monitor, "Textregeln", "[{\"text\":\"Start\",\"percent\":10,\"status\":\"Prüfen\"}]")]
    public void Existing_json_values_roundtrip_through_rows(string type, string name, string json)
    {
        var field = Field(type, name, json);
        Assert.True(field.IsTable);
        var before = ParameterTable.Read(field);
        field.Owner.ApplyTableValues(ParameterTable.Write(field, before.Rows));
        Assert.Equal(before.Rows.SelectMany(row => row), ParameterTable.Read(field).Rows.SelectMany(row => row));
    }
}
