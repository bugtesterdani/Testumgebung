using Microsoft.Data.Sqlite;
using Testumgebung.Contracts;
using Testumgebung.RemoteAdmin;
using Xunit;

namespace Testumgebung.RemoteAdmin.Tests;

public sealed class RemoteSettingsDatabaseTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tu-remote-");
    private static readonly RemoteStationSettings Initial = new(false, "", "", 0, 0, false, false);
    private static readonly string[] Names = ["P1", "P2"];
    private string DatabasePath => Path.Combine(_directory.FullName, "settings.db");

    [Fact]
    public void Revision_and_audit_survive_restart_and_agent_acknowledgment_is_idempotent()
    {
        var db = Create();
        Assert.Null(db.Exchange("lab", new(Initial, Names)));
        var station = Assert.Single(db.Stations());
        var desired = Initial with { ErrorStreak = 4 };
        Assert.True(db.Request(station.Id, station.Version, "entra-object-id", desired));
        Assert.False(db.Request(station.Id, station.Version, "other", desired));

        db = Create();
        var change = db.Exchange("lab", new(Initial, Names));
        Assert.NotNull(change);
        Assert.Equal((Initial, desired), (change.Expected, change.Desired));
        Assert.Null(db.Exchange("lab", new(desired, Names, change.Revision, "applied")));
        Assert.Null(db.Exchange("lab", new(desired, Names, change.Revision, "applied")));

        var revision = Assert.Single(db.History("lab"));
        Assert.Equal(("entra-object-id", Initial, desired, "applied"),
            (revision.Actor, revision.Before, revision.After, revision.Status));
        Assert.NotNull(revision.Completed);
        Assert.Equal(desired, Assert.Single(db.Stations()).Settings);
    }

    [Fact]
    public void Outdated_requests_and_reports_for_other_stations_cannot_overwrite_local_changes()
    {
        var db = Create();
        db.Exchange("lab", new(Initial, Names));
        var version = Assert.Single(db.Stations()).Version;
        var unknownStation = Initial with { ShowPairedStations = true, FirstPairedStation = "P1", SecondPairedStation = "P3" };
        Assert.False(db.Request("lab", version, "manager", unknownStation));

        var desired = Initial with { CalibrationAlarm = true };
        Assert.True(db.Request("lab", version, "manager", desired));
        var local = Initial with { OutboxThreshold = 3 };
        var change = db.Exchange("lab", new(local, Names));
        Assert.NotNull(change);
        db.Exchange("other", new(desired, Names, change.Revision, "applied"));
        Assert.Equal("pending", Assert.Single(db.History("lab")).Status);

        db.Exchange("lab", new(local, Names, change.Revision, "conflict"));
        Assert.Equal("conflict", Assert.Single(db.History("lab")).Status);
        Assert.Equal(local, db.Stations().Single(station => station.Id == "lab").Settings);
        Assert.False(db.Request("lab", version, "manager", desired));
    }

    private RemoteSettingsDatabase Create()
    {
        var db = new RemoteSettingsDatabase(DatabasePath);
        db.Initialize();
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }
}
