using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Storage;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence.Results;
using Xunit;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Pilot – Zwischenaufzeichnung und Wiederherstellung unvollständiger Prüfläufe (Absturz, Stromausfall): Journal je
/// Prüflingstest, nach dem Speichern gelöscht; nach Neustart als abgebrochen (ERROR) speichern oder verwerfen.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-rec-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Dir(string name) => Path.Combine(_dir, name);

    /// <summary>Prüfling mit Kennung, Messung, geheimem Schritt (nicht in die Datei) und einem Schritt, der hängt.</summary>
    private static (TestDefinition Definition, TaskCompletionSource Gate) Hanging()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secret = Steps.SetValue("geheim", "Notiz", "\"intern\"");
        secret.Storage = secret.Storage.With(StorageTarget.File, false);
        var dut = new GroupDefinition("dut", "Prüfling") { IsDutRun = true }
            .Var("Kennung", VarType.String).Var("Notiz", VarType.String)
            .Add(Steps.SetValue("id", "Kennung", "\"SN-CRASH\""),
                 Steps.Code("mess", "Messung", _ => new StepOutcome(ResultRating.Pass, 230.5, "V", LowLimit: 220, HighLimit: 240)),
                 secret,
                 Steps.Code("haengt", "Hängt", async _ => { await gate.Task; return StepOutcome.Unrated(null); }));
        return (Test(Group("loop").Add(dut)), gate);
    }

    [Fact]
    public async Task Absturz_hinterlaesst_Journal_Neustart_speichert_als_abgebrochen()
    {
        var (definition, gate) = Hanging();
        var journal = new RunJournal(Dir("journal"));
        var run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock(), StationName = "Prüfplatz 03" });
        _ = new ResultRecorder(run, journal, new StorageResolver(definition));
        var task = run.RunAsync();
        await run.WaitFor<StepStarted>(s => s.NodeId == "haengt").Within();

        // Laufend: Journal existiert, gehört aber dem aktiven Schreiber → nicht „unvollständig“
        Assert.Single(Directory.GetFiles(Dir("journal"), "*" + RunJournal.Extension));
        Assert.Empty(journal.FindIncomplete());

        // „Neustart“: neue Instanz sieht das Journal als unvollständig (Kennung aus dem Kennungsschritt)
        var restarted = new RunJournal(Dir("journal"));
        var incomplete = Assert.Single(restarted.FindIncomplete());
        Assert.Equal("SN-CRASH", incomplete.DutId);
        Assert.Equal("Prüfplatz 03", incomplete.Header.Station);
        Assert.Contains(incomplete.Events, e => e.NodeId == "mess" && e.Value is double v && v == 230.5 && e.Unit == "V" && e.HighLimit == 240);
        Assert.Contains("geheim", incomplete.Header.ExcludedFromFile);
        Assert.False(incomplete.Damaged);

        var files = new ResultFileStore(Dir("ergebnisse"));
        var pipeline = new ResultPipeline(files, null) { Journal = restarted };
        var report = await pipeline.RecoverAsync(incomplete, DateTimeOffset.Now, "admin").Within();
        Assert.True(report.File!.Success, report.File.Error);
        var saved = ResultFile.Read(report.File.Path!).Run;
        Assert.Equal(ResultRating.Error, saved.Rating);
        Assert.Equal("SN-CRASH", saved.DutId);
        Assert.Contains(saved.Events, e => e.NodeId == "mess");
        Assert.DoesNotContain(saved.Events, e => e.NodeId == "geheim");
        Assert.Contains(saved.Events, e => e.Kind == ResultEventKind.Diagnostic && e.Message!.Contains("Unvollständiger Prüflingstest"));
        Assert.Empty(restarted.FindIncomplete());

        gate.SetResult();
        await task.Within();
    }

    [Fact]
    public async Task Regulaerer_Abschluss_entfernt_das_Journal()
    {
        var (definition, gate) = Hanging();
        gate.SetResult();
        var journal = new RunJournal(Dir("journal"));
        var pipeline = new ResultPipeline(new ResultFileStore(Dir("ergebnisse")), null) { Journal = journal };
        var run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock() });
        var resolver = new StorageResolver(definition);
        var recorder = new ResultRecorder(run, journal, resolver);
        var stored = new TaskCompletionSource<StorageReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RunRecorded += r => _ = pipeline.ProcessAsync(r, resolver).ContinueWith(t => stored.SetResult(t.Result), TaskScheduler.Default);
        await run.RunAsync().Within();
        Assert.True((await stored.Task.Within()).File!.Success);
        Assert.Empty(Directory.GetFiles(Dir("journal"), "*" + RunJournal.Extension));
        Assert.Empty(new RunJournal(Dir("journal")).FindIncomplete());
    }

    [Fact]
    public async Task Abgeschnittene_letzte_Zeile_und_Verwerfen()
    {
        var (definition, gate) = Hanging();
        var journal = new RunJournal(Dir("journal"));
        var run = new TestRun(definition, new TestRunOptions { Clock = new VirtualClock() });
        _ = new ResultRecorder(run, journal);
        var task = run.RunAsync();
        await run.WaitFor<StepStarted>(s => s.NodeId == "haengt").Within();
        var path = Directory.GetFiles(Dir("journal"), "*" + RunJournal.Extension).Single();
        gate.SetResult();
        await task.Within();

        // Stromausfall mitten im Schreiben: Ersatzdatei mit abgeschnittener Zeile
        var copy = Path.Combine(Dir("journal"), "kaputt" + RunJournal.Extension);
        var text = File.ReadAllText(path);
        File.WriteAllText(copy, text + "E {\"s\":99,\"k\":\"Ste");
        File.Delete(path);
        var restarted = new RunJournal(Dir("journal"));
        var incomplete = Assert.Single(restarted.FindIncomplete());
        Assert.True(incomplete.Damaged);
        Assert.Contains("unvollständig", incomplete.Summary);
        Assert.Contains(incomplete.Events, e => e.NodeId == "mess");

        restarted.Discard(incomplete);
        Assert.Empty(restarted.FindIncomplete());
        Assert.Single(Directory.GetFiles(Path.Combine(Dir("journal"), RunJournal.DiscardedFolderName)));
    }

    [Fact]
    public void Wiederherstellungsordner_zurueckkopieren_nachdem_der_Ergebnisordner_wieder_da_ist()
    {
        var results = Dir("Ergebnisse");
        var recovery = Dir("Wiederherstellung");
        var store = new ResultFileStore(results, recovery);
        var run = new ResultRun { RunId = "WH-1", DutId = "SN-1", TestName = "T", Started = DateTimeOffset.Now, Ended = DateTimeOffset.Now, Rating = ResultRating.Pass };

        // Ergebnisordner als Datei blockieren → Speichern landet im Wiederherstellungsordner
        Directory.CreateDirectory(_dir);
        File.WriteAllText(results, "blockiert");
        var saved = store.Save(run);
        Assert.True(saved.Recovered);
        Assert.NotNull(saved.Path);
        Assert.StartsWith(recovery, saved.Path!);
        Assert.Single(store.PendingRecovery());

        // Hindernis entfernen und zurückkopieren
        File.Delete(results);
        var (restored, problems) = store.RestoreRecovered();
        Assert.Equal(1, restored);
        Assert.Empty(problems);
        Assert.Empty(store.PendingRecovery());
        Assert.Single(store.List());
        Assert.Equal("WH-1", ResultFile.Read(store.List()[0].Path).Run.RunId);

        // Nochmals ausführen ist idempotent (gleiche Datei) und meldet nichts
        Assert.Equal((0, 0), (store.RestoreRecovered().Restored, store.RestoreRecovered().Problems.Count));
    }

    [Fact]
    public void Beschaedigte_Datei_bleibt_im_Wiederherstellungsordner()
    {
        var results = Dir("Erg2");
        var recovery = Dir("Wdh2");
        Directory.CreateDirectory(recovery);
        File.WriteAllBytes(Path.Combine(recovery, "kaputt" + ResultFile.Extension), [1, 2, 3, 4]);
        var store = new ResultFileStore(results, recovery);
        var (restored, problems) = store.RestoreRecovered();
        Assert.Equal(0, restored);
        Assert.Single(problems);
        Assert.Single(store.PendingRecovery());   // bleibt liegen
    }

}
