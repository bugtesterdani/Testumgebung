using System.Buffers.Binary;
using System.Text;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Reports;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class AbortOutcomeTests
{
    private static ResultRun Run(ResultRating rating, bool aborted, string id = "run") => new()
    {
        RunId = id, DutId = "SN-1", Rating = rating, OperatorAbort = aborted, Station = "station",
        Started = DateTimeOffset.UnixEpoch, Ended = DateTimeOffset.UnixEpoch.AddSeconds(1),
        Events = [new(1, ResultEventKind.Step, "step", "measurement", 1, DateTimeOffset.UnixEpoch, rating, 12d, "V")]
    };
    [Theory]
    [InlineData(ResultRating.NResult)] [InlineData(ResultRating.Pass)] [InlineData(ResultRating.Fail)] [InlineData(ResultRating.Error)]
    public void Aborted_outcome_is_explicit_and_roundtrips_without_erasing_step_ratings(ResultRating rating)
    {
        var run = Run(rating, true); Assert.Equal("ABORT", run.Status.ToString());
        using var stream = new MemoryStream(); ResultFile.Write(stream, run); var bytes = stream.ToArray();
        var stored = bytes.AsSpan(25, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(13))).ToArray();
        if (bytes[12] == 1)
        {
            using var compressed = new System.IO.Compression.DeflateStream(new MemoryStream(stored), System.IO.Compression.CompressionMode.Decompress);
            using var unpacked = new MemoryStream(); compressed.CopyTo(unpacked); stored = unpacked.ToArray();
        }
        var header = Assert.IsType<Dictionary<string, object?>>(MsgPackReader.Parse(stored)); Assert.Equal("ABORT", header["status"]);
        Assert.Contains("\"Outcome\":\"ABORT\"", System.Text.Json.JsonSerializer.Serialize(run.Status));
        var roundtrip = ResultFile.Read(bytes); Assert.True(roundtrip.IsIntact); Assert.Equal("ABORT", roundtrip.Run.Status.ToString());
        Assert.Equal(rating, roundtrip.Run.Rating); Assert.Equal(rating, Assert.Single(roundtrip.Run.Events).Rating);
        var document = new ResultDocumentViewModel(roundtrip.Run); Assert.Equal("ABORT", document.Outcome); Assert.Equal(ResultRating.Error, document.OutcomeRating);
        Assert.Contains("ABORT", ResultAnalysis.RunCsv(run)); Assert.Equal(rating.ToDisplay(), new RunStatus(rating, false).ToString());
        Assert.Equal("PASS", new ResultDocumentViewModel(Run(ResultRating.Pass, false)).Outcome);
        Assert.Equal(ResultRating.Pass, new ResultDocumentViewModel(Run(ResultRating.Pass, false)).OutcomeRating);
    }
    [Theory]
    [InlineData(true, null)] [InlineData(false, "ABORT")] [InlineData(false, null)]
    public void Legacy_abort_flag_and_new_explicit_status_are_both_readable(bool flag, string? status)
    {
        var writer = new MsgPackWriter().Map(status is null ? 3 : 4).Key("runId").Str("legacy").Key("rating").Str("PASS").Key("werror").Bool(flag);
        if (status is not null) writer.Key("status").Str(status);
        var body = writer.ToArray(); var bytes = new byte[8 + ResultFile.ChunkHeaderSize + body.Length];
        "TRES\x01\0\0\0"u8.CopyTo(bytes); "HEAD"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13), body.Length); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(17), body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), Crc32.Compute(body)); body.CopyTo(bytes, 25);
        var result = ResultFile.Read(bytes).Run; Assert.Equal(flag || status == "ABORT", result.OperatorAbort);
        Assert.Equal(flag || status == "ABORT" ? "ABORT" : "PASS", result.Status.ToString());
    }
    [SqliteFact]
    public async Task Database_preserves_abort_outcome_and_original_measurements_without_schema_change()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-abort-db-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var database = new SqlRunDatabase(NativeSqliteFactory.Instance, $"Data Source={Path.Combine(directory, "result.db")}", new SqliteDialect());
            await database.StoreAsync(Run(ResultRating.Pass, true), default);
            var loaded = await database.LoadRunAsync("run", default); Assert.NotNull(loaded); Assert.Equal("ABORT", loaded.Status.ToString());
            Assert.Equal(ResultRating.Pass, loaded.Rating); Assert.Equal("ABORT", Assert.Single(await database.QueryRunsAsync(new RunQuery(), default)).Status.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void Statistics_assign_each_aborted_run_only_to_abort_and_never_to_pass_yield()
    {
        var aborted = Enum.GetValues<ResultRating>().Select((r, i) => Run(r, true, "abort" + i)).ToArray();
        var statistics = ResultAnalysis.Compute(aborted, new StatisticsFilter());
        Assert.Equal(4, statistics.Runs); Assert.Equal(4, statistics.OperatorAborts);
        Assert.Equal(0, statistics.Pass + statistics.Fail + statistics.Error + statistics.NResult);
        Assert.Equal(0d, statistics.Yield); Assert.Equal(0d, statistics.FirstPassYield); Assert.Equal(0d, statistics.FinalYield);
        Assert.Equal(4, Assert.Single(statistics.Daily).Abort); Assert.Equal(4, statistics.Daily[0].Total);
        Assert.Equal(0d, Assert.Single(statistics.Stations).Yield); Assert.Contains("ABORT", ResultAnalysis.StatisticsCsv(statistics));
        var mixed = ResultAnalysis.Compute([Run(ResultRating.Pass, false), .. aborted], new StatisticsFilter()); Assert.Equal(1, mixed.Pass); Assert.Equal(.2, mixed.Yield);
    }
    [Fact]
    public async Task Real_operator_abort_marks_only_the_active_dut_and_keeps_previous_completed_result()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        var dut = new GroupDefinition("dut", "DUT") { IsDutRun = true }; dut.Variables.Add(new("Kennung", VarType.String));
        dut.Children.Add(Steps.Code("serial", "Serial", ctx => { ctx.Set("Kennung", "SN-" + ++count); return StepOutcome.Unrated(); }));
        dut.Children.Add(Steps.Rate("pass", ResultRating.Pass));
        dut.Children.Add(Steps.Code("gate", "Gate", async ctx =>
        { if (count == 2) { reached.SetResult(); await Task.Delay(Timeout.Infinite, ctx.CancellationToken); } return StepOutcome.Unrated(); }));
        dut.ExitGroup = new GroupDefinition("cleanup", "Cleanup"); dut.ExitGroup.Children.Add(Steps.Code("cleanup-error", "Cleanup result", _ => count == 2 ? new StepOutcome(ResultRating.Error) : StepOutcome.Unrated()));

        var loop = new GroupDefinition("loop", "Loop") { Repetition = Repetition.Endless }; loop.Children.Add(dut);
        var definition = new TestDefinition("Abort test", "1", new("start", "Start"), loop, new("end", "End"));
        var engine = new TestRun(definition); var recorder = new ResultRecorder(engine); var results = new List<ResultRun>(); recorder.RunRecorded += results.Add;
        var running = engine.RunAsync(); await reached.Task.Within(); engine.RequestAbort(); var status = await running.Within();
        Assert.Equal("ABORT", status.ToString()); Assert.Equal(2, results.Count);
        Assert.Equal("SN-1", results[0].DutId); Assert.Equal("PASS", results[0].Status.ToString());
        Assert.Equal("SN-2", results[1].DutId); Assert.Equal("ABORT", results[1].Status.ToString());
        Assert.Contains(results[1].Events, e => e.Kind == ResultEventKind.Abort); Assert.Contains(results[1].Events, e => e.Rating == ResultRating.Pass);
    }
}
