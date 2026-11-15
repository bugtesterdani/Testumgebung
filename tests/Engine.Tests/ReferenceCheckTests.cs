using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class ReferenceCheckTests
{
    private static ReferenceCheckConfig Settings() => new() { Enabled = true, GoodId = "GOOD", BadId = "BAD" };

    [Theory]
    [InlineData("time")]
    [InlineData("shift")]
    [InlineData("count")]
    public void Both_samples_must_pass_with_expected_ratings_before_production_is_released(string trigger)
    {
        var config = Settings();
        var now = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero).ToLocalTime();
        var start = TimeOnly.FromDateTime(now.LocalDateTime);
        config.ShiftStarts = $"{start:HH:mm};{start.AddHours(4):HH:mm}";
        if (trigger == "time") config.HoursInterval = 1;
        if (trigger == "count") config.DutInterval = 2;
        var state = new ReferenceCheckState();

        Assert.True(state.IsDue(config, now));
        Assert.True(state.RecordReference(config, ResultRunKind.GoodReference, new(ResultRating.Pass, false), now));
        Assert.False(state.RecordReference(config, ResultRunKind.BadReference, new(ResultRating.Pass, false), now));
        Assert.False(state.RecordReference(config, ResultRunKind.BadReference, new(ResultRating.Fail, true), now));
        Assert.True(state.IsDue(config, now));
        Assert.True(state.RecordReference(config, ResultRunKind.BadReference, new(ResultRating.Fail, false), now));
        Assert.False(state.IsDue(config, now));
        if (trigger == "count")
        {
            state.RecordProduction();
            Assert.False(state.IsDue(config, now));
            state.RecordProduction();
        }
        else
        {
            var interval = trigger == "time" ? 1 : 4;
            Assert.False(state.IsDue(config, now.AddHours(interval).AddSeconds(-1)));
            now = now.AddHours(interval);
        }
        Assert.True(state.IsDue(config, now));
        Assert.True(state.RecordReference(config, ResultRunKind.GoodReference, new(ResultRating.Pass, false), now));
        Assert.True(state.IsDue(config, now));
    }

    [Fact]
    public async Task Station_rejects_production_until_references_pass_then_stops_after_the_configured_count()
    {
        var dut = Group("dut").Var("Kennung", VarType.String, "PRODUCT");
        dut.IsDutRun = true;
        dut.Add(new GroupDefinition("good", "Gut") { EntryCondition = "Kennung != \"BAD\"" }
            .Add(Rating("pass", "PASS")));
        dut.Add(new GroupDefinition("bad", "Schlecht") { EntryCondition = "Kennung == \"BAD\"" }
            .Add(Rating("fail", "FAIL")));
        var loop = Group("loop").Add(dut);
        loop.Repetition = Repetition.Endless;
        var project = new TestProject(Test(loop, finalization: Group("fin").Add(Rating("end", "NRESULT"))));
        var setup = StationSetup.Default(project);
        var config = Settings();
        config.DutInterval = 2;
        setup.Stations[0].ReferenceCheck = config;
        var ui = new QueueDispatcher();
        var shell = new ShellViewModel(ui, project: project, clock: new VirtualClock(),
            stations: new StationCoordinator(setup, new ResourceManager()));
        var station = shell.SelectedStation;

        await station.StartAsync().Within();
        Assert.Null(station.Run);
        Assert.Contains("gesperrt", station.Error);
        station.ReferenceScan = "GOOD";
        await station.StartReferenceAsync().Within();
        ui.Pump();
        Assert.Equal(1, station.Run!.DutsCompleted);
        Assert.Contains(station.Protocol, row => row.Text == "Referenzprüfung bestätigt");
        await station.StartAsync().Within();
        Assert.Contains("gesperrt", station.Error);

        station.ReferenceScan = "BAD";
        await station.StartReferenceAsync().Within();
        ui.Pump();
        Assert.Contains(station.Protocol, row => row.Status == "Produktion freigegeben");
        await station.StartAsync().Within();
        ui.Pump();
        Assert.Equal(2, station.Run!.DutsCompleted);
        Assert.Single(station.Run.Steps("end"));
        Assert.False(station.Run.OperatorAborted);
        await station.StartAsync().Within();
        Assert.Contains("gesperrt", station.Error);

        static StepDefinition Rating(string id, string rating)
            => BlockCatalog.Default.Create(BlockCatalog.SetRating, id, id, new Dictionary<string, string> { ["Bewertung"] = rating });
    }

    [Theory]
    [InlineData(ResultRunKind.GoodReference)]
    [InlineData(ResultRunKind.BadReference)]
    public void References_round_trip_in_result_files_and_do_not_change_production_metrics(ResultRunKind kind)
    {
        var reference = new ResultRun { RunId = "ref", DutId = "ref", Kind = kind, Rating = ResultRating.Fail };
        using var stream = new MemoryStream();
        ResultFile.Write(stream, reference);
        var saved = ResultFile.Read(stream.ToArray());
        Assert.True(saved.IsIntact);
        Assert.Equal(kind, saved.Run.Kind);
        Assert.Equal(kind, saved.Run.CopyWith("Datenbank", [], [], []).Kind);

        var production = new ResultRun { RunId = "prod", DutId = "prod", Rating = ResultRating.Pass };
        var stats = ResultAnalysis.Compute([production, saved.Run], new());
        Assert.Equal(1, stats.Runs);
        Assert.Equal(1d, stats.FirstPassYield);
        Assert.Equal(1d, stats.FinalYield);
    }
}
