/**
 * @file
 * @brief Bewahrt ausstehende Datenbankübertragungen dauerhaft und organisiert spätere idempotente Wiederholungen.
 */

using System.Text.Json;
using Testumgebung.Contracts;

namespace Testumgebung.Persistence.Results;

/// <summary>
/// Datenbankadapter für die dauerhafte Speicherung abgeschlossener Prüflingsläufe.
/// Vertrag: eine Transaktion je Run, Unique-Key auf der Run-ID, idempotente Einfügung – ein bereits vorhandener Run
/// gilt als Erfolg. So erzeugt eine Wiederholung nach verlorener Commit-Antwort keine Duplikate.
/// </summary>
public interface IRunDatabase
{
    /// <summary>Lesbare Bezeichnung des Speicherziels für Diagnose und Oberfläche.</summary>
    string Name { get; }

    /// <summary>Speichert einen vollständigen Lauf atomar und idempotent anhand seiner Run-ID.</summary>
    /// <param name="run">Lauf mit Struktur, Ereignissen, Messwerten und Kurven.</param>
    /// <param name="cancellationToken">Abbruchsignal für die Speicherung.</param>
    /// <remarks>
    /// Bei bereits gespeicherter Run-ID gilt der Aufruf als erfolgreich. Ein Fehler nach einem
    /// Commit kann beim Aufrufer trotzdem als Fehlschlag erscheinen; erneutes Speichern darf
    /// dann keine Duplikate erzeugen. Erst die erfolgreiche Rückkehr bestätigt die Speicherung.
    /// </remarks>
    Task StoreAsync(ResultRun run, CancellationToken cancellationToken);
}

public enum RunNotificationKind
{
    /// <summary>Initiale Speicherung am Testende erfolgreich.</summary>
    Stored,
    /// <summary>Initiale Speicherung am Testende gescheitert (Werkerwarnung 10 s).</summary>
    StoreFailed,
    /// <summary>Späterer Outbox-Versuch gescheitert (keine Werkerwarnung).</summary>
    RetryFailed,
    /// <summary>Nachträgliche Übertragung erfolgreich.</summary>
    StoredLater,
    /// <summary>Outbox-Eintrag nicht lesbar – in Unterordner „defekt“ verschoben.</summary>
    OutboxDamaged,
}

/// <summary>Meldung je Run (und Versuch) für den zusätzlichen Meldeweg (MQTT ab Etappe 6) und die Entwickleransicht.</summary>
public sealed record RunNotification(RunNotificationKind Kind, string RunId, string DutId, DateTimeOffset Time, int Attempt, string? Error)
{
    public bool IsError => Kind is RunNotificationKind.StoreFailed or RunNotificationKind.RetryFailed or RunNotificationKind.OutboxDamaged;

    public string Text => Kind switch
    {
        RunNotificationKind.Stored => "In Datenbank gespeichert",
        RunNotificationKind.StoreFailed => $"Speicherung am Testende gescheitert: {Error}",
        RunNotificationKind.RetryFailed => $"Outbox-Versuch {Attempt} gescheitert: {Error}",
        RunNotificationKind.StoredLater => $"Nachträglich übertragen (Versuch {Attempt})",
        RunNotificationKind.OutboxDamaged => $"Outbox-Eintrag defekt: {Error}",
        _ => Kind.ToString(),
    };
}

/// <summary>Zusätzlicher Meldeweg. Strikt best effort: Fehler dürfen den Test nie beeinflussen.</summary>
public interface IRunNotifier
{
    void Notify(RunNotification notification);
}

public sealed record OutboxOptions
{
    /// <summary>Erster Abstand für Hintergrundversuche.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Faktor je weiterem Fehlversuch (exponentielles Backoff).</summary>
    public double BackoffFactor { get; init; } = 2;

