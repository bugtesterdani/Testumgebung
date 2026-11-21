using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabReviewRegressionTests
{
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
