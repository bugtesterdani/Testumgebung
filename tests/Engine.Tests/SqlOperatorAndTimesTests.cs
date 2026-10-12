using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class SqlOperatorAndTimesTests
{
    [SqliteFact]
    public async Task Typed_operator_inputs_and_chunked_explicit_times_round_trip_and_legacy_text_falls_back()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-sql-times-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory, "results.db")}";
            var db = new SqlRunDatabase(NativeSqliteFactory.Instance, connectionString, new SqliteDialect());
            var time = DateTimeOffset.UnixEpoch.AddDays(1); var values = new object?[] { 1.5, 42L, true, false, "text", null };
            var samples = Enumerable.Range(0, 65537).Select(i => i * .5).ToArray(); var times = samples.Select((_, i) => i * .01).ToArray();
            await db.StoreAsync(new ResultRun
            {
                RunId = "run",
                Started = time,
                Ended = time.AddSeconds(1),
                Events = values.Select((value, i) => new ResultEvent(i + 1, ResultEventKind.OperatorAnswer, "input-" + i, "Input", 1, time, Value: value)).ToList(),
                Waveforms = [new ResultWaveform("wave", 1, "node", new Waveform("a", "V", 100, time, samples, times))]
            }, default);
            var run = (await db.LoadRunAsync("run", default))!; Assert.Equal(values, run.Events.Select(e => e.Value));
            var wave = Assert.Single(run.Waveforms).Data; Assert.Equal(samples, wave.Samples); Assert.Equal(times, wave.Times);
            Assert.Null(await db.LoadRunAsync("missing", default));
            using var connection = NativeSqliteFactory.Instance.CreateConnection()!; connection.ConnectionString = connectionString; await connection.OpenAsync();
            async Task Update(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
            await Update("UPDATE tu_operator_inputs SET value_text='legacy' WHERE value_type IN ('int','double')");
            await Update("UPDATE tu_waveform_chunks SET times=NULL WHERE part=1");
            await Update("UPDATE tu_runs SET started_text='invalid',ended_text='invalid'");
            var legacy = (await db.LoadRunAsync("run", default))!;
            Assert.Equal(time, legacy.Started); Assert.Equal(time.AddSeconds(1), legacy.Ended);
            Assert.Equal(new object?[] { "legacy", "legacy", true, false, "text", null }, legacy.Events.Select(e => e.Value));
            Assert.Null(Assert.Single(legacy.Waveforms).Data.Times);
            // A limited query must also ignore operator-input rows from runs outside the selected result set.
            await db.StoreAsync(new ResultRun { RunId = "later", Started = time.AddHours(1), Ended = time.AddHours(1), Events = [new ResultEvent(1, ResultEventKind.OperatorAnswer, null, "Input", 1, time, Value: 42L)] }, default);
            var limited = Assert.Single(await db.QueryRunsAsync(new RunQuery(Limit: 1), default)); Assert.Equal("run", limited.RunId); Assert.Equal(6, limited.Events.Count);
        }
        finally { Directory.Delete(directory, true); }
    }
}