    public TimeSpan MaxRetryInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Zeitlimit eines einzelnen Übertragungsversuchs.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record OutboxEntry(string RunId, string DutId, DateTimeOffset Created, int Attempts, string? LastError, DateTimeOffset NextAttempt);

public sealed record OutboxSubmitResult(bool Stored, string? Error);

/// <summary>
/// Lokale dauerhafte Outbox (Abschnitt 9.3). Ein Eintrag ist eine Ergebnisdatei mit genau den für die Datenbank
/// gewählten Daten plus Zustandsdatei. Ablauf: Enqueue (atomar auf Datenträger) → Übertragungsversuch → bei Erfolg
/// löschen, sonst im Hintergrund mit Backoff wiederholen. Nach einem Neustart lädt <see cref="Start"/> die offenen
/// Einträge; ein Herunterfahren wird nie wegen einer Übertragung verzögert.
/// </summary>
public sealed class OutboxService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IRunDatabase _database;
    private readonly IClock _clock;
    private readonly IRunNotifier? _notifier;
    private readonly object _gate = new();
    private readonly Dictionary<string, OutboxEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _loaded;

    public OutboxService(string folder, IRunDatabase database, IClock? clock = null, OutboxOptions? options = null, IRunNotifier? notifier = null)
    {
        Folder = folder;
        _database = database;
        _clock = clock ?? SystemClock.Instance;
        Options = options ?? new OutboxOptions();
        _notifier = notifier;
    }

