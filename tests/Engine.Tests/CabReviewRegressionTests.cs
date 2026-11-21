using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabReviewRegressionTests
{
    [Theory]
    [InlineData("SC1", "size", "SC1")]
    [InlineData("SC1,n", "size", "SC1")]
    [InlineData("SC1,16,0.35", "size", "SC1")]
    [InlineData("16,0.35", "height", "16")]
    public void Ean_standard_sizes_and_individual_dimensions_are_alternative_forms(string dimensions, string parameter, string expected)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\rB 0,0,0,EAN13," + dimensions + ";123456789012\rA 1\r")));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var barcode = Assert.Single(result.Commands, c => c.Syntax.Command == "B");
        Assert.Equal(expected, barcode.Parameter(parameter)!.Value);
    }

    [Theory]
    [InlineData("[Digits]")]
    [InlineData("[Digits,1,12]")]
    public void Barcode_content_for_previously_defined_fields_is_deferred(string reference)
    {
        var source = "J\rT:Digits;0,0,0,3,3;123456789012\rB 0,0,0,EAN13,10,0.3;" + reference + "\rA 1\r";
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-dynamic");
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("J\rB 0,0,0,EAN13,10,0.3;[Digits]\rT:Digits;0,0,0,3,3;123456789012\rA 1\r")]
    [InlineData("J\rT:Digits;0,0,0,3,3;123456789012\rA 1\rJ\rB 0,0,0,EAN13,10,0.3;[Digits]\rA 1\r")]
    public void Undeclared_names_and_names_from_other_jobs_do_not_hide_literal_barcode_errors(string source)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source)));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "barcode-dynamic");
        Assert.Contains(result.Diagnostics, d => d.Code == "barcode-characters");
    }

    [Theory]
    [InlineData("T 0,0,0,3;text")]
    [InlineData("W 0,0,0,20,20,3;text")]
    public void Selected_vector_or_rich_text_syntax_requires_its_size(string command)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\r" + command + "\rA 1\r")));
        var field = Assert.Single(result.Commands, c => c.Syntax.Command is "T" or "W");
        var size = field.Parameter("size");
        Assert.NotNull(size);
        Assert.True(size.Rule.Required);
        Assert.False(size.IsPresent);
        Assert.Contains(result.Diagnostics, d => d.Code == "parameter-value" && d.Message.Contains("size", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("T 0,0,0,3,pt12;text")]
    [InlineData("T 0,0,0,-1,x2,y2;text")]
    public void Valid_vector_and_bitmap_size_alternatives_remain_valid(string command)
        => Assert.False(CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes("J\r" + command + "\rA 1\r"))).HasErrors);

    [Theory]
    [InlineData("e BMP;logo\r", "JS-3.4", "name", "logo")]
    [InlineData("F 100,custom\r", "JS-4.6", "name", "custom")]
    [InlineData("F 100;custom\r", "JS-4.6", "name", "custom")]
    public void Documented_erase_and_font_alias_forms_bind_both_parameters(string source, string id, string name, string value)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes(source));
        var result = CabValidator.Validate(document);
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        var command = Assert.Single(result.Commands, c => c.Definition.Catalog.Id == id);
        Assert.Equal(value, command.Parameter(name)!.Value);
        Assert.Equal(source, Encoding.UTF8.GetString(document.Write()));
    }

    [Theory]
    [InlineData("t\r", "param")]
    [InlineData("t \r", "param")]
    [InlineData("P\r", "disp")]
    [InlineData("P \r", "disp")]
    public void Missing_optional_arguments_remain_absent(string source, string parameter)
    {
        var result = CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source)));
        Assert.False(result.HasErrors, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Assert.Null(Assert.Single(result.Commands).Parameter(parameter));
    }

    [Theory]
    [InlineData("e BMP\r")]
    [InlineData("F 100\r")]
    [InlineData("F bad,custom\r")]
    [InlineData("t invalid\r")]
    [InlineData("P invalid\r")]
    public void Missing_required_or_invalid_explicit_arguments_still_fail(string source)
        => Assert.True(CabValidator.Validate(CabParser.Parse(Encoding.UTF8.GetBytes(source))).HasErrors);

    [Theory]
    [InlineData("C 1,39.999", false)]
    [InlineData("C 1,40", true)]
    [InlineData("m i\rC 1,1", false)]
    [InlineData("m i\rC 1,2", true)]
    public void Cut_displacement_is_strictly_smaller_than_label_height_in_the_active_units(string command, bool error)
    {
        var document = CabParser.Parse(Encoding.UTF8.GetBytes("m m\rJ\rS e;0,0,40,40,60\r" + command + "\rA 1\r"));
        Assert.Equal(error, CabValidator.Validate(document).Diagnostics.Any(d => d.Code == "cut-displacement"));
    }
}
