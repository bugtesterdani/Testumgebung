using Testumgebung.Contracts;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ProductionOverviewTests
{
    private static readonly DateTimeOffset Evening = new(2026, 8, 1, 22, 0, 0, TimeSpan.Zero);

    private static ResultRun Run(string id, string dut, int minutes, ResultRating rating, string order = "A-1",
        string station = "P1", ResultRunKind kind = ResultRunKind.Production, bool development = false)
    {
        var started = Evening.AddMinutes(minutes);
        return new()
        {
            RunId = id, DutId = dut, Started = started, Ended = started.AddSeconds(10), Rating = rating,
            Station = station, Order = order, TestName = "Test", Kind = kind, IsDevelopmentRun = development,
            Events = [new(1, ResultEventKind.Diagnostic, null, "", 1, started, Message: "Aus der Auswahl übernommen: Auftrag = \"ALT\"")],
        };
    }

    [Fact]
    public void Overnight_shifts_keep_the_start_date_and_yields_count_retests_correctly()
    {
        var runs = new[]
        {
            Run("retry", "1", 180, ResultRating.Pass),
            Run("first", "1", 5, ResultRating.Fail),
            Run("pass", "2", 120, ResultRating.Pass),
            Run("ref", "3", 37, ResultRating.Fail, kind: ResultRunKind.BadReference),
            Run("dev", "4", 37, ResultRating.Fail, development: true),
            Run("otherOrder", "1", 37, ResultRating.Pass, order: ""),
            Run("otherStation", "1", 37, ResultRating.Pass, station: "P2"),
            Run("nextShift", "1", 480, ResultRating.Pass),
        };
        var overview = ProductionOverview.Compute(runs, new(), station => station == "P2" ? [new(8, 0), new(20, 0)] : [], TimeZoneInfo.Utc);
        Assert.Equal(4, overview.Count);
        var night = overview.Single(row => row.Station == "P1" && row.Order == "A-1" && row.ShiftStarted == Evening.DateTime);
        Assert.Equal(2, night.Metrics.Pieces);
        Assert.Equal(1, night.Metrics.Retests);
        Assert.Equal(0.5, night.Metrics.FirstPassYield);
        Assert.Equal(1, night.Metrics.FinalYield);
        Assert.Equal(TimeSpan.FromSeconds(10), night.Metrics.MeanCycleTime);
        Assert.Contains(overview, row => row.Order == "ALT");
        Assert.Contains(overview, row => row.Station == "P2" && row.ShiftStarted == Evening.AddHours(-2).DateTime);
        Assert.Equal(Evening.AddHours(8).DateTime, overview[0].ShiftStarted);
    }

    [Fact]
    public async Task Production_overview_uses_same_filters_for_files_and_database()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tu-production-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            ResultRun[] runs = [Run("p1", "1", 5, ResultRating.Pass), Run("p2", "2", 0, ResultRating.Fail, station: "P2")];
            foreach (var run in runs)
            {
                using var file = File.Create(Path.Combine(folder, run.RunId + ResultFile.Extension));
                ResultFile.Write(file, run);
            }
            var source = new TestDatabase(runs);
            var page = new ReportsViewModel(() => folder, null, new QueueDispatcher(), () => source)
            {
                Station = "P1",
            };
            await page.LoadAsync();
            var atStation = Assert.Single(page.ProductionRows);
            Assert.Equal("P1", atStation.Station);
            Assert.Equal(1, atStation.Metrics.Pieces);

            page.Source = ReportsViewModel.DatabaseSource;
            await page.LoadAsync();
            Assert.Equal(atStation, Assert.Single(page.ProductionRows));
            Assert.Equal("P1", source.Query!.Station);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private sealed class TestDatabase(IReadOnlyList<ResultRun> runs) : IRunQuerySource
    {
        public string Name => "Testdatenbank";
        public RunQuery? Query { get; private set; }

        public Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult<IReadOnlyList<ResultRun>>(runs.Where(query.Matches).ToArray());
        }
    }
}
