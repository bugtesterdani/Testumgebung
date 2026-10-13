using System.Data.Common;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Results;
using Xunit;

namespace Testumgebung.Engine.Tests;

/// <summary>
/// Etappe 6: Datenbankadapter. SQLite läuft immer (native Bibliothek); SQL Server, PostgreSQL und MySQL laufen,
/// wenn die NuGet-Treiber verfügbar sind und ein Verbindungsstring gesetzt ist:
/// TESTUMGEBUNG_TEST_MSSQL, TESTUMGEBUNG_TEST_POSTGRES, TESTUMGEBUNG_TEST_MYSQL.
/// </summary>
public sealed class DatabaseAdapterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tu-db-" + Guid.NewGuid().ToString("N")[..8]);

    public DatabaseAdapterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private SqlRunDatabase Sqlite(string name = "pruef.db")
        => new(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(_dir, name)}", new SqliteDialect());

    private static bool NoSqlite() => !NativeSqliteFactory.IsAvailable;

    [SqliteFact]
    public async Task SQLite_Vertragspruefung_Erfolg_Idempotenz_Antwortverlust_Kurven()
    {

        var result = await DatabaseContractCheck.RunAsync(Sqlite()).Within();
        Assert.True(result.Passed, string.Join("\n", result.Steps.Select(s => $"{s.Name}: {s.Passed} {s.Detail}")));
        Assert.Equal(["Verbindung und Schema", "Run speichern", "Idempotente Wiederholung", "Antwort nach Commit verloren", "Kurven verlustfrei", "Testdaten entfernen"],
            result.Steps.Select(s => s.Name));
        Assert.Contains("0 Run(s)", await Sqlite().TestConnectionAsync(default));   // Testdaten wieder entfernt
    }

    [SqliteFact]
    public async Task SQLite_Schema_wird_einmal_migriert_und_Version_gefuehrt()
    {

        var text = await Sqlite().TestConnectionAsync(default);
        Assert.Contains("Schema v3", text);
        Assert.Contains("0 Run(s)", text);

        await using var connection = NativeSqliteFactory.Instance.CreateConnection()!;
        connection.ConnectionString = $"Data Source={Path.Combine(_dir, "pruef.db")}";
        await connection.OpenAsync();
        Assert.Equal(0, await new SchemaMigrator(new SqliteDialect()).MigrateAsync(connection, default)); // nichts mehr zu tun
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tu_schema_version";
        Assert.Equal(3L, await cmd.ExecuteScalarAsync());                    // v1 Messdaten, v2 Releases
    }

    [SqliteFact]
    public async Task SQLite_echter_Pruefling_mit_Kurven_Messwerten_Werkereingaben()
    {

        var run = await RecordSample();
        var db = Sqlite();
        await db.StoreAsync(run, default);
        var c = await db.CountAsync(run.RunId);
        Assert.Equal(1, c.Runs);
        Assert.Equal(run.Events.Count(e => e.Kind is not (ResultEventKind.OperatorAnswer or ResultEventKind.Development)), c.Steps);
        Assert.Equal(run.Events.Count(e => e.Kind == ResultEventKind.OperatorAnswer), c.OperatorInputs);
        Assert.Equal(3, c.WaveformChunks);                                  // L1–L3, je ein Block
        Assert.Equal(run.Waveforms[1].Data.Samples, await db.LoadSamplesAsync(run.RunId, 2));

        // Typisierte Ablage abfragbar
        await using var connection = NativeSqliteFactory.Instance.CreateConnection()!;
        connection.ConnectionString = $"Data Source={Path.Combine(_dir, "pruef.db")}";
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value_double, unit, low_limit, high_limit, rating FROM tu_measurements WHERE run_id = @r AND node_id = 'spannung'";
        var p = cmd.CreateParameter();
        p.ParameterName = "@r";
        p.Value = run.RunId;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.InRange(reader.GetDouble(0), 220, 240);
        Assert.Equal(("V", 220.0, 240.0, "PASS"), (reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetString(4)));
    }

    [SqliteFact]
    public async Task P02_P04_P05_Outbox_mit_echter_Datenbank_Ausfall_Neustart_Antwortverlust()
    {

        var run = await RecordSample();
        var outboxFolder = Path.Combine(_dir, "outbox");
        var notifier = new List<RunNotification>();

        // Ausfall: Datenbankdatei liegt in einem nicht vorhandenen Ordner
        var broken = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(_dir, "fehlt", "x.db")}", new SqliteDialect());
        var first = new OutboxService(outboxFolder, broken, notifier: new ListNotifier(notifier));
        var r = await first.SubmitAsync(run);
        Assert.False(r.Stored);
        Assert.Contains("nicht zu öffnen", r.Error);
        await first.DisposeAsync();

        // Neustart mit funktionierender DB, Antwort nach Commit geht einmal verloren
        var db = Sqlite();
        var lose = 1;
        db.AfterCommit = _ => { if (lose-- > 0) throw new TimeoutException("Antwort verloren"); };
        var second = new OutboxService(outboxFolder, db, notifier: new ListNotifier(notifier));
        await second.RetryNowAsync();                                      // lädt Outbox, Commit erfolgt, Antwort verloren
        Assert.Single(second.Pending);
        await second.RetryNowAsync();                                      // Wiederholung → bereits vorhanden → Erfolg
        Assert.Empty(second.Pending);
        Assert.Equal(1, (await db.CountAsync(run.RunId)).Runs);
        Assert.Equal([RunNotificationKind.StoreFailed, RunNotificationKind.RetryFailed, RunNotificationKind.StoredLater], notifier.Select(n => n.Kind));
    }

    [Fact]
    public void Verbindungsziel_wird_ohne_Passwort_angezeigt()
    {
        var db = new SqlRunDatabase(NativeSqliteFactory.Instance, "Server=sql01;Database=Pruefung;User Id=tu;Password=geheim", new SqlServerDialect());
        Assert.Equal("Microsoft SQL Server (sql01/Pruefung)", db.Name);
        Assert.DoesNotContain("geheim", db.Name);
    }

    [Fact]
    public async Task Alle_Dialekte_erzeugen_vollstaendige_Skripte_und_typisierte_Parameter()
    {
        foreach (var provider in Enum.GetValues<DatabaseProvider>())
        {
            var rec = new RecordingFactory();
            var db = new SqlRunDatabase(rec, "Data Source=x", SqlDialect.For(provider));
            await db.StoreAsync(DatabaseContractCheck.SampleRun("R1"), default);
            var sql = string.Join("\n", rec.Log.Select(l => l.Sql));
            foreach (var table in SqlDialect.RunTables.Append("tu_definitions"))
                Assert.Contains("CREATE TABLE " + table, sql);
            Assert.Equal(2, rec.Log.Count(l => l.Sql.Contains("INSERT INTO tu_waveform_chunks")));
            Assert.All(rec.Log.Where(l => l.Sql.Contains("INSERT")), l =>
                Assert.All(System.Text.RegularExpressions.Regex.Matches(l.Sql, "@([A-Za-z]+)").Select(m => m.Groups[1].Value),
                    name => Assert.True(l.Parameters.ContainsKey(name), $"{provider}: Parameter @{name} fehlt in {l.Sql}")));
        }
        Assert.Contains("IF OBJECT_ID", new SqlServerDialect().CreateVersionTable);
        Assert.Contains("ENGINE=InnoDB", string.Join("", new MySqlDialect().Migrations[0].Statements));
    }

    /// <summary>Mit TU_SQL_DUMP=Pfad: erzeugte Anweisungen samt Parametertypen als JSON-Zeilen (Wiedergabe gegen echte Server).</summary>
    [EnvironmentFact("TU_SQL_DUMP")]
    public async Task Anweisungen_fuer_Serverpruefung_exportieren()
    {
        var target = Environment.GetEnvironmentVariable("TU_SQL_DUMP");
        Assert.False(string.IsNullOrWhiteSpace(target));
        Directory.CreateDirectory(target);
        var run = await RecordSample();
        foreach (var provider in Enum.GetValues<DatabaseProvider>())
        {
            var rec = new RecordingFactory();
            var db = new SqlRunDatabase(rec, "Data Source=x", SqlDialect.For(provider));
            await db.StoreAsync(DatabaseContractCheck.SampleRun("VERTRAG-1"), default);
            await db.StoreAsync(run, default);
            await db.CountAsync(run.RunId);
            await db.LoadSamplesAsync(run.RunId, 1);
            await db.DeleteRunAsync("VERTRAG-1");
            // Etappe 7: Release-Ablage
            rec.Scalars = sql => sql.Contains("SELECT project_name", StringComparison.Ordinal) ? "AC-Prüfung" : 0L;
            var releases = new Testumgebung.Persistence.Releases.ReleaseService(new SqlReleaseStore(db));
            var project = SampleProjects.AcPruefung();
            var draft = await releases.CreateDraftAsync(project, "2.0", new Testumgebung.Persistence.Releases.Actor("m.weber@firma.example", UserRights.Develop | UserRights.Release));
            await releases.Store.TransitionAsync(new Testumgebung.Persistence.Releases.ReleaseTransition(draft.ReleaseId,
                Testumgebung.Persistence.Releases.ReleaseState.Draft, Testumgebung.Persistence.Releases.ReleaseState.Review, "m.weber@firma.example", DateTimeOffset.Now, "OK"),
                new Testumgebung.Persistence.Releases.AuditDraft(DateTimeOffset.Now, "m.weber@firma.example", "Entwicklungsprüfung abgeschlossen", "ok", draft.ReleaseId));
            await releases.Store.TransitionAsync(new Testumgebung.Persistence.Releases.ReleaseTransition(draft.ReleaseId,
                Testumgebung.Persistence.Releases.ReleaseState.Review, Testumgebung.Persistence.Releases.ReleaseState.Released, "qs@firma.example", DateTimeOffset.Now, ArchiveOtherReleased: true),
                new Testumgebung.Persistence.Releases.AuditDraft(DateTimeOffset.Now, "qs@firma.example", "Version freigegeben", "v2.0", draft.ReleaseId));
            await releases.Store.ListAsync();
            await releases.Store.ReadAuditAsync();
            var lines = rec.Log.Select((l, i) => System.Text.Json.JsonSerializer.Serialize(new
            {
                sql = l.Sql,
                p = l.Parameters.ToDictionary(kv => kv.Key, kv => new
                {
                    t = rec.Types[i][kv.Key].ToString(),
                    v = kv.Value is byte[] b ? (object?)Convert.ToBase64String(b) : kv.Value,
                }),
            }));
            await File.WriteAllLinesAsync(Path.Combine(target, provider + ".jsonl"), lines);
        }
    }

    [Fact]
    public void Referenzskripte_in_docs_sind_aktuell()
    {
        var docs = FindDocs();
        Assert.NotNull(docs);
        foreach (var provider in Enum.GetValues<DatabaseProvider>())
        {
            var dialect = SqlDialect.For(provider);
            var path = Path.Combine(docs, "sql", $"schema-{provider.ToString().ToLowerInvariant()}.sql");
            Assert.True(File.Exists(path), $"{path} fehlt");
            Assert.Equal(SchemaScript.For(dialect).ReplaceLineEndings(), File.ReadAllText(path).ReplaceLineEndings());
        }
    }

    // ------------------------------------------------------------------ Echte Server (nur mit Treibern und Verbindungsstring)

