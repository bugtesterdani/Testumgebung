using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class SqlQueryBoundaryTests
{
    [SqliteFact]
    public async Task Server_filters_limits_and_typed_events_preserve_expected_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-sql-query-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var db = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(directory, "runs.db")}", new SqliteDialect());
            var time = DateTimeOffset.UnixEpoch.AddDays(1);
            for (var i = 0; i < 3; i++)
            {
                await db.StoreAsync(new ResultRun
                {
                    RunId = $"R{i}",
                    Station = i == 0 ? "P1" : "P2",
                    DutId = $"SN{i}",
                    Order = "FA1",
                    TestName = i == 0 ? "Test A" : "Test B",
                    TestVersion = "1",
                    Started = time.AddHours(i),
                    Ended = time.AddHours(i).AddSeconds(1),
                    IsDevelopmentRun = i == 2,
                    Rating = i == 0 ? ResultRating.Pass : ResultRating.Fail,
                    Events =
                    [
                        new ResultEvent(1, ResultEventKind.Step, "bool", "Bool", 1, time, ResultRating.Pass, true),
                        new ResultEvent(2, ResultEventKind.Step, "integer", "Integer", 1, time, ResultRating.NResult, 42L),
                        new ResultEvent(3, ResultEventKind.Step, "text", "Text", 1, time, ResultRating.Fail, "text", Message: "failure"),
                        new ResultEvent(4, ResultEventKind.Step, "none", "None", 1, time, ResultRating.Error),
                        new ResultEvent(5, ResultEventKind.Group, "group", "Group", 1, time, ResultRating.Pass),
                    ]
                }, default);
            }
            async Task Check(RunQuery query, params string[] expected)
                => Assert.Equal(expected, (await db.QueryRunsAsync(query, default)).Select(r => r.RunId));
            await Check(new RunQuery(), "R0", "R1", "R2");
            await Check(new RunQuery(From: time.AddHours(1), To: time.AddHours(1)), "R1");
            await Check(new RunQuery(To: time), "R0");
            await Check(new RunQuery(Test: "Test B", Station: "P2", DutId: "SN1"), "R1");
            await Check(new RunQuery(Test: "missing")); await Check(new RunQuery(Station: "missing")); await Check(new RunQuery(DutId: "missing"));
            await Check(new RunQuery(IncludeDevelopment: false), "R0", "R1");
            await Check(new RunQuery(Limit: 1), "R0"); await Check(new RunQuery(Limit: 0));
            var result = Assert.Single(await db.QueryRunsAsync(new RunQuery(Limit: 1), default));
            Assert.Equal("FA1", result.Order); Assert.Empty(result.Structure); Assert.Equal(5, result.Events.Count);
            Assert.Equal(true, result.Events.Single(e => e.NodeId == "bool").Value);
            Assert.Equal(42L, result.Events.Single(e => e.NodeId == "integer").Value);
            Assert.Equal("text", result.Events.Single(e => e.NodeId == "text").Value);
            Assert.Null(result.Events.Single(e => e.NodeId == "none").Value);
            Assert.Equal("failure", result.Events.Single(e => e.NodeId == "text").Message);
        }
        finally { Directory.Delete(directory, true); }
    }
}
