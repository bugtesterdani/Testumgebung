using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabBarcodeControlCatalogTests
{
    public static IEnumerable<object[]> Controls() => CabCatalog.Default["JS-4.2"].Parameters.Single(p => p.Name == "U.xxxx").Values.Select(v => new object[] { v.Value });

    [Theory]
    [MemberData(nameof(Controls))]
    public void Every_general_barcode_control_is_recognized_in_its_actual_protocol_context(string name)
    {
        var bytes = Encoding.UTF8.GetBytes("J\rB 0,0,0,CODE128,10,0.3;[U:" + name + "]\rA 1\r");
        var document = CabParser.Parse(bytes);
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var row = Assert.Single(CabDocumentPreview.Evaluate(document, new()));
        Assert.DoesNotContain(row.Preview.Issues, i => i.Code == "content-parameter");
        Assert.Equal(bytes, document.Write());
    }

    [Fact]
    public void Text_SU_and_barcode_SUB_remain_distinct_documented_contexts()
    {
        Assert.Equal("\u001a", CabContentPreview.Evaluate("[U:SU]", new()).Text);
        Assert.Equal("\u001a", CabContentPreview.Evaluate("[U:SUB]", new(), barcode: true).Text);
        Assert.True(CabContentPreview.Evaluate("[U:SUB]", new()).Value.Placeholder);
        Assert.True(CabContentPreview.Evaluate("[U:SU]", new(), barcode: true).Value.Placeholder);
    }
}
