/**
 * @file
 * @brief Beschreibt gespeicherte Läufe, Ereignisse, Kurven und Anhänge unabhängig von Dateiformat und Anzeige.
 */

using Testumgebung.Contracts;

namespace Testumgebung.Persistence.Results;

public enum ResultEventKind
{
    Step,
    Group,
    GroupNotEntered,
    OperatorShown,
    OperatorAnswer,
    Diagnostic,
    Development,
    Abort,
}

/// <summary>Element der Ablaufstruktur (einmalig je Datei; Ereignisse referenzieren es).</summary>
public sealed record ResultNode(string Id, string? ParentId, string Name, string Kind, string? BlockType, string? Description);

/// <summary>
/// Ereignis eines Prüflingstests. <see cref="Value"/> ist null, bool, long, double, string oder DateTime;
/// Kurven werden über <see cref="WaveformIds"/>, Bilder über <see cref="AttachmentId"/> referenziert.
/// </summary>
public sealed record ResultEvent(
    long Sequence,
    ResultEventKind Kind,
    string? NodeId,
    string Path,
    int Iteration,
    DateTimeOffset Time,
    ResultRating? Rating = null,
    object? Value = null,
    string? Unit = null,
    double? LowLimit = null,
    double? HighLimit = null,
    string? Message = null,
    bool Cancelled = false,
    IReadOnlyList<string>? WaveformIds = null,
    string? AttachmentId = null);

/// <summary>Kurve mit Bezug auf das erzeugende Ereignis.</summary>
public sealed record ResultWaveform(string Id, long EventSequence, string? NodeId, Waveform Data);

/// <summary>Eingebetteter Anhang (z. B. Anweisungsbild). Id = SHA-256 des Inhalts → dedupliziert.</summary>
public sealed record ResultAttachment(string Id, string Name, string MediaType, byte[] Data);

/// <summary>Ein Prüflingstest – Inhalt einer Ergebnisdatei bzw. eines Outbox-Eintrags.</summary>
public sealed class ResultRun
{
    public required string RunId { get; init; }
    public string StationRunId { get; init; } = "";
    public string Station { get; init; } = "";
    public string DutId { get; init; } = "";
    public string Order { get; init; } = "";
    public string TestName { get; init; } = "";
    public string TestVersion { get; init; } = "";
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset Ended { get; init; }
    public ResultRating Rating { get; init; }
    public bool OperatorAbort { get; init; }
    public bool IsDevelopmentRun { get; init; }

    /// <summary>Für welches Ziel gefiltert wurde („Ergebnisdatei“, „Datenbank“, leer = ungefiltert).</summary>
    public string FilteredFor { get; init; } = "";

    public string Application { get; init; } = "Testumgebung";

    public RunStatus Status => new(Rating, OperatorAbort);

    public List<ResultNode> Structure { get; init; } = [];
    public List<ResultEvent> Events { get; init; } = [];
    public List<ResultWaveform> Waveforms { get; init; } = [];
    public List<ResultAttachment> Attachments { get; init; } = [];

    public ResultRun CopyWith(string filteredFor, List<ResultEvent> events, List<ResultWaveform> waveforms, List<ResultAttachment> attachments) => new()
    {
        RunId = RunId,
        StationRunId = StationRunId,
        Station = Station,
        DutId = DutId,
        Order = Order,
        TestName = TestName,
        TestVersion = TestVersion,
        Started = Started,
        Ended = Ended,
        Rating = Rating,
        OperatorAbort = OperatorAbort,
        IsDevelopmentRun = IsDevelopmentRun,
        FilteredFor = filteredFor,
        Application = Application,
        Structure = Structure,
        Events = events,
        Waveforms = waveforms,
        Attachments = attachments,
    };

    public override string ToString() => $"{RunId} {DutId} {Status}";
}
