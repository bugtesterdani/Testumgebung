using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class TraceRestartTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static TraceChannelDefinition[] Channels() => [new("U", "V", null) { SafetyMaximum = 10 }];

    [Fact]
    public void Early_fail_keeps_its_result_until_an_explicit_restart_then_starts_fresh()
    {
        var recordings = new EnvelopeRecordings();
        recordings.Start("owner", "trace", Channels(), Now, formula: new FormulaLimits("0", "10"));
        var failed = recordings.Add("owner", "trace", 0, [11], Now)!;
        Assert.True(failed.Finished);
        Assert.False(failed.Passed);
        recordings.Start("owner", "trace", Channels(), Now.AddSeconds(1), duration: 1, formula: new FormulaLimits("0", "10"));
        Assert.False(recordings.IsEnded("owner", "trace"));
        Assert.False(recordings.HasViolation("owner", "trace"));
        Assert.False(recordings.IsPaused("owner", "trace"));
        recordings.Add("owner", "trace", null, [5], Now.AddSeconds(1));
        recordings.Add("owner", "trace", null, [5], Now.AddSeconds(2));
        var next = recordings.Stop("owner", "trace");
        Assert.True(next.Passed);
        Assert.Equal(new double[] { 0, 1 }, next.Data.Recording!.RawChannels[0].Times);
        Assert.NotNull(failed.EarlyStop);
        Assert.Equal(11, Assert.Single(failed.Data.Recording!.RawChannels[0].Values));
    }
}