    public string Folder { get; }
    /// <summary>Technische Parameter (Retry, Backoff, Zeitlimit) – zur Laufzeit änderbar.</summary>
    public OutboxOptions Options { get; set; }
    public IRunDatabase Database => _database;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "Outbox");

    /// <summary>Meldung je Run/Versuch (zusätzlich zu <see cref="IRunNotifier"/>).</summary>
    public event Action<RunNotification>? Notified;

    /// <summary>Offene Einträge haben sich geändert.</summary>
    public event Action? Changed;

    public IReadOnlyList<OutboxEntry> Pending
    {
        get
        {
            lock (_gate) return _entries.Values.OrderBy(e => e.Created).ToList();
        }
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>
    /// Enqueue vor dem Übertragungsversuch, dann ein initialer Versuch. Bei Misserfolg bleibt der Eintrag dauerhaft
    /// in der Outbox; der Aufrufer zeigt die Werkerwarnung.
    /// </summary>
    public async Task<OutboxSubmitResult> SubmitAsync(ResultRun run)
    {
        EnsureLoaded();
        var entry = new OutboxEntry(run.RunId, run.DutId, _clock.Now, 0, null, _clock.Now);
        await Task.Run(() =>
        {
            ResultFileStore.WriteAtomic(Folder, DataFile(run.RunId), stream => ResultFile.Write(stream, run), overwrite: true);
            WriteState(entry);
        }).ConfigureAwait(false);
        lock (_gate)
        {
            _entries[run.RunId] = entry;
            _inFlight.Add(run.RunId);
        }
        Changed?.Invoke();
        var error = await AttemptAsync(entry, initial: true).ConfigureAwait(false);
        return new OutboxSubmitResult(error is null, error);
    }

    /// <summary>Offene Einträge vom Datenträger laden (nach Programmstart) und Hintergrundversuche starten.</summary>
    public void Start()
    {
        EnsureLoaded();
        if (IsRunning) return;
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    /// <summary>Hintergrundversuche beenden. Laufende Übertragungen werden abgebrochen, nicht abgewartet.</summary>
    public async Task StopAsync()
    {
        if (_loopCts is null) return;
        await _loopCts.CancelAsync().ConfigureAwait(false);
        Wake();
        if (_loop is not null)
            await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        _loopCts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Alle offenen Einträge sofort versuchen (Bedienknopf „Jetzt übertragen“).</summary>
    public Task RetryNowAsync() => RetryDueAsync(force: true);

    /// <summary>Ein Durchgang über alle fälligen Einträge (Hintergrund).</summary>
    public async Task RetryDueAsync(bool force = false)
    {
        EnsureLoaded();
        List<OutboxEntry> due;
        lock (_gate)
        {
            var now = _clock.Now;
            due = _entries.Values.Where(e => !_inFlight.Contains(e.RunId) && (force || e.NextAttempt <= now)).OrderBy(e => e.Created).ToList();
            foreach (var e in due) _inFlight.Add(e.RunId);
        }
        foreach (var e in due) await AttemptAsync(e, initial: false).ConfigureAwait(false);
    }

    /// <summary>Lädt die Outbox einmalig vom Datenträger (Fortsetzen nach Neustart).</summary>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
        }
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.GetFiles(Folder, "*" + ResultFile.Extension))
        {
            var runId = Path.GetFileNameWithoutExtension(file);
            OutboxEntry entry;
            try
            {
                var state = Path.ChangeExtension(file, ".json");
                if (File.Exists(state) && JsonSerializer.Deserialize<OutboxEntry>(File.ReadAllText(state), Json) is { } s) entry = s;
                else
                {
                    var head = ResultFile.Read(file).Run;
                    entry = new OutboxEntry(head.RunId, head.DutId, _clock.Now, 0, null, _clock.Now);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or ResultFileException or UnauthorizedAccessException)
            {
                MoveToDamaged(runId, ex.Message, "");
                continue;
            }
            lock (_gate) _entries[entry.RunId] = entry with { NextAttempt = _clock.Now };
        }
        Changed?.Invoke();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RetryDueAsync().ConfigureAwait(false);
                TimeSpan wait;
                lock (_gate)
                {
                    var next = _entries.Values.Where(e => !_inFlight.Contains(e.RunId)).Select(e => e.NextAttempt).DefaultIfEmpty(_clock.Now + Options.RetryInterval).Min();
                    wait = next - _clock.Now;
                }
                if (wait < TimeSpan.FromMilliseconds(10)) wait = TimeSpan.FromMilliseconds(10);
                Task wake;
                lock (_gate) wake = _wake.Task;
                await Task.WhenAny(_clock.Delay(wait, token), wake).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_wake.Task.IsCompleted) _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void Wake()
    {
        lock (_gate) _wake.TrySetResult();
    }

    /// <summary>Ein Übertragungsversuch. Liefert null bei Erfolg, sonst die Fehlermeldung.</summary>
    private async Task<string?> AttemptAsync(OutboxEntry entry, bool initial)
    {
        var attempt = entry.Attempts + 1;
        string? error;
        try
        {
            ResultRun run;
            try
            {
                run = ResultFile.Read(Path.Combine(Folder, DataFile(entry.RunId))).Run;
            }
            catch (ResultFileException ex)
            {
                MoveToDamaged(entry.RunId, ex.Message, entry.DutId);
                return ex.Message;
            }

            using var cts = _loopCts is { } loop
                ? CancellationTokenSource.CreateLinkedTokenSource(loop.Token)
                : new CancellationTokenSource();
            cts.CancelAfter(Options.AttemptTimeout);
            try
            {
                await _database.StoreAsync(run, cts.Token).ConfigureAwait(false);
                error = null;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                error = $"Zeitüberschreitung nach {Options.AttemptTimeout.TotalSeconds:0} s";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error is null)
            {
                DeleteFiles(entry.RunId);
                lock (_gate) _entries.Remove(entry.RunId);
                Notify(new RunNotification(initial ? RunNotificationKind.Stored : RunNotificationKind.StoredLater,
                    entry.RunId, entry.DutId, _clock.Now, attempt, null));
            }
            else
            {
                var updated = entry with { Attempts = attempt, LastError = error, NextAttempt = _clock.Now + Backoff(attempt) };
                lock (_gate)
                {
                    if (_entries.ContainsKey(entry.RunId)) _entries[entry.RunId] = updated;
                }
                try { WriteState(updated); }
                catch (IOException) { /* Zustand ist nur Komfort; die Daten liegen bereits dauerhaft vor. */ }
                Notify(new RunNotification(initial ? RunNotificationKind.StoreFailed : RunNotificationKind.RetryFailed,
                    entry.RunId, entry.DutId, _clock.Now, attempt, error));
            }
            return error;
        }
        finally
        {
            lock (_gate) _inFlight.Remove(entry.RunId);
            Changed?.Invoke();
            Wake();
        }
    }

    private TimeSpan Backoff(int attempts)
    {
        var ms = Options.RetryInterval.TotalMilliseconds * Math.Pow(Options.BackoffFactor, Math.Max(0, attempts - 1));
        return TimeSpan.FromMilliseconds(Math.Min(ms, Options.MaxRetryInterval.TotalMilliseconds));
    }

    private void Notify(RunNotification n)
    {
        try { _notifier?.Notify(n); }
        catch (Exception) { /* Meldeweg ist best effort und darf nie stören. */ }
        try { Notified?.Invoke(n); }
        catch (Exception) { }
    }

    private void MoveToDamaged(string runId, string error, string dutId)
    {
        var damaged = Path.Combine(Folder, "defekt");
        try
        {
            Directory.CreateDirectory(damaged);
            foreach (var f in new[] { DataFile(runId), StateFile(runId) })
            {
                var src = Path.Combine(Folder, f);
                if (File.Exists(src)) File.Move(src, Path.Combine(damaged, f), overwrite: true);
            }
        }
        catch (IOException) { }
        lock (_gate) _entries.Remove(runId);
        Notify(new RunNotification(RunNotificationKind.OutboxDamaged, runId, dutId, _clock.Now, 0, error));
    }

    private void WriteState(OutboxEntry entry)
        => ResultFileStore.WriteAtomic(Folder, StateFile(entry.RunId), JsonSerializer.SerializeToUtf8Bytes(entry, Json), overwrite: true);

    private void DeleteFiles(string runId)
    {
        foreach (var f in new[] { DataFile(runId), StateFile(runId) })
        {
            try { File.Delete(Path.Combine(Folder, f)); }
            catch (IOException) { }
        }
    }

    private static string DataFile(string runId) => SafeName(runId) + ResultFile.Extension;

    private static string StateFile(string runId) => SafeName(runId) + ".json";

    private static string SafeName(string runId)
        => new(runId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}

/// <summary>Datenbank, deren Ausfall sich zur Vorführung simulieren lässt.</summary>
public interface ISimulatedDatabase
{
    bool Offline { get; set; }
}

/// <summary>Referenzimplementierung des Datenbankvertrags im Speicher (Tests, Simulation).</summary>
public sealed class InMemoryRunDatabase : IRunDatabase, ISimulatedDatabase
{
    private readonly Dictionary<string, ResultRun> _runs = new(StringComparer.Ordinal);

    public string Name => "Speicher";

    /// <summary>Simulierter Ausfall: wirft vor dem Commit.</summary>
    public bool Offline { get; set; }

    /// <summary>Simulierter Verlust der Antwort: Commit erfolgt, danach Ausnahme (P05).</summary>
    public int LoseAnswers { get; set; }

    public int StoreCalls { get; private set; }

    public IReadOnlyCollection<ResultRun> Runs
    {
        get
        {
            lock (_runs) return _runs.Values.ToList();
        }
    }

    public Task StoreAsync(ResultRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_runs)
        {
            StoreCalls++;
            if (Offline) throw new IOException("Datenbank nicht erreichbar (simuliert).");
            _runs.TryAdd(run.RunId, run); // Unique-Key Run-ID: vorhandener Run = Erfolg, kein Duplikat
            if (LoseAnswers > 0)
            {
                LoseAnswers--;
                throw new TimeoutException("Antwort nach Commit verloren (simuliert).");
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Simulierte Datenbank als Ordner (bis zu den echten Adaptern in Etappe 6): je Run eine Datei, Run-ID eindeutig,
/// Einfügen idempotent. Mit <see cref="Offline"/> lässt sich ein DB-Ausfall vorführen.
/// </summary>
public sealed class FolderRunDatabase(string folder) : IRunDatabase, ISimulatedDatabase, IRunQuerySource
{
    public Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken)
    {
        if (Offline) throw new IOException("Datenbank nicht erreichbar (simuliert offline).");
        var loaded = Testumgebung.Persistence.Reports.ResultAnalysis.Load(Folder, null, cancellationToken);
        IReadOnlyList<ResultRun> runs = loaded.Runs.Where(query.Matches).OrderBy(r => r.Started).Take(query.Limit).ToList();
        return Task.FromResult(runs);
    }

    public Task<ResultRun?> LoadRunAsync(string runId, CancellationToken cancellationToken)
    {
        if (Offline) throw new IOException("Datenbank nicht erreichbar (simuliert offline).");
        foreach (var file in Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*" + ResultFile.Extension) : [])
        {
            var content = ResultFile.Read(file);
            if (content.Run.RunId == runId) return Task.FromResult<ResultRun?>(content.Run);
        }
        return Task.FromResult<ResultRun?>(null);
    }

    public string Name => $"Simulierte Datenbank ({Folder})";
    public string Folder { get; } = folder;
    public bool Offline { get; set; }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "SimulierteDatenbank");

    public Task StoreAsync(ResultRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Offline) throw new IOException("Datenbank nicht erreichbar (simuliert offline).");
        var name = new string(run.RunId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + ResultFile.Extension;
        if (File.Exists(Path.Combine(Folder, name))) return Task.CompletedTask; // idempotent
        ResultFileStore.WriteAtomic(Folder, name, stream => ResultFile.Write(stream, run));
        return Task.CompletedTask;
    }

    public int Count => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*" + ResultFile.Extension).Length : 0;
}

/// <summary>Austauschbare Datenbank (Einstellungsänderung ohne Neustart). Ohne Ziel scheitert jeder Versuch – die Outbox behält die Runs.</summary>
public sealed class SwitchableRunDatabase : IRunDatabase, ISimulatedDatabase, IRunQuerySource
{
    /// <summary>Die aktuelle Datenbank kann gelesen werden (SQL oder simuliert).</summary>
    public bool CanQuery => _inner is IRunQuerySource;

    public Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken)
        => Source().QueryRunsAsync(query, cancellationToken);

    public Task<ResultRun?> LoadRunAsync(string runId, CancellationToken cancellationToken)
        => Source().LoadRunAsync(runId, cancellationToken);

    private IRunQuerySource Source()
        => _inner as IRunQuerySource ?? throw new IOException(_inner is null ? "Keine Datenbank konfiguriert." : $"{_inner.Name} kann nicht gelesen werden.");

    private volatile IRunDatabase? _inner;

    public IRunDatabase? Inner
    {
        get => _inner;
        set => _inner = value;
    }

    public string Name => _inner?.Name ?? "Keine Datenbank konfiguriert";

    public bool Offline
    {
        get => _inner is ISimulatedDatabase { Offline: true };
        set
        {
            if (_inner is ISimulatedDatabase s) s.Offline = value;
        }
    }

    public bool CanSimulateOutage => _inner is ISimulatedDatabase;

    public Task StoreAsync(ResultRun run, CancellationToken cancellationToken)
        => (_inner ?? throw new IOException("Keine Datenbank konfiguriert.")).StoreAsync(run, cancellationToken);
}

/// <summary>Austauschbarer Meldeweg; Fehler des Ziels werden verschluckt (best effort).</summary>
public sealed class SwitchableNotifier : IRunNotifier
{
    private volatile IRunNotifier? _inner;

    public IRunNotifier? Inner
    {
        get => _inner;
        set => _inner = value;
    }

    public void Notify(RunNotification notification)
    {
        try { _inner?.Notify(notification); }
        catch (Exception) { /* best effort */ }
    }
}

/// <summary>Auswahl gespeicherter Läufe für die Auswertung (Zeitraum über den Startzeitpunkt).</summary>
public sealed record RunQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Test = null, string? Station = null,
    string? DutId = null, bool IncludeDevelopment = true, int Limit = 20000);

/// <summary>Datenbank, aus der Läufe für Auswertung und Berichte gelesen werden können (ohne Kurven).</summary>
public interface IRunQuerySource
{
    string Name { get; }

    Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken);

    /// <summary>Einen Lauf vollständig lesen – mit Kurven (für den Prüfbericht/Reader aus der Datenbank). null = nicht vorhanden.</summary>
    Task<ResultRun?> LoadRunAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<ResultRun?>(null);
}

public static class RunQueries
{
    public static bool Matches(this RunQuery q, ResultRun r)
        => (q.From is null || r.Started >= q.From) && (q.To is null || r.Started <= q.To)
           && (string.IsNullOrWhiteSpace(q.Test) || r.TestName == q.Test) && (string.IsNullOrWhiteSpace(q.Station) || r.Station == q.Station)
           && (string.IsNullOrWhiteSpace(q.DutId) || r.DutId == q.DutId) && (q.IncludeDevelopment || !r.IsDevelopmentRun);
}
