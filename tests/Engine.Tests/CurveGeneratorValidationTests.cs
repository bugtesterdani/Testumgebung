using Testumgebung.Domain.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class CurveGeneratorValidationTests
{
    [Theory]
    [InlineData("channels")]
    [InlineData("duplicate")]
    [InlineData("rate")]
    [InlineData("duration")]
    [InlineData("zero-samples")]
    [InlineData("too-many")]
    [InlineData("no-formula")]
    [InlineData("bad-formula")]
    public void Generator_rejects_invalid_configuration_before_allocating_curve(string scenario)
    {
        var settings = new GeneratorSettings { ChannelIds = ["A"], SampleRate = 10, Duration = 1 };
        settings = scenario switch { "channels" => settings with { ChannelIds = [] }, "duplicate" => settings with { ChannelIds = ["A", "A"] }, "rate" => settings with { SampleRate = 0 }, "duration" => settings with { Duration = 0 }, "zero-samples" => settings with { Duration = .001 }, "too-many" => settings with { SampleRate = CurveGenerator.MaxSamples + 1 }, "no-formula" => settings with { Kind = GeneratorKind.Formula, Formulas = [] }, _ => settings with { Kind = GeneratorKind.Formula, Formulas = ["invalid("] } };
        Assert.NotEmpty(Assert.Throws<CurveGeneratorException>(() => CurveGenerator.Generate(settings, "id", "name", "1", "tester", DateTimeOffset.UnixEpoch)).Message);
    }
    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("x:1")]
    [InlineData("1:x")]
    [InlineData("1:0")]
    [InlineData("1:-1")]
    public void Invalid_step_definitions_are_rejected(string steps) => Assert.Throws<CurveGeneratorException>(() => CurveGenerator.ParseSteps(steps));
    [Theory]
    [InlineData(GeneratorKind.Sine)]
    [InlineData(GeneratorKind.Formula)]
    public void Missing_phase_or_formula_for_later_channel_uses_documented_fallback(GeneratorKind kind)
    {
        var settings = new GeneratorSettings { Kind = kind, ChannelIds = ["A", "B"], Phases = [], Formulas = ["2*t"], SampleRate = 10, Duration = 1 };
        var curve = CurveGenerator.Generate(settings, "id", "name", "1", "tester", DateTimeOffset.UnixEpoch); Assert.Equal(curve.Channels[0].Samples, curve.Channels[1].Samples); Assert.Equal(10, curve.Count);
    }
    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("1", "1.0", 0)]
    [InlineData("1.0", "1", 0)]
    [InlineData("1.9", "1.10", -1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1.alpha", "1.beta", -1)]
    [InlineData("1.1", "1.beta", -1)]
    [InlineData("1.alpha", "1.1", 1)]
    public void Versions_compare_numeric_segments_and_ordinal_suffixes(string? left, string? right, int expected)
        => Assert.Equal(expected, Math.Sign(VersionComparer.Instance.Compare(left, right)));
}
