using Testumgebung.Persistence.Results;
using Testumgebung.Contracts;
using Testumgebung.DatabaseAdapters;
using Xunit;

namespace Testumgebung.Engine.Tests;

public sealed class RemoteSettingsDatabaseTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tu-remote-");
    private static readonly RemoteStationSettings Initial = new(false, "", "", 0, 0, false, false);
    private static readonly string[] Names = ["P1", "P2"];

    [SqliteFact]
    public async Task Revision_and_audit_survive_restart_in_the_result_database()
    {
        var results = CreateDatabase();
        await results.StoreAsync(new ResultRun { RunId = "existing-result", Station = "lab" }, default);
        var db = results.RemoteSettings;
        Assert.Null(await db.ExchangeAsync("lab", new(Initial, Names), default));
        var station = Assert.Single(await db.StationsAsync(default));
        var desired = Initial with { ErrorStreak = 4 };
        Assert.True(await db.RequestAsync(station.Id, station.Version, "entra-object-id", desired, default));
        var pending = Assert.Single(await db.StationsAsync(default));
        Assert.True(pending.Pending);
        Assert.False(await db.RequestAsync(station.Id, pending.Version, "other", desired, default));

        db = CreateDatabase().RemoteSettings;
        var change = await db.ExchangeAsync("lab", new(Initial, Names), default);
        Assert.NotNull(change);
        Assert.Equal((Initial, desired), (change.Expected, change.Desired));
        Assert.Null(await db.ExchangeAsync("lab", new(desired, Names, change.Revision, "applied"), default));
        Assert.Null(await db.ExchangeAsync("lab", new(desired, Names, change.Revision, "applied"), default));

        var revision = Assert.Single(await db.HistoryAsync("lab", default));
        Assert.Equal(("entra-object-id", Initial, desired, "applied"),
            (revision.Actor, revision.Before, revision.After, revision.Status));
        Assert.NotNull(revision.Completed);
        station = Assert.Single(await db.StationsAsync(default));
        Assert.Equal(desired, station.Settings);
        Assert.False(station.Pending);
        Assert.Contains("1 Run(s)", await results.TestConnectionAsync(default));
    }

    [SqliteFact]
    public async Task A_conflict_and_other_station_reports_cannot_clear_the_wrong_revision()
    {
        var db = CreateDatabase().RemoteSettings;
        await db.ExchangeAsync("lab", new(Initial, Names), default);
        var version = Assert.Single(await db.StationsAsync(default)).Version;
        var unknownStation = Initial with { ShowPairedStations = true, FirstPairedStation = "P1", SecondPairedStation = "P3" };
        Assert.False(await db.RequestAsync("lab", version, "manager", unknownStation, default));

        var desired = Initial with { CalibrationAlarm = true };
        Assert.True(await db.RequestAsync("lab", version, "manager", desired, default));
        var local = Initial with { OutboxThreshold = 3 };
        var change = await db.ExchangeAsync("lab", new(local, Names), default);
        Assert.NotNull(change);
        await db.ExchangeAsync("other", new(desired, Names, change.Revision, "applied"), default);
        Assert.NotNull(await db.ExchangeAsync("lab", new(local, Names, change.Revision, "applied"), default));
        Assert.Equal("pending", Assert.Single(await db.HistoryAsync("lab", default)).Status);

        await db.ExchangeAsync("lab", new(local, Names, change.Revision, "conflict"), default);
        Assert.Equal("conflict", Assert.Single(await db.HistoryAsync("lab", default)).Status);
        Assert.Equal(local, (await db.StationsAsync(default)).Single(station => station.Id == "lab").Settings);
        Assert.False(await db.RequestAsync("lab", version, "manager", desired, default));
    }

    private SqlRunDatabase CreateDatabase() => new(NativeSqliteFactory.Instance,
        $"Data Source={Path.Combine(_directory.FullName, "results.db")}", SqlDialect.For(DatabaseProvider.Sqlite));

    public void Dispose()
    {

        _directory.Delete(recursive: true);
    }
}
