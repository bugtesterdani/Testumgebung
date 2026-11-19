using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class PartialSlopeWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void E3_final_disjoint_window_detects_the_last_fast_ramp()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", [new("U", "V", null)], Now, duration: 2.29,
            slope: new(SlopeWindowMode.Disjoint, 50, -60, 6));
        for (var index = 0; index <= 229; index++)
        {
            var time = index * 0.01;
            var value = time <= 1.7 ? time * 5 : time <= 2.2 ? 8.5 : 8.5 + (time - 2.2) / 0.09;
            recordings.Add("owner", "trace", time, [value], Now);
        }
        var result = recordings.Stop("owner", "trace");
        Assert.False(result.Passed);
        Assert.Single(Assert.Single(result.Channels).Violations);
        Assert.Equal(2.29, Assert.Single(result.Channels[0].PartialWindowTimes), 8);
    }
}