#if DB_PROVIDERS
    [EnvironmentFact("TESTUMGEBUNG_TEST_MSSQL")]
    public Task SqlServer_Vertragspruefung()
    {
        AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);
        return RunProvider("TESTUMGEBUNG_TEST_MSSQL", Microsoft.Data.SqlClient.SqlClientFactory.Instance, new SqlServerDialect());
    }

    [EnvironmentFact("TESTUMGEBUNG_TEST_POSTGRES")]
    public Task PostgreSql_Vertragspruefung() => RunProvider("TESTUMGEBUNG_TEST_POSTGRES", Npgsql.NpgsqlFactory.Instance, new PostgreSqlDialect());

    [EnvironmentFact("TESTUMGEBUNG_TEST_MYSQL")]
    public Task MySql_Vertragspruefung() => RunProvider("TESTUMGEBUNG_TEST_MYSQL", MySqlConnector.MySqlConnectorFactory.Instance, new MySqlDialect());

    [Fact]
    public Task Sqlite_MicrosoftDataSqlite_Vertragspruefung()
        => RunProvider(null, Microsoft.Data.Sqlite.SqliteFactory.Instance, new SqliteDialect(), $"Data Source={Path.Combine(_dir, "ms.db")}");

    private static async Task RunProvider(string? env, DbProviderFactory factory, SqlDialect dialect, string? fixedConnection = null)
    {
        var cs = fixedConnection ?? Environment.GetEnvironmentVariable(env!);
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Integrationstest ohne Verbindungsstring gestartet.");
        var db = new SqlRunDatabase(factory, cs, dialect);
        var result = await DatabaseContractCheck.RunAsync(db).WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(result.Passed, string.Join("\n", result.Steps.Select(s => $"{s.Name}: {s.Passed} {s.Detail}")));
        var run = await RecordSample();
        await db.StoreAsync(run, default);
        await db.StoreAsync(run, default);
        Assert.Equal(1, (await db.CountAsync(run.RunId)).Runs);
        await db.DeleteRunAsync(run.RunId);
    }
