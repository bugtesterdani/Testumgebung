using Testumgebung.Contracts;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class ReportsPageBoundaryTests
{
    [Fact]
    public async Task Folder_load_reports_corruption_and_exports_filtered_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "report-page-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var page = new ReportsViewModel(() => null, null, new QueueDispatcher()); Assert.Equal("", page.Folder); Assert.Equal("–", page.RunsText); Assert.Equal("–", page.DutsText); Assert.Equal("–", page.DurationText); Assert.Equal("", page.FilterText); Assert.Equal("", page.MeasurementText);
            page.Apply(); page.ExportCsv(Path.Combine(directory, "none.csv")); page.ExportPdf(Path.Combine(directory, "none.pdf")); Assert.Empty(Directory.GetFiles(directory));
            page.Folder = " " + directory + " "; page.Source = "unknown"; Assert.True(page.IsFolderSource);
            File.WriteAllText(Path.Combine(directory, "bad" + ResultFile.Extension), "broken"); await page.LoadAsync(); Assert.Equal(1, page.ProblemCount); Assert.Single(page.Problems); Assert.Contains("mit Problemen", page.Status);
            File.Delete(Path.Combine(directory, "bad" + ResultFile.Extension)); await page.LoadAsync(); Assert.Equal(0, page.ProblemCount); Assert.Empty(page.Problems); Assert.Contains("0 Datei", page.Status); Assert.False(page.IsBusy);
            page.Test = null!; page.Version = null!; page.Station = null!; page.DutPattern = null!;
            page.From = new DateTime(2026, 1, 2, 12, 0, 0); page.To = new DateTime(2026, 1, 3, 12, 0, 0); Assert.Equal(0, page.Filter.From!.Value.Hour); Assert.Equal(23, page.Filter.To!.Value.Hour);
            page.ResetFilterCommand.Execute(null); Assert.Null(page.From); Assert.Null(page.To);
            page.Use([new ResultRun { RunId = "R", TestName = "T", TestVersion = "1", Station = "S", DutId = "SN", Rating = ResultRating.Pass, Started = DateTimeOffset.UnixEpoch, Ended = DateTimeOffset.UnixEpoch.AddSeconds(2) }]);
            Assert.True(page.HasStatistics); Assert.Equal("2,0 s", page.DurationText); Assert.NotEqual("–", page.RunsText); Assert.NotEqual("–", page.DutsText); Assert.NotEmpty(page.FilterText);
            page.ExportCsv(directory); Assert.Contains("nicht gespeichert", page.Status); page.ExportPdf(directory); Assert.Contains("nicht gespeichert", page.Status);
            page.Folder = null!; Assert.Equal("", page.Folder); await page.LoadAsync(); Assert.Contains("existiert nicht", Assert.Single(page.Problems));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Queued_folder_progress_cannot_overwrite_completed_or_subsequent_load_status()
    {
        var directory = Path.Combine(Path.GetTempPath(), "report-progress-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var dispatcher = new QueueDispatcher();
            var page = new ReportsViewModel(() => null, null, dispatcher) { Folder = directory };
            var corruptFile = Path.Combine(directory, "bad" + ResultFile.Extension);
            File.WriteAllText(corruptFile, "broken");
            await page.LoadAsync();
            Assert.Contains("mit Problemen", page.Status);
            var completedStatus = page.Status;

            // Intentionally deliver worker notifications after the final result.
            dispatcher.Pump();
            Assert.Equal(completedStatus, page.Status);

            // Leave another load's notifications queued while the next load finishes.
            await page.LoadAsync();
            File.Delete(corruptFile);
            await page.LoadAsync();
            Assert.Contains("0 Datei", page.Status);
            var subsequentStatus = page.Status;
            dispatcher.Pump();
            Assert.Equal(subsequentStatus, page.Status);
            Assert.False(page.IsBusy);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(2d, null)]
    [InlineData(null, 2d)]
    [InlineData(1d, 3d)]
    public void Histogram_highlights_only_bins_outside_limits(double? low, double? high)
    {
        var page = new ReportsViewModel(() => null, null, new QueueDispatcher());
        var measurement = new MeasurementStatistics("id", "Voltage", "V", 4, 0, 4, 2, 1, low, high, 0, null, null, [0, 1, 3, 4]); page.SelectedMeasurement = measurement;
        Assert.Equal(12, page.Histogram.Count); Assert.Equal(4, page.Histogram.Sum(b => int.Parse(b.Tooltip.Split(':')[1].Trim())));
        Assert.Contains("Voltage", page.MeasurementText); Assert.Contains("ohne Grenzwerte", page.MeasurementText);
        Assert.Equal(low is not null || high is not null, page.Histogram.Any(b => b.Highlight));
        page.SelectedMeasurement = measurement; Assert.Equal(12, page.Histogram.Count); page.SelectedMeasurement = null; Assert.Empty(page.Histogram); Assert.Equal("", page.MeasurementText);
    }

    private sealed class BlockedDatabase : IRunQuerySource
    {
        public string Name => "fixture";
        public TaskCompletionSource<IReadOnlyList<ResultRun>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RunQuery> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ResultRun>> QueryRunsAsync(RunQuery query, CancellationToken cancellationToken) { Entered.TrySetResult(query); return Result.Task; }
    }
    [Theory]
    [InlineData("io")]
    [InlineData("invalid")]
    [InlineData("timeout")]
    public async Task Failed_database_query_reports_error_and_releases_loading_guard(string error)
    {
        var db = new BlockedDatabase(); var page = new ReportsViewModel(() => null, null, new QueueDispatcher(), () => db) { Source = ReportsViewModel.DatabaseSource };
        var loading = page.LoadAsync(); await db.Entered.Task.Within(); Assert.True(page.IsBusy); Assert.False(page.LoadCommand.CanExecute(null)); await page.LoadAsync();
        db.Result.SetException(error switch { "io" => new IOException("fixture"), "invalid" => new InvalidOperationException("fixture"), _ => new TimeoutException("fixture") }); await loading.Within();
        Assert.False(page.IsBusy); Assert.True(page.LoadCommand.CanExecute(null)); Assert.Contains("Datenbank nicht lesbar: fixture", page.Status);
    }
}
