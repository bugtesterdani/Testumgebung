using Testumgebung.Domain.Storage;

namespace Testumgebung.Persistence.Results;

/// <summary>Ergebnis der Speicherung eines Prüflingstests (unabhängig von PASS/FAIL/ERROR).</summary>
/// <param name="File">null = Dateispeicherung nicht konfiguriert.</param>
/// <param name="Database">null = keine Datenbank konfiguriert.</param>
public sealed record StorageReport(string RunId, string DutId, FileSaveResult? File, OutboxSubmitResult? Database)
{
    public bool DatabaseFailed => Database is { Stored: false };
    public bool FileFailed => File is { Success: false };
}

/// <summary>
/// Am Ende jedes Prüflingstests: Filter je Ziel anwenden, Ergebnisdatei schreiben (auch bei WERROR) und die für die
/// Datenbank gewählten Daten über die Outbox übertragen. Läuft außerhalb des Engine-Threads.
/// </summary>
public sealed class ResultPipeline(ResultFileStore? files, OutboxService? outbox)
{
    /// <summary>Zielordner der Ergebnisdateien – zur Laufzeit änderbar.</summary>
    public ResultFileStore? Files { get; set; } = files;
    public OutboxService? Outbox { get; } = outbox;

    /// <summary>Zwischenaufzeichnung laufender Prüflingstests; null = ohne Absturzsicherung.</summary>
    public RunJournal? Journal { get; set; }

    /// <summary>false = keine Datenbank konfiguriert: keine Übertragung, keine Warnung (Outbox bleibt für Altbestände aktiv).</summary>
    public bool DatabaseEnabled { get; set; } = true;

    public async Task<StorageReport> ProcessAsync(ResultRun recorded, StorageResolver resolver)
    {
        FileSaveResult? file = null;
        if (Files is not null)
        {
            var forFile = ResultFilter.ForFile(recorded, resolver);
            file = await Task.Run(() => Files.Save(forFile)).ConfigureAwait(false);
        }

        OutboxSubmitResult? db = null;
        if (Outbox is not null && DatabaseEnabled)
        {
            var forDb = ResultFilter.ForDatabase(recorded, resolver);
            try
            {
                db = await Outbox.SubmitAsync(forDb).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                db = new OutboxSubmitResult(false, $"Outbox nicht beschreibbar: {ex.Message}");
            }
        }
        var report = new StorageReport(recorded.RunId, recorded.DutId, file, db);
        // Gesichert (Datei, Wiederherstellungsordner oder Outbox) → Zwischenaufzeichnung nicht mehr nötig.
        if (file?.Path is not null || db is { Stored: true } || (file is null && db is null)) Journal?.Complete(recorded.RunId);
        return report;
    }

    /// <summary>Unvollständigen Lauf nach Neustart als abgebrochen (ERROR) speichern – Filter des ursprünglichen Ablaufs gelten.</summary>
    public async Task<StorageReport> RecoverAsync(IncompleteRun incomplete, DateTimeOffset now, string user)
    {
        var run = RunJournal.ToAbortedRun(incomplete, now, user);
        FileSaveResult? file = null;
        if (Files is not null)
        {
            var forFile = Exclude(run, incomplete.Header.ExcludedFromFile, ResultFilter.FileTarget);
            file = await Task.Run(() => Files.Save(forFile)).ConfigureAwait(false);
        }
        OutboxSubmitResult? db = null;
        if (Outbox is not null && DatabaseEnabled)
        {
            try
            {
                db = await Outbox.SubmitAsync(Exclude(run, incomplete.Header.ExcludedFromDatabase, ResultFilter.DatabaseTarget)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                db = new OutboxSubmitResult(false, $"Outbox nicht beschreibbar: {ex.Message}");
            }
        }
        if (file?.Path is not null || db is { Stored: true } || (file is null && db is null))
        {
            try
            {
                File.Delete(incomplete.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // bleibt sichtbar
            }
        }
        return new StorageReport(run.RunId, run.DutId, file, db);
    }

    private static ResultRun Exclude(ResultRun run, IReadOnlyCollection<string> excluded, string label)
    {
        var set = excluded.ToHashSet(StringComparer.Ordinal);
        return run.CopyWith(label, run.Events.Where(e => e.NodeId is null || !set.Contains(e.NodeId)).ToList(), [], []);
    }
}
