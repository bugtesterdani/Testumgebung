using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabParameterRuleTests
{
    [Theory]
    [InlineData("JS-4.4", "x", "-2.5", "2,5")]
    [InlineData("JS-4.9", "r", "90.0", "45")]
    [InlineData("JS-4.7.1", "r", "22.5", "360")]
    [InlineData("JS-3.13", "MM", "01", "13")]
    [InlineData("JS-3.13", "hh", "23", "24")]
    [InlineData("JS-3.3", "B", "-20", "-21")]
    [InlineData("JS-4.12", "Cx", "C3.5", "C11")]
    [InlineData("JS-4.16", "effects.q", "1000", "1001")]
    [InlineData("JS-4.2.41", "VERSION", "40", "41")]
    [InlineData("JS-4.2.42", "VERSION", "31", "32")]
    [InlineData("JS-4.5.4", "length", "8", "7")]
    [InlineData("JS-4.18", "ao", "aF", "aFG")]
    [InlineData("JS-4.2.11", "options", "+MOD10+WS2", "+WSn")]
    [InlineData("JS-4.16", "effects", "b,q120,fr1.5", "qbad")]
    [InlineData("JS-4.7.2", "options", "[D:2,1],[S:0,100,90]", "[D:bad,1]")]
    [InlineData("JS-4.7.1", "options", "[F:25%][O]", "[D:2,1]")]
    [InlineData("JS-4.7.1", "F.value", "CUSTOM42", "13%")]
    [InlineData("JS-4.5.4", "accesspwd", "0x1234abcd", "0x123")]
    [InlineData("JS-4.5.4", "accesspwd", "abcd", "abc")]
    [InlineData("JS-4.16", "size", "pt12.5", "ptbad")]
    [InlineData("JS-4.16", "font", "321", "321.5")]
    [InlineData("JS-4.1", "param", "10 [PREVIEW:ALL][SAVE]", "1.5")]
    [InlineData("JS-4.1", "param", "[?,R]", "[?,bad]")]
    [InlineData("JS-4.3", "param", "9999", "10000")]
    [InlineData("JS-4.3", "param", "sp", "unknown")]
    public void Documented_types_limits_and_template_values_are_enforced(string id, string name, string valid, string invalid)
    {
        var rule = CabDefinitions.ParameterRule(id, name);
        Assert.Null(rule.Validate(valid));
        Assert.NotNull(rule.Validate(invalid));
    }

    [Fact]
    public void Generation_specific_limits_and_field_names_preserve_the_manual_distinctions()
    {
        var name = CabDefinitions.ParameterRule("JS-4.16", "name");
        Assert.Null(name.Validate("FeldÄ123"));
        Assert.NotNull(name.Validate("3Feld"));
        Assert.NotNull(name.Validate("Feld_Name"));
        Assert.Null(name.Validate(new string('A', 10), CabPrinterGeneration.X2));
        Assert.NotNull(name.Validate(new string('A', 11), CabPrinterGeneration.X2));
        Assert.Null(name.Validate(new string('A', 32), CabPrinterGeneration.X4));
        Assert.NotNull(name.Validate(new string('A', 33), CabPrinterGeneration.X4));
        var position = CabDefinitions.ParameterRule("JS-4.5.4", "pos");
        Assert.Null(position.Validate("-10", CabPrinterGeneration.X2));
        Assert.NotNull(position.Validate("-10", CabPrinterGeneration.X4));
        var power = CabDefinitions.ParameterRule("JS-4.5.4", "power");
        Assert.Null(power.Validate("S", CabPrinterGeneration.X2));
        Assert.NotNull(power.Validate("5", CabPrinterGeneration.X2));
        Assert.Null(power.Validate("17", CabPrinterGeneration.X4));
        Assert.NotNull(power.Validate("18", CabPrinterGeneration.X4));
        Assert.Null(CabDefinitions.ParameterRule("JS-3.13", "ss").Validate(null));
        Assert.NotNull(CabDefinitions.ParameterRule("JS-3.13", "YY").Validate(null));
        Assert.NotNull(CabDefinitions.ParameterRule("JS-3.13", "MM").Validate("1"));
    }
}
