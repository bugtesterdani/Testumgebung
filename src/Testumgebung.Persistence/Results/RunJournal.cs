/**
 * @file
 * @brief Schreibt Zwischenaufzeichnungen und rekonstruiert unvollständige Prüflingsläufe nach einem Neustart.
 */

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Testumgebung.Contracts;

namespace Testumgebung.Persistence.Results;

/// <summary>Kopf eines Zwischenaufzeichnungs-Journals (erste Zeile).</summary>
public sealed class JournalHeader
{
    public string RunId { get; set; } = "";
    public string StationRunId { get; set; } = "";
    public string Station { get; set; } = "";
    public string TestName { get; set; } = "";
    public string TestVersion { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    public string? Order { get; set; }
    public string? DutId { get; set; }

    /// <summary>Schritte, die die Prüflingskennung schreiben (Kennung bei Absturz aus deren letztem Wert).</summary>
    public List<string> DutIdWriters { get; set; } = [];

    /// <summary>Knoten, die laut Filter nicht in die Ergebnisdatei bzw. Datenbank gehören (Wiederherstellung beachtet sie).</summary>
    public List<string> ExcludedFromFile { get; set; } = [];
    public List<string> ExcludedFromDatabase { get; set; } = [];
    public List<ResultNode> Structure { get; set; } = [];
}

/// <summary>Unvollständiger Prüflingstest nach Programmende, Absturz oder Stromausfall.</summary>
public sealed record IncompleteRun(string Path, JournalHeader Header, IReadOnlyList<ResultEvent> Events, DateTimeOffset LastActivity, bool Damaged)
{
    public string DutId => Header.DutId is { Length: > 0 } id ? id : "ohne Kennung";
    public string Title => $"{Header.Station} · {DutId} · {Header.TestName} v{Header.TestVersion}";
    public string Summary => $"Begonnen {Header.Started.ToLocalTime():dd.MM.yyyy HH:mm:ss}, letzte Aufzeichnung {LastActivity.ToLocalTime():HH:mm:ss}, "
                             + $"{Events.Count} Ereignis(se){(Damaged ? ", letzte Zeile unvollständig" : "")}";
}

/// <summary>
/// Interne Zwischenaufzeichnung (Abschnitt 15 „unvollständige Runs“): je laufendem Prüflingstest ein Journal
/// (JSON-Zeilen, nach jedem Ereignis auf den Datenträger geschrieben). Nach erfolgreicher Speicherung wird es gelöscht.
/// Bleibt es nach einem Absturz liegen, entscheidet ein Mensch: als abgebrochen (ERROR) speichern oder verwerfen.
/// Es gibt keine automatische Weiterprüfung.
/// </summary>
public sealed class RunJournal(string folder)
{
    public const string Extension = ".journal";
    public const string DiscardedFolderName = "verworfen";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string Folder { get; } = folder;

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "Zwischenaufzeichnung");

