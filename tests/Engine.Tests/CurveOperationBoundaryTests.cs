using Testumgebung.Domain.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class CurveOperationBoundaryTests
{
    private static CurveDefinition Curve(double[] values, double[]? times = null, double rate = 10)
        => new() { Id = "curve", Name = "Curve", Version = "1", SampleRate = rate, Times = times, Channels = [new CurveChannel("A", "V", values), new CurveChannel("B", "A", values.Select(v => v * 2).ToArray())] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shift_preserves_sample_alignment_and_does_not_mutate_the_original(bool explicitTimes)
    {
        var curve = Curve([1, 2, 3], explicitTimes ? [0, .1, .2] : null);
        var delayed = CurveOps.Shift(curve, .1);
        Assert.Equal(explicitTimes ? new double[] { 1, 2, 3 } : [1, 1, 2, 3], delayed.Channels[0].Samples);
        Assert.Equal(explicitTimes ? new double[] { .1, .2, .3 } : null, delayed.Times, new ApproximateArrayComparer());
        var advanced = CurveOps.Shift(curve, -.1);
        Assert.Equal(new double[] { 2, 3 }, advanced.Channels[0].Samples);
        Assert.Equal(new double[] { 1, 2, 3 }, curve.Channels[0].Samples);
        Assert.Throws<ArgumentException>(() => CurveOps.Shift(curve, -1));
        if (!explicitTimes) Assert.Same(curve, CurveOps.Shift(curve, .001));
        Assert.NotEmpty(delayed.History);
    }

    [Fact]
    public void Positive_shift_of_empty_channels_uses_zero_as_initial_value()
    {
        var delayed = CurveOps.Shift(Curve([]), .2);
        Assert.Equal(new double[] { 0, 0 }, delayed.Channels[0].Samples);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trim_scale_resample_and_mapping_keep_channel_identity_and_time_basis(bool explicitTimes)
    {
        var curve = Curve([1, 2, 3], explicitTimes ? [0, .1, .2] : null);
        var trimmed = CurveOps.Trim(curve, .1, .3); Assert.Equal(new double[] { 2, 3 }, trimmed.Channels[0].Samples);
        if (explicitTimes) Assert.Equal(new double[] { 0, .1 }, trimmed.Times!);
        Assert.Throws<ArgumentException>(() => CurveOps.Trim(curve, .2, .1)); Assert.Throws<ArgumentException>(() => CurveOps.Trim(curve, 1, 2));
        var scaled = CurveOps.Scale(curve, -2, "A"); Assert.Equal(new double[] { -2, -4, -6 }, scaled.Channels[0].Samples); Assert.Same(curve.Channels[1], scaled.Channels[1]);
        Assert.Equal(new double[] { 4, 8, 12 }, CurveOps.Scale(curve, 2).Channels[1].Samples);
        Assert.Throws<ArgumentException>(() => CurveOps.Scale(curve, double.NaN)); Assert.Throws<ArgumentException>(() => CurveOps.Scale(curve, 1, "missing"));
        var resampled = CurveOps.Resample(curve, 20); Assert.Null(resampled.Times); Assert.Equal(5, resampled.Count); Assert.Equal(1.5, resampled.Channels[0].Samples[1], 9);
        Assert.Throws<ArgumentException>(() => CurveOps.Resample(curve, 0)); Assert.Throws<ArgumentException>(() => CurveOps.Resample(curve, CurveGenerator.MaxSamples * 100));
        var mapped = CurveOps.MapChannels(curve, [("B", "output")]); Assert.Equal("output", Assert.Single(mapped.Channels).Id); Assert.Equal(curve.Channels[1].Samples, mapped.Channels[0].Samples);
        Assert.Throws<ArgumentException>(() => CurveOps.MapChannels(curve, [("missing", "output")]));
        Assert.Throws<ArgumentException>(() => CurveOps.MapChannels(curve, [("A", "same"), ("B", "same")]));
        Assert.Empty(CurveOps.Validate(curve));
    }

    [Theory]
    [InlineData("empty", "Keine Kanäle")]
    [InlineData("length", "unterschiedliche Längen")]
    [InlineData("duplicate", "nicht eindeutig")]
    [InlineData("nan", "nicht endliche Werte")]
    [InlineData("times-count", "Anzahl Zeitpunkte")]
    [InlineData("times-order", "nicht streng steigend")]
    [InlineData("rate", "Abtastrate fehlt")]
    public void Validation_reports_invalid_channel_or_time_metadata(string damage, string expected)
    {
        IReadOnlyList<CurveChannel> channels = damage switch { "empty" => [], "length" => [new("A", "V", [1]), new("B", "V", [1, 2])], "duplicate" => [new("A", "V", [1]), new("A", "V", [1])], "nan" => [new("A", "V", [double.NaN, double.PositiveInfinity])], _ => [new("A", "V", [1, 2])] };
        var curve = new CurveDefinition { Id = "c", Name = "c", Version = "1", Channels = channels, SampleRate = damage == "rate" ? 0 : 10, Times = damage switch { "times-count" => [0], "times-order" => [0, 0], _ => null } };
        Assert.Contains(CurveOps.Validate(curve), text => text.Contains(expected));
    }

    private sealed class ApproximateArrayComparer : IEqualityComparer<double[]?>
    {
        public bool Equals(double[]? x, double[]? y) => x is null || y is null ? x is null && y is null : x.Length == y.Length && x.Zip(y).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9);
        public int GetHashCode(double[]? obj) => 0;
    }
}