#endif

    // ------------------------------------------------------------------ Hilfen

    private static async Task<ResultRun> RecordSample()
    {
        var project = SampleProjects.AcPruefung();
        var (_, registry) = SampleProjects.CreateSimulation(project);
        var op = new ScriptedOperator()
            .Answer("Kennung erfassen", _ => "SN-DB-" + Guid.NewGuid().ToString("N")[..6]).Answer("Sichtprüfung", _ => "i. O.")
            .Answer("Gehäusetemperatur erfassen", _ => "22").Answer("Prüfling anschließen", _ => true);
        var run = new TestRun(project.Definition, new TestRunOptions { Clock = new VirtualClock(), Hardware = registry, Operator = op });
        var recorder = new ResultRecorder(run);
        var tcs = new TaskCompletionSource<ResultRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RunRecorded += r => tcs.TrySetResult(r);
        var task = run.RunAsync();
        var result = await tcs.Task.Within();
        run.RequestAbort();
        await task.Within();
        return result;
    }

    private static string? FindDocs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Testumgebung.sln"))) dir = dir.Parent;
        return dir is null ? null : Path.Combine(dir.FullName, "docs");
    }

    private sealed class ListNotifier(List<RunNotification> list) : IRunNotifier
    {
        public void Notify(RunNotification notification)
        {
            lock (list) list.Add(notification);
        }
    }

    [SqliteFact]
    public async Task SQLite_Auswertung_liest_Laeufe_mit_Schritten_Messwerten_und_Eingaben()
    {

        var db = Sqlite("auswertung.db");
        var r1 = DatabaseContractCheck.SampleRun("A1");
        await db.StoreAsync(r1, default).Within();
        await db.StoreAsync(new ResultRun
        {
            RunId = "A2", Station = "P2", DutId = "SN-2", TestName = "Vertragstest", TestVersion = "1.0",
            Started = r1.Started.AddHours(1), Ended = r1.Started.AddHours(1).AddSeconds(2), Rating = ResultRating.Fail,
            IsDevelopmentRun = true, Structure = r1.Structure, Events =
            [
                new ResultEvent(1, ResultEventKind.Step, "m", "Prüfling > Messung", 1, r1.Started.AddHours(1), ResultRating.Fail, 260.0, "V", 220, 240),
                new ResultEvent(2, ResultEventKind.OperatorAnswer, "e", "", 1, r1.Started.AddHours(1), Value: "SN-2"),
            ],
        }, default).Within();

        var all = await ((IRunQuerySource)db).QueryRunsAsync(new RunQuery(), default).Within();
        Assert.Equal(["A1", "A2"], all.Select(r => r.RunId).OrderBy(x => x).ToArray());
        var a1 = all.Single(r => r.RunId == "A1");
        Assert.Equal("SN-VERTRAG", a1.DutId);
        Assert.Equal(ResultRating.Pass, a1.Rating);
        // Struktur, Messwert und Werkereingabe kamen mit
        Assert.Contains(a1.Structure, n => n.Id == "m" && n.BlockType == "analog.messen");
        var measure = a1.Events.First(e => e.NodeId == "m" && e.Kind == ResultEventKind.Step);
        Assert.Equal(230.123456789, (double)measure.Value!, 6);
        Assert.Equal((220.0, 240.0), (measure.LowLimit, measure.HighLimit));
        Assert.Contains(a1.Events, e => e.Kind == ResultEventKind.OperatorAnswer && (string?)e.Value == "SN-VERTRAG");
        Assert.DoesNotContain(a1.Waveforms, _ => true);   // Kurven werden in der Auswertung nicht geladen

        // Serverseitige Filter: Entwicklungsläufe aus, Zeitraum, Prüfplatz
        var prod = await ((IRunQuerySource)db).QueryRunsAsync(new RunQuery(IncludeDevelopment: false), default).Within();
        Assert.Equal(["A1"], prod.Select(r => r.RunId));
        var byStation = await ((IRunQuerySource)db).QueryRunsAsync(new RunQuery(Station: "P2"), default).Within();
        Assert.Equal(["A2"], byStation.Select(r => r.RunId));
        var byTime = await ((IRunQuerySource)db).QueryRunsAsync(new RunQuery(From: r1.Started.AddMinutes(30)), default).Within();
        Assert.Equal(["A2"], byTime.Select(r => r.RunId));

        // Auswertung rechnet mit den gelesenen Läufen (eine FAIL, eine PASS)
        var stats = Testumgebung.Persistence.Reports.ResultAnalysis.Compute(all, new Testumgebung.Persistence.Reports.StatisticsFilter { IncludeDevelopment = true });
        Assert.Equal(2, stats.Runs);
        Assert.Equal(1, stats.Fail);
        File.Delete(Path.Combine(_dir, "auswertung.db"));
    }

    [SqliteFact]
    public async Task SQLite_als_IRunQuerySource_ueber_die_austauschbare_Datenbank()
    {

        var switchable = new SwitchableRunDatabase { Inner = Sqlite("switch.db") };
        Assert.True(switchable.CanQuery);
        await switchable.StoreAsync(DatabaseContractCheck.SampleRun("S1"), default).Within();
        var runs = await switchable.QueryRunsAsync(new RunQuery(), default).Within();
        Assert.Single(runs);
        Assert.Equal("S1", runs[0].RunId);

        var empty = new SwitchableRunDatabase();
        Assert.False(empty.CanQuery);
        await Assert.ThrowsAsync<IOException>(() => empty.QueryRunsAsync(new RunQuery(), default));
    }


    [SqliteFact]
    public async Task SQLite_LoadRunAsync_liefert_Lauf_mit_Kurven()
    {

        var db = Sqlite("kurven.db");
        await db.StoreAsync(DatabaseContractCheck.SampleRun("K1"), default).Within();

        var run = await ((IRunQuerySource)db).LoadRunAsync("K1", default).Within();
        Assert.NotNull(run);
        Assert.Equal("K1", run!.RunId);
        var wave = Assert.Single(run.Waveforms);
        Assert.Equal("Spannung_L1", wave.Data.Channel);
        Assert.Equal(SqlRunDatabase.SamplesPerChunk + 1000, wave.Data.Count);     // über mehrere Chunks zusammengesetzt
        Assert.Equal(Math.Sin(5 * 0.01) * 325.27 + 1e-9 * 5, wave.Data.Samples[5], 1e-6);
        Assert.Equal("Vertragstest", wave.Data.Quality);

        // Reader-Dokument aus dem DB-Lauf zeigt die Kurve
        var doc = new Testumgebung.Presentation.ViewModels.ResultDocumentViewModel(run);
        Assert.True(doc.HasWaveforms);
        Assert.Null(await ((IRunQuerySource)db).LoadRunAsync("gibtsnicht", default).Within());
        File.Delete(Path.Combine(_dir, "kurven.db"));
    }

}
