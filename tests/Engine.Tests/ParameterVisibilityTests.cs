using Testumgebung.Engine.Blocks;

namespace Testumgebung.Engine.Tests;

public sealed class ParameterVisibilityTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("ja", true)]
    [InlineData("1", true)]
    [InlineData("FALSE", false)]
    [InlineData("", false)]
    public void Boolean_conditions_accept_existing_boolean_spellings(string value, bool expected)
    {
        Assert.Equal(expected, new ParameterVisibility("enabled", Boolean: true).Matches(_ => value));
        Assert.Equal(!expected, new ParameterVisibility("enabled", Boolean: false).Matches(_ => value));
    }

    [Theory]
    [InlineData("2", true)]
    [InlineData("0,5", true)]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("text", false)]
    public void Positive_number_condition_requires_a_finite_number(string value, bool expected)
        => Assert.Equal(expected, new ParameterVisibility("count", GreaterThanZero: true).Matches(_ => value));

    [Fact]
    public void Null_legacy_values_are_treated_as_empty_input()
    {
        Assert.False(new ParameterVisibility("value", Boolean: true).Matches(_ => null!));
        Assert.False(new ParameterVisibility("value", GreaterThanZero: true).Matches(_ => null!));
        Assert.True(new ParameterVisibility("value", "").Matches(_ => null!));
    }

    [Fact]
    public void All_conditions_use_defaults_and_require_every_condition()
    {
        var condition = new ParameterVisibility(All:
            [new("mode", AllowedValues: ["pair", "rolling"]), new("count", GreaterThanZero: true)]);
        var values = new Dictionary<string, string> { ["mode"] = "rolling", ["count"] = "2" };
        Assert.True(condition.Matches(name => values[name]));
        values["count"] = "0";
        Assert.False(condition.Matches(name => values[name]));
        values["count"] = "2";
        values["mode"] = "band";
        Assert.False(condition.Matches(name => values[name]));
        Assert.True(new ParameterVisibility("mode", "band").Matches(name => values[name]));
    }
}
