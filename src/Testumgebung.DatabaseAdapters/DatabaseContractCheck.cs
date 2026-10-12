using System.Diagnostics;
using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.DatabaseAdapters;

public sealed record ContractCheckStep(string Name, bool Passed, string Detail);

public sealed record ContractCheckResult(IReadOnlyList<ContractCheckStep> Steps)
{
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Passed);
}

/// <summary>
/// Vertragsprüfung gegen die konfigurierte Datenbank – dieselben Nachweise wie die automatisierten Tests
/// (Etappe 6: Erfolg, idempotente Wiederholung, Antwortverlust nach Commit, verlustfreie Kurven).
/// Arbeitet mit eigenen Test-Run-IDs (<c>VERTRAG-…</c>) und entfernt diese am Ende wieder.
/// </summary>
public static class DatabaseContractCheck
{
    public static async Task<ContractCheckResult> RunAsync(SqlRunDatabase db, CancellationToken cancellationToken = default)
    {
        var steps = new List<ContractCheckStep>();
        var id = "VERTRAG-" + Guid.NewGuid().ToString("N")[..12];
        var lostId = id + "-B";
        var sw = Stopwatch.StartNew();

        async Task<bool> Step(string name, Func<Task<string>> action)
        {
            try
            {
                steps.Add(new ContractCheckStep(name, true, await action().ConfigureAwait(false)));
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                steps.Add(new ContractCheckStep(name, false, ex.GetType().Name + ": " + ex.Message));
                return false;
            }
        }

        if (!await Step("Verbindung und Schema", () => db.TestConnectionAsync(cancellationToken)).ConfigureAwait(false))
            return new ContractCheckResult(steps);

        var run = SampleRun(id);
        try
        {
            await Step("Run speichern", async () =>
            {
                sw.Restart();
                await db.StoreAsync(run, cancellationToken).ConfigureAwait(false);
                var c = await db.CountAsync(id, cancellationToken).ConfigureAwait(false);
                Expect(c == Expected, $"Zeilen {c} ≠ erwartet {Expected}");
                return $"{c.Steps} Schritte, {c.Measurements} Messwerte, {c.WaveformChunks} Kurvenblöcke in {sw.ElapsedMilliseconds} ms";
            }).ConfigureAwait(false);

            await Step("Idempotente Wiederholung", async () =>
            {
                await db.StoreAsync(run, cancellationToken).ConfigureAwait(false);
                var c = await db.CountAsync(id, cancellationToken).ConfigureAwait(false);
                Expect(c == Expected, $"Nach Wiederholung {c} – Duplikate!");
                return "Zweites Speichern erzeugt keine Duplikate.";
            }).ConfigureAwait(false);

            await Step("Antwort nach Commit verloren", async () =>
            {
                var lost = SampleRun(lostId);
                var previous = db.AfterCommit;
                db.AfterCommit = _ => throw new TimeoutException("Antwort verloren (simuliert)");
                try
                {
                    await db.StoreAsync(lost, cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("Simulierter Antwortverlust wurde nicht ausgelöst.");
                }
                catch (TimeoutException) { }
                finally
                {
                    db.AfterCommit = previous;
                }
                await db.StoreAsync(lost, cancellationToken).ConfigureAwait(false); // Wiederholung aus der Outbox
                var c = await db.CountAsync(lostId, cancellationToken).ConfigureAwait(false);
                Expect(c.Runs == 1 && c == Expected, $"Nach Wiederholung {c}");
                return "Commit erfolgt, Antwort verloren, Wiederholung ohne Duplikat.";
            }).ConfigureAwait(false);

            await Step("Kurven verlustfrei", async () =>
            {
                var samples = await db.LoadSamplesAsync(id, 1, cancellationToken).ConfigureAwait(false);
                Expect(samples.AsSpan().SequenceEqual(run.Waveforms[0].Data.Samples), "Samples weichen ab");
                return $"{samples.Length} Samples in {(samples.Length + SqlRunDatabase.SamplesPerChunk - 1) / SqlRunDatabase.SamplesPerChunk} Blöcken bitgenau.";
            }).ConfigureAwait(false);
        }
        finally
        {
            await Step("Testdaten entfernen", async () =>
            {
                await db.DeleteRunAsync(id, cancellationToken).ConfigureAwait(false);
                await db.DeleteRunAsync(lostId, cancellationToken).ConfigureAwait(false);
                return "Test-Runs gelöscht.";
            }).ConfigureAwait(false);
        }
        return new ContractCheckResult(steps);
    }

    private static readonly StoredRunCounts Expected = new(1, 4, 2, 2, 1, 1);

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Synthetischer Run mit allen Tabellen: Schritte, Messwert, Text, Werkereingabe, Entwicklereingriff, Kurve über zwei Blöcke.</summary>
    public static ResultRun SampleRun(string runId)
    {
        var t0 = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(2));
        var samples = Enumerable.Range(0, SqlRunDatabase.SamplesPerChunk + 1000).Select(i => Math.Sin(i * 0.01) * 325.27 + 1e-9 * i).ToArray();
        var wave = new Waveform("Spannung_L1", "V", 10000, t0, samples, Quality: "Vertragstest", SyncGroup: "s1");
        return new ResultRun
        {
            RunId = runId,
            StationRunId = "Vertragstest",
            Station = "Vertragstest",
            DutId = "SN-VERTRAG",
            TestName = "Vertragstest",
            TestVersion = "1.0",
            Started = t0,
            Ended = t0.AddSeconds(3),
            Rating = ResultRating.Pass,
            Structure =
            [
                new ResultNode("g", null, "Prüfling", "gruppe", null, null),
                new ResultNode("m", "g", "Messung", "schritt", "analog.messen", null),
                new ResultNode("e", "g", "Eingabe", "schritt", "werker", null),
                new ResultNode("k", "g", "Kurve", "schritt", "kurve.aufnehmen", null),
            ],
            Events =
            [
                new ResultEvent(1, ResultEventKind.Step, "m", "Prüfling > Messung", 1, t0.AddSeconds(1), ResultRating.Pass, 230.123456789, "V", 220, 240),
                new ResultEvent(2, ResultEventKind.OperatorShown, "e", "Prüfling > Eingabe", 1, t0.AddSeconds(1.5), Message: "Seriennummer eingeben"),
                new ResultEvent(3, ResultEventKind.OperatorAnswer, "e", "Prüfling > Eingabe", 1, t0.AddSeconds(2), Value: "SN-VERTRAG"),
                new ResultEvent(4, ResultEventKind.Step, "e", "Prüfling > Eingabe", 1, t0.AddSeconds(2), ResultRating.NResult, "SN-VERTRAG"),
                new ResultEvent(5, ResultEventKind.Step, "k", "Prüfling > Kurve", 1, t0.AddSeconds(2.5), ResultRating.Pass, Unit: "V", WaveformIds: ["k#5/Spannung_L1"]),
                new ResultEvent(6, ResultEventKind.Development, null, "", 0, t0.AddSeconds(2.6), Value: "Vertragstest", Message: "Variable: X = 1"),
            ],
            Waveforms = [new ResultWaveform("k#5/Spannung_L1", 5, "k", wave)],
        };
    }
}
