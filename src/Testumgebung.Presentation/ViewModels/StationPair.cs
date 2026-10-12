using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;

namespace Testumgebung.Presentation.ViewModels;

public sealed partial class StationViewModel
{
    internal PairedStationContext? PairContext { get; set; }
    private readonly SemaphoreSlim _operatorGate = new(1, 1);

    async Task<object?> IOperatorInterface.AskAsync(string runId, OperatorPrompt prompt, CancellationToken cancellationToken)
    {
        await _operatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await AskCoreAsync(runId, prompt, cancellationToken).ConfigureAwait(false); }
        finally { _operatorGate.Release(); }
    }
}
