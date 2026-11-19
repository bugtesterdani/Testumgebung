using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class SlopeLivePreviewTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(SlopeWindowMode.Consecutive, 2)]
    [InlineData(SlopeWindowMode.Disjoint, 50)]
    [InlineData(SlopeWindowMode.Rolling, 50)]
    public void First_raw_sample_is_reported_without_waiting_for_a_slope_window(SlopeWindowMode mode, int points)
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now,
            slope: new(mode, points, -1, 1));
        var preview = recordings.Add("owner", "trace", 0, [5], Now);
        Assert.NotNull(preview);
        Assert.False(preview.Finished);
        var raw = Assert.Single(preview.Data.Recording!.RawChannels);
        Assert.Equal(5, Assert.Single(raw.Values));
        Assert.DoesNotContain(preview.Data.Channels, wave => wave.Count > 0 && wave.Unit == "V/s");
    }
}
