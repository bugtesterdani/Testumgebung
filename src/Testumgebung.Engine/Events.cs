using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine;

/// <summary>
/// Ausführungsereignisse – Grundlage für Protokoll, Werkeranzeige, Ergebnisdatei und Datenbank (spätere Etappen).
/// </summary>
public abstract record EngineEvent
{
    public required DateTimeOffset Time { get; init; }
    public required string RunId { get; init; }
    public long Sequence { get; internal set; }
}

/// <summary>Ereignis zu einem Element des Ablaufs.</summary>
public abstract record NodeEvent : EngineEvent
{
    public required string NodeId { get; init; }
    public required string Path { get; init; }

    /// <summary>Iteration der direkt umgebenden Gruppe (1-basiert).</summary>
    public int Iteration { get; init; }
}

public sealed record StateChanged(EngineState State) : EngineEvent;

public sealed record StepStarted : NodeEvent;

public sealed record StepCompleted(
    ResultRating Rating,
    object? Value,
    string? Unit,
    double? LowLimit,
    double? HighLimit,
    string? Message,
    bool Cancelled) : NodeEvent;

public sealed record GroupEntered(string ScopeId, bool IsExit) : NodeEvent;

public sealed record GroupNotEntered(string Reason) : NodeEvent;

public sealed record GroupIterationStarted(int GroupIteration) : NodeEvent;

public sealed record GroupCompleted(ResultRating Rating, int Iterations, bool IsExit) : NodeEvent;

public sealed record ResourceWaitStarted(IReadOnlyList<string> Resources) : NodeEvent;

public sealed record ResourceAcquired(IReadOnlyList<string> Resources) : NodeEvent;

public sealed record ResourceNotAcquired(IReadOnlyList<string> Resources, string Reason) : NodeEvent;

public sealed record ResourceReleased(IReadOnlyList<string> Resources) : NodeEvent;

/// <summary>Werkerabbruch angefordert (nummeriertes Ereignis).</summary>
public sealed record OperatorAbortRequested(int Number, string Effect) : EngineEvent;

/// <summary>Eine laufende Exit-Gruppe wurde durch erneuten Werkerabbruch beendet.</summary>
public sealed record ExitGroupAborted : NodeEvent;

public sealed record Paused(PauseInfo Info) : EngineEvent;

public sealed record Resumed(DebugCommand Command) : EngineEvent;

public sealed record Diagnostic(string? NodeId, string Message) : EngineEvent;

/// <summary>Beginn eines Prüflingstests (eigener Run innerhalb des Loops).</summary>
public sealed record DutRunStarted(string DutRunId) : NodeEvent { public string? Order { get; init; } }

/// <summary>Ende eines Prüflingstests – Grundlage zum Speichern (auch bei WERROR).</summary>
public sealed record DutRunCompleted(
    string DutRunId,
    string DutId,
    RunStatus Status,
    DateTimeOffset Started,
    string TestVersion) : NodeEvent { public string? Order { get; init; } }

/// <summary>Inhalt für die Werkeroberfläche (Anweisung, Bild, Eingabe, Auswahl). Anzeige ab sofort.</summary>
public sealed record OperatorContentShown(OperatorPrompt Prompt) : NodeEvent;

/// <summary>Antwort des Werkers wurde übernommen.</summary>
public sealed record OperatorAnswered(string DisplayId, object? Value) : NodeEvent;

/// <summary>Gruppe verlassen: „bis Gruppenende“ sichtbare Inhalte dieser Gruppe entfernen.</summary>
public sealed record OperatorContentCleared(string GroupScopeId) : NodeEvent;

/// <summary>Entwicklereingriff (Variable geändert, Haltepunkt, Patch) – Lauf wird als Entwicklungslauf markiert.</summary>
public sealed record DevelopmentEvent(string User, string Kind, string Details) : EngineEvent;

/// <summary>Neue freigegebene Version ab diesem Prüflingstest aktiv (V01).</summary>
public sealed record VersionActivated(string FromVersion, string ToVersion) : EngineEvent;
