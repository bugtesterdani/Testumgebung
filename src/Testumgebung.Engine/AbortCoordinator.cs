/**
 * @file
 * @brief Verteilt Werkerabbrüche auf regulären Ablauf und verschachtelte Exit-Phasen statt alle Cleanup-Schritte zu überspringen.
 */

namespace Testumgebung.Engine;

/// <summary>
/// Ausführungsphase mit eigenem Abbruchsignal. Die reguläre Phase gilt für den gesamten regulären Ablauf;
/// jede gestartete Exit-Gruppe erhält eine neue Phase (Abschnitt 2.4).
/// </summary>
internal sealed class ExecutionPhase(string name, bool isExit, string? exitNodeId = null) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public string Name { get; } = name;
    public bool IsExit { get; } = isExit;
    public string? ExitNodeId { get; } = exitNodeId;
    public CancellationToken Token => _cts.Token;
    public bool IsCancelled => _cts.IsCancellationRequested;

    /// <summary>
    /// Signal setzen. Callbacks laufen asynchron, damit der Aufrufer (z. B. UI-Thread des Abbruchknopfs)
    /// nicht die Engine-Fortsetzung synchron ausführt. <see cref="IsCancelled"/> ist sofort wahr.
    /// </summary>
    public void Cancel() => _ = _cts.CancelAsync();

    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// Verteilt nummerierte Werkerabbruch-Ereignisse (Vorschlag aus Abschnitt 2.4):
/// <list type="number">
/// <item>Erster Abbruch: reguläre Phase abbrechen – keine weiteren regulären Schritte.
/// Eine bereits gestartete Exit-Gruppe darf ihre Wiederholungen zunächst ausführen.</item>
/// <item>Jeder weitere Abbruch: nur die innerste laufende Exit-Gruppe abbrechen.
/// Die nächste äußere Exit-Gruppe erhält beim Start eine neue Phase und läuft normal.</item>
/// </list>
/// Der Abbruch ist damit kein dauerhaft gesetztes Signal, das alle äußeren Exit-Gruppen überspringt.
/// </summary>
internal sealed class AbortCoordinator
{
    private readonly object _gate = new();
    private readonly List<ExecutionPhase> _exitStack = [];

    public AbortCoordinator()
    {
        Regular = new ExecutionPhase("regulär", isExit: false);
    }

    public ExecutionPhase Regular { get; }

    public int AbortCount { get; private set; }

    public bool OperatorAborted
    {
        get { lock (_gate) return AbortCount > 0; }
    }

    public ExecutionPhase PushExit(string exitNodeId, string name)
    {
        var phase = new ExecutionPhase(name, isExit: true, exitNodeId);
        lock (_gate) _exitStack.Add(phase);
        return phase;
    }

    public void Pop(ExecutionPhase phase)
    {
        lock (_gate) _exitStack.Remove(phase);
    }

    /// <summary>Registriert einen Werkerabbruch und liefert Nummer und Wirkung.</summary>
    public (int Number, string Effect, ExecutionPhase? Cancelled) RequestAbort()
    {
        lock (_gate)
        {
            AbortCount++;
            if (!Regular.IsCancelled)
            {
                Regular.Cancel();
                return (AbortCount, "Reguläre Schritte gestoppt; Exit-Gruppen von innen nach außen.", Regular);
            }
            var top = _exitStack.LastOrDefault(p => !p.IsCancelled);
            if (top is not null)
            {
                top.Cancel();
                return (AbortCount, $"Aktuelle Exit-Gruppe '{top.Name}' beendet.", top);
            }
            return (AbortCount, "Keine laufende Exit-Gruppe – ohne Wirkung.", null);
        }
    }
}