    public string PathFor(string runId)
    {
        var safe = string.Concat(runId.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return System.IO.Path.Combine(Folder, safe + Extension);
    }

    /// <summary>Journal für einen beginnenden Prüflingstest anlegen; null, wenn der Ordner nicht beschreibbar ist (Lauf geht weiter).</summary>
    public JournalWriter? Begin(JournalHeader header)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = PathFor(header.RunId);
            // FileShare.Delete: Eine Wiederherstellung (anderer Prozess/Neustart) darf das Journal auch löschen, solange noch ein
            // Schreiber es offen hält – unter Windows sonst „Zugriff verweigert“. Bei einem echten Absturz ist der Schreiber ohnehin weg.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, 4096, FileOptions.WriteThrough);
            lock (_gate) _open.Add(path);
            var writer = new JournalWriter(this, path, stream);
            writer.WriteLine("H", JsonSerializer.Serialize(header, Json));
            return writer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void Closed(string path)
    {
        lock (_gate) _open.Remove(path);
    }

    /// <summary>Prüflingstest vollständig gespeichert → Journal entfernen.</summary>
    public void Complete(string runId)
    {
        try
        {
            File.Delete(PathFor(runId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // bleibt als „unvollständig“ sichtbar – lieber doppelt angeboten als verloren
        }
    }

    /// <summary>Journale ohne aktiven Schreiber (dieses Programm) – älteste zuerst.</summary>
    public IReadOnlyList<IncompleteRun> FindIncomplete()
    {
        if (!Directory.Exists(Folder)) return [];
        string[] open;
        lock (_gate) open = [.. _open];
        var list = new List<IncompleteRun>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*" + Extension))
        {
            if (open.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            if (Read(path) is { } run) list.Add(run);
        }
        return list.OrderBy(r => r.Header.Started).ToList();
    }

    /// <summary>Journal lesen; eine abgeschnittene letzte Zeile (Stromausfall) wird übersprungen.</summary>
    private static void ResolveDutId(JournalHeader header, List<ResultEvent> events)
    {
        if (string.IsNullOrEmpty(header.DutId))
        {
            var writers = header.DutIdWriters.ToHashSet(StringComparer.Ordinal);
            header.DutId = events.LastOrDefault(e => e.Kind == ResultEventKind.Step && e.NodeId is not null && writers.Contains(e.NodeId)
                                                     && e.Value is string { Length: > 0 })?.Value as string;
        }
    }

    public static IncompleteRun? Read(string path)
    {
        string[] lines;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        JournalHeader? header = null;
        var events = new List<ResultEvent>();
        var damaged = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 2) continue;
            try
            {
                switch (line[0])
                {
                    case 'H':
                        header = JsonSerializer.Deserialize<JournalHeader>(line[2..], Json);
                        break;
                    case 'E':
                        if (JsonSerializer.Deserialize<JournalEvent>(line[2..], Json) is { } e) events.Add(e.ToEvent());
                        break;
                    case 'D':
                        if (header is not null) header.DutId = line[2..];
                        break;
                }
            }
            catch (JsonException)
            {
                damaged = true;
            }
        }
        if (header is null) return null;
        ResolveDutId(header, events);
        var last = events.Count > 0 ? events.Max(e => e.Time) : header.Started;
        return new IncompleteRun(path, header, events, last, damaged);
    }

    /// <summary>Unvollständigen Lauf als abgebrochenen Prüflingstest (ERROR) für die Speicherung aufbereiten.</summary>
    public static ResultRun ToAbortedRun(IncompleteRun incomplete, DateTimeOffset now, string user)
    {
        var h = incomplete.Header;
        var events = incomplete.Events.ToList();
        var seq = events.Count == 0 ? 0 : events.Max(e => e.Sequence);
        events.Add(new ResultEvent(seq + 1, ResultEventKind.Diagnostic, null, "", 0, now, ResultRating.Error,
            Value: user, Message: $"Unvollständiger Prüflingstest (Programmende, Absturz oder Stromausfall) – nach Neustart als abgebrochen gespeichert. "
                                  + $"Letzte Aufzeichnung {incomplete.LastActivity.ToLocalTime():dd.MM.yyyy HH:mm:ss}. Kurven werden nicht zwischengespeichert."));
        return new ResultRun
        {
            RunId = h.RunId,
            StationRunId = h.StationRunId,
            Station = h.Station,
            DutId = h.DutId ?? "",
            Order = h.Order ?? "",
            TestName = h.TestName,
            TestVersion = h.TestVersion,
            Started = h.Started,
            Ended = incomplete.LastActivity,
            Rating = ResultRating.Error,
            OperatorAbort = false,
            Structure = h.Structure,
            Events = events,
        };
    }

    /// <summary>Verwerfen: Journal nicht löschen, sondern in „verworfen“ ablegen (nachvollziehbar).</summary>
    public void Discard(IncompleteRun incomplete)
    {
        var target = System.IO.Path.Combine(Folder, DiscardedFolderName);
        Directory.CreateDirectory(target);
        File.Move(incomplete.Path, System.IO.Path.Combine(target, System.IO.Path.GetFileName(incomplete.Path)), overwrite: true);
    }
}

/// <summary>Schreibt Ereignisse eines Prüflingstests; jede Zeile wird sofort auf den Datenträger geschrieben.</summary>
public sealed class JournalWriter : IDisposable
{
    private readonly RunJournal _owner;
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private bool _closed;

    internal JournalWriter(RunJournal owner, string path, FileStream stream)
    {
        _owner = owner;
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    internal void WriteLine(string kind, string json)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(kind + " " + json + "\n");
                _stream.Write(bytes);
                _stream.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Zwischenaufzeichnung ist eine Absicherung – der Prüfablauf läuft weiter.
            }
        }
    }

    public void Append(ResultEvent e) => WriteLine("E", JsonSerializer.Serialize(JournalEvent.From(e), RunJournal.Json));

    public void SetDutId(string dutId) => WriteLine("D", dutId.Replace('\n', ' '));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _stream.Dispose();
        }
        _owner.Closed(Path);
    }
}

/// <summary>Ereignis im Journal; der Wert wird typisiert als Text abgelegt (Kurven nur als Verweis).</summary>
internal sealed class JournalEvent
{
    public long S { get; set; }
    public ResultEventKind K { get; set; }
    public string? N { get; set; }
    public string P { get; set; } = "";
    public int I { get; set; }
    public DateTimeOffset T { get; set; }
    public ResultRating? R { get; set; }
    public char? Vt { get; set; }
    public string? V { get; set; }
    public string? U { get; set; }
    public double? Lo { get; set; }
    public double? Hi { get; set; }
    public string? M { get; set; }
    public bool C { get; set; }
    public int? W { get; set; }

    public static JournalEvent From(ResultEvent e)
    {
        var (vt, v) = e.Value switch
        {
            null => ((char?)null, (string?)null),
            bool b => ('b', b ? "1" : "0"),
            long l => ('l', l.ToString(CultureInfo.InvariantCulture)),
            double d => ('d', d.ToString("R", CultureInfo.InvariantCulture)),
            DateTime dt => ('t', dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            _ => ('s', Convert.ToString(e.Value, CultureInfo.InvariantCulture)),
        };
        return new JournalEvent
        {
            S = e.Sequence,
            K = e.Kind,
            N = e.NodeId,
            P = e.Path,
            I = e.Iteration,
            T = e.Time,
            R = e.Rating,
            Vt = vt,
            V = v,
            U = e.Unit,
            Lo = e.LowLimit,
            Hi = e.HighLimit,
            M = e.Message,
            C = e.Cancelled,
            W = e.WaveformIds?.Count,
        };
    }

    public ResultEvent ToEvent()
    {
        object? value = Vt switch
        {
            'b' => V == "1",
            'l' => long.Parse(V!, CultureInfo.InvariantCulture),
            'd' => double.Parse(V!, CultureInfo.InvariantCulture),
            't' => DateTime.Parse(V!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            's' => V,
            _ => null,
        };
        var message = W is > 0 ? (M is null ? "" : M + " · ") + $"{W} Kurve(n) nicht zwischengespeichert" : M;
        return new ResultEvent(S, K, N, P, I, T, R, value, U, Lo, Hi, message, C);
    }
}
