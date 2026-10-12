using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class OutputInterpolationBoundaryTests
{
    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, .5, 5)]
    [InlineData(false, 1, 10)]
    [InlineData(false, 1.5, 5)]
    [InlineData(false, 2, 0)]
    [InlineData(false, -.5, 5)]
    [InlineData(true, 0, 0)]
    [InlineData(true, .5, 5)]
    [InlineData(true, 1, 10)]
    [InlineData(true, 1.5, 10)]
    [InlineData(true, 2, 0)]
    [InlineData(true, -.5, 10)]
    public void Uniform_and_explicit_times_interpolate_and_repeat(bool explicitTimes, double time, double expected)
    {
        var curve = new OutputCurve("curve", 1, explicitTimes ? [10, 11] : null, [new OutputChannelData("a", "V", [0, 10]), new OutputChannelData("b", "V", [100, 110])]);
        Assert.Equal(2, curve.Count); Assert.Equal(2, curve.Duration);
        Assert.Equal(expected, curve.ValueAt(0, time), 9); Assert.Equal(expected + 100, curve.ValueAt(1, time), 9);
    }

    [Fact]
    public void Empty_and_single_sample_curves_have_defined_values()
    {
        var empty = new OutputCurve("empty", 1, null, []); Assert.Equal(0, empty.Count); Assert.Equal(0, empty.Duration);
        var emptyChannel = empty with { Channels = [new OutputChannelData("a", "V", [])] }; Assert.Equal(0, emptyChannel.ValueAt(0, 10));
        var single = empty with { Channels = [new OutputChannelData("a", "V", [7])] };
        Assert.Equal(7, single.ValueAt(0, .5)); Assert.Equal(7, (single with { Times = [5] }).ValueAt(0, 0));
        Assert.Equal(0, (single with { Times = [5] }).Duration);
    }

    [Theory]
    [InlineData(SecurityKey.Method.Xor, "010203FF", "1020", "112213DF")]
    [InlineData(SecurityKey.Method.Add, "010203FF", "1020", "1122131F")]
    [InlineData(SecurityKey.Method.Invert, "010203FF", "", "FEFDFC00")]
    [InlineData(SecurityKey.Method.AddRotateXor, "0180FF", "", "585BA5")]
    [InlineData(SecurityKey.Method.AddRotateXor, "0180FF", "01", "5E595A")]
    [InlineData(SecurityKey.Method.AddRotateXor, "0180FF", "0110", "141310")]
    public void Security_key_known_vectors_cover_repeated_constants_and_overflow(SecurityKey.Method method, string seed, string parameter, string expected)
        => Assert.Equal(expected, Convert.ToHexString(SecurityKey.Compute(method, Convert.FromHexString(seed), Convert.FromHexString(parameter))));

    [Theory]
    [InlineData(SecurityKey.Method.Xor)]
    [InlineData(SecurityKey.Method.Add)]
    public void Security_key_requires_constant(SecurityKey.Method method)
    {
        Assert.Throws<CommunicationException>(() => SecurityKey.Compute(method, [1], []));
        Assert.Empty(SecurityKey.Compute(method, [], [1]));
    }
}
