using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabManualRegressionTests
{
    [Theory]
    [InlineData("B 0,0,0,MAXICODE+mode4;demo")]
    [InlineData("B 0,0,0,QRCODE+elL,.3;demo")]
    [InlineData("B 0,0,0,RSSEXPANDED+STACKED4,10,.3;(01)00000000000000(10)ABCDEFGHIJKLMNO")]
    [InlineData("M l fnt;Demo")]
    public void Documented_option_spelling_and_application_identifier_delimiters_are_accepted(string line)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r"));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void Binary_numeric_padding_is_ignored_without_changing_source()
    {
        const string text = "[BIN:$01, $02,\t3]";
        var result = CabContentPreview.Evaluate(text, new());
        Assert.False(result.Value.Placeholder);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Value.Bytes);
        var bytes = Encoding.UTF8.GetBytes("T 0,0,0,3,3;" + text + "\r");
        var document = CabParser.Parse(bytes);
        Assert.False(CabValidator.Validate(document).HasErrors);
        Assert.Equal(bytes, document.Write());
    }
    [Theory]
    [InlineData("RSSLIMITED", "00000000")]
    [InlineData("RSS14", "0000000000000")]
    [InlineData("RSS14", "(01)00000000000000")]
    [InlineData("RSS14+STACKED", "000000000000")]
    [InlineData("RSS14+STACKEDOMNI", "0000000000000")]
    [InlineData("RSS14+TRUNCATED", "00000000")]
    public void Short_gtin_input_is_checked_after_documented_padding_without_rewriting(string type, string value)
    {
        var bytes = Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + ",10,.3;" + value + "\rA 1\r");
        var document = CabParser.Parse(bytes); var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-normalization");
        Assert.Equal(bytes, document.Write());
    }

    [Fact]
    public void Incorrect_checksums_are_not_ignored_for_short_gtin_input()
        => Assert.Contains(CabValidator.Validate(CabParser.Parse("J\rB 0,0,0,RSS14,10,.3;0000000000001\rA 1\r"u8)).Diagnostics, d => d.Code == "barcode-checksum");
    [Fact]
    public void Empty_fill_removes_serial_zero_padding_and_script_style_names_are_case_insensitive()
    {
        Assert.Equal("7", CabContentPreview.Evaluate("[SER:0007][C:]", new()).Text);
        Assert.Equal("0", CabContentPreview.Evaluate("[SER:0000][C:]", new()).Text);
        var settings = new CabPreviewSettings { ExampleTime = new(2024, 1, 1) };
        Assert.Equal("٠١", CabContentPreview.Evaluate("[DAY02][S:arabic]", settings).Text);
        Assert.Equal("๐๑", CabContentPreview.Evaluate("[DAY02][S:thai]", settings).Text);
    }

    [Fact]
    public void Previously_declared_SER_is_a_field_reference_but_SER_with_arguments_is_a_function()
    {
        var fields = new Dictionary<string, CabPreviewValue> { ["SER"] = new("0007") };
        var reference = CabContentPreview.Evaluate("[SER]", new(), fields);
        Assert.Equal("0007", reference.Text);
        Assert.Equal("JS-5.88", Assert.Single(reference.Syntax.Fields).Definition.Id);
        Assert.Equal("1", CabContentPreview.Evaluate("[SER:1]", new(), fields).Text);
    }

    [Theory]
    [InlineData("13", "\r")]
    [InlineData("10", "\n")]
    public void Decimal_unicode_is_supported_in_its_documented_barcode_context(string argument, string expected)
    {
        Assert.Null(CabDefinitions.ParameterRule("JS-4.2", "U.xxxx").Validate(argument));
        Assert.Equal(expected, CabContentPreview.Evaluate("[U:" + argument + "]", new(), barcode: true).Text);
        Assert.True(CabContentPreview.Evaluate("[U:" + argument + "]", new()).Value.Placeholder);
    }
    [Theory]
    [InlineData("Aztec", "JS-4.2.6", ",.3", "demo")]
    [InlineData("2 OF 5 INTERLEAVED", "JS-4.2.3", ",10,.3,2", "1234")]
    [InlineData("Code128", "JS-4.2.11", ",10,.3", "demo")]
    [InlineData("Code 128", "JS-4.2.11", ",10,.3", "demo")]
    public void Documented_case_and_whitespace_variants_keep_their_source_spelling(string type, string id, string dimensions, string data)
    {
        var bytes = Encoding.UTF8.GetBytes("J\rB 0,0,0," + type + dimensions + ";" + data + "\rA 1\r");
        var document = CabParser.Parse(bytes); var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var command = Assert.Single(result.Commands, c => c.Syntax.Command == "B");
        Assert.Equal(id, command.Definition.Catalog.Id); Assert.Equal(type, command.Parameter("type")!.Value);
        Assert.Equal(bytes, document.Write());
    }

    [Fact]
    public void Similar_but_unknown_type_suffix_is_not_accepted_as_a_known_barcode()
        => Assert.True(CabValidator.Validate(CabParser.Parse("J\rB 0,0,0,Code128XYZ,10,.3;demo\rA 1\r"u8)).HasErrors);

    [Fact]
    public void Numeric_reference_padding_is_distinct_from_literal_text_padding()
    {
        var fields = new Dictionary<string, CabPreviewValue> { ["Amount"] = new("2") };
        var sum = CabContentPreview.Evaluate("[+:Amount, Amount]", new(), fields);
        Assert.False(sum.Value.Placeholder); Assert.Equal("4,00", sum.Text);
        Assert.Equal(" AMOUNT ", CabContentPreview.Evaluate("[UPPER: Amount ]", new(), fields).Value.Text);
    }

    [Theory]
    [InlineData("G 0,0,0;L:12,0.4,a,a[D:2,1][O]")]
    [InlineData("G 0,0,0;C:10,5,.3[F:25%]")]
    [InlineData("G 0,0,0;R:10,5,.3,.4[S:0,100,90]")]
    public void Graphic_options_can_follow_numeric_arguments_without_a_comma(string line)
    {
        var bytes = Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r");
        var document = CabParser.Parse(bytes); var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(Assert.Single(result.Commands, c => c.Syntax.Command == "G").Parameter("options"));
        Assert.Equal(bytes, document.Write());
    }

    [Fact]
    public void Unexplained_fourth_circle_value_is_retained_with_an_explicit_warning()
    {
        var document = CabParser.Parse("J\rG 0,0,0;C:10,5,1,2[O]\rA 1\r"u8);
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "graphic-ambiguous" && d.Severity == CabDiagnosticSeverity.Warning);
    }

    [Theory]
    [InlineData("C:10,5,1,unknown")]
    [InlineData("L:10,.3,a,a,1")]
    [InlineData("R:10,5,.3,.3,1")]
    public void Undocumented_extra_parameters_are_not_silently_treated_as_the_circle_ambiguity(string settings)
        => Assert.True(CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rG 0,0,0;" + settings + "\rA 1\r"))).HasErrors);
}
