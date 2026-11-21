using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabContentRegressionTests
{
    [Theory]
    [InlineData("[?:Pick,,,L]")]
    [InlineData("[?:Pick,,,M]")]
    [InlineData("[SHA256:abc,C:]")]
    public void Present_but_empty_packed_options_are_invalid(string source)
    {
        var field = Assert.Single(CabContentParser.Parse(source).Fields);
        Assert.NotEmpty(CabContentGrammar.Errors(field));
        Assert.True(CabContentPreview.Evaluate(source, new()).Value.Placeholder);
    }

    [Theory]
    [InlineData("[?:Pick,,,M!117]")]
    [InlineData("[WTAG:0,,1]")]
    [InlineData("[WEPC:4,,1]")]
    [InlineData("[SER:1,,2]")]
    public void Documented_empty_optional_positions_remain_valid(string source)
        => Assert.Empty(CabContentGrammar.Errors(Assert.Single(CabContentParser.Parse(source).Fields)));

    [Theory]
    [InlineData("[+: -7,0][D:4,0][C:0]", "-0007")]
    [InlineData("[+: -7,0][D:4,0][C: ]", "   -7")]
    [InlineData("[+: -7,0][D:4,0][C:*]", "-***7")]
    [InlineData("[P:1.239,.,-][R:n]", "1,23")]
    [InlineData("[P:1.239,.,-][R:u]", "1,24")]
    [InlineData("[P:1.239,.,-][R:d]", "1,23")]
    [InlineData("[P:1.239,.,-][R:m]", "1,24")]
    [InlineData("[P:-1.239,.,-][R:u]", "-1,23")]
    [InlineData("[P:-1.239,.,-][R:d]", "-1,24")]
    public void Numeric_formatting_keeps_the_sign_and_honors_rounding(string source, string expected)
        => Assert.Equal(expected, CabContentPreview.Evaluate(source, new()).Text);

    [Theory]
    [InlineData("[LEN:Bytes]")]
    [InlineData("[LOWER:Bytes]")]
    [InlineData("[UPPER:Bytes]")]
    [InlineData("[TRIM:Bytes]")]
    [InlineData("[LTRIM:Bytes]")]
    [InlineData("[RTRIM:Bytes]")]
    [InlineData("[SPLIT:Bytes,1]")]
    [InlineData("[SELECT:Pick,Bytes,1,1]")]
    [InlineData("[==:Bytes,Bytes]")]
    public void Text_functions_do_not_evaluate_the_readable_binary_marker(string source)
    {
        var fields = new Dictionary<string, CabPreviewValue> { ["Bytes"] = new("⟦Binär 410042⟧", [65, 0, 66]) };
        var result = CabContentPreview.Evaluate(source, new(), fields);
        Assert.True(result.Value.Placeholder);
        Assert.Contains(result.Issues, i => i.Code == "preview-value" && i.Message.Contains("Binärdaten", StringComparison.Ordinal));
        Assert.Equal("410042", CabContentPreview.Evaluate("[HEX:Bytes]", new(), fields).Text);
    }

    [Theory]
    [InlineData("FNC1")]
    [InlineData("CODEA")]
    [InlineData("CODEB")]
    [InlineData("CODEC")]
    public void Semantic_barcode_controls_cannot_be_hashed_as_their_display_marker(string control)
    {
        var value = CabContentPreview.Evaluate("[U:" + control + "]", new()).Value;
        Assert.True(value.Placeholder);
        var fields = new Dictionary<string, CabPreviewValue> { ["Control"] = value };
        Assert.True(CabContentPreview.Evaluate("[SHA256:Control]", new(), fields).Value.Placeholder);
    }

    [Fact]
    public void Multiline_rich_text_fields_are_bound_without_changing_the_html()
    {
        var bytes = Encoding.UTF8.GetBytes("J\rW:Rich;0,0,0,50,20,3,3,1;<HTML>\r<p>[DAY]</p>\r</HTML>\rA 1\r");
        var document = CabParser.Parse(bytes);
        var row = Assert.Single(CabDocumentPreview.Evaluate(document, new() { ExampleTime = new(2024, 1, 1) }));
        Assert.Contains("<p>1</p>", row.Preview.Text);
        var field = Assert.Single(row.Content.Syntax.Fields);
        Assert.Equal("[DAY]", Encoding.UTF8.GetString(row.Content.Source.Bytes(field.Offset, field.Length)));
        Assert.Equal(bytes, document.Write());
    }
}
