using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public sealed class HandoverWaitAction(string channel, int firstSide) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var paired = context.PairedStation ?? throw new InvalidOperationException("Freigaben benötigen einen gemeinsamen Zwei-Platz-Auftrag.");
        context.Log($"Seite {paired.Side}: Freigabe „{channel}“ abwarten.");
        await paired.Handover.WaitAsync(paired.Side, channel, firstSide, context.CancellationToken).ConfigureAwait(false);
        return StepOutcome.Unrated(paired.Side, "Freigabe erhalten.");
    }
}

public sealed class HandoverReleaseAction(string channel) : IStepAction
{
    public Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var paired = context.PairedStation ?? throw new InvalidOperationException("Freigaben benötigen einen gemeinsamen Zwei-Platz-Auftrag.");
        paired.Handover.Release(paired.Side, channel);
        return Task.FromResult(StepOutcome.Unrated(paired.Side == 1 ? 2 : 1, "Andere Seite freigegeben."));
    }
}
