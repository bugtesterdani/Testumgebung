using System.Text.Json;
using Microsoft.Data.Sqlite;
using Testumgebung.Contracts;

namespace Testumgebung.RemoteAdmin;

public sealed record StationSnapshot(string Id, string Seen, string[] Stations, RemoteStationSettings Settings, long Version);
public sealed record StationRevision(long Id, string Requested, string Actor, RemoteStationSettings Before,
    RemoteStationSettings After, string Status, string? Completed);

public sealed class RemoteSettingsDatabase(string path)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    public void Initialize()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS stations (
                id TEXT PRIMARY KEY, seen TEXT NOT NULL, names TEXT NOT NULL, settings TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS revisions (
                id INTEGER PRIMARY KEY AUTOINCREMENT, station TEXT NOT NULL, requested TEXT NOT NULL,
                actor TEXT NOT NULL, previous TEXT NOT NULL, desired TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending', completed TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS one_pending ON revisions(station) WHERE status = 'pending';
            CREATE INDEX IF NOT EXISTS station_history ON revisions(station, id DESC);
            """;
        command.ExecuteNonQuery();
    }

    public List<StationSnapshot> Stations()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id, seen, names, settings, version FROM stations ORDER BY id");
        using var reader = command.ExecuteReader();
        var stations = new List<StationSnapshot>();
        while (reader.Read()) stations.Add(ReadStation(reader));
        return stations;
    }

    public List<StationRevision> History(string id)
    {
        using var db = Open();
        using var command = Command(db, null, """
            SELECT id, requested, actor, previous, desired, status, completed
            FROM revisions WHERE station = $station ORDER BY id DESC LIMIT 50
            """, ("$station", id));
        using var reader = command.ExecuteReader();
        var revisions = new List<StationRevision>();
        while (reader.Read())
            revisions.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                Read<RemoteStationSettings>(reader.GetString(3)), Read<RemoteStationSettings>(reader.GetString(4)),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return revisions;
    }

    public bool Request(string id, long version, string actor, RemoteStationSettings desired)
    {
        if (!desired.IsValid) return false;
        using var db = Open();
        using var transaction = db.BeginTransaction();
        var station = Find(db, transaction, id);
        if (station is null || station.Version != version || desired == station.Settings || !desired.MatchesStations(station.Stations))
            return false;
        using var command = Command(db, transaction, """
            INSERT INTO revisions (station, requested, actor, previous, desired)
            SELECT $station, $time, $actor, $previous, $desired
            WHERE NOT EXISTS (SELECT 1 FROM revisions WHERE station = $station AND status = 'pending')
            """, ("$station", id), ("$time", Now()), ("$actor", actor), ("$previous", Write(station.Settings)), ("$desired", Write(desired)));
        var added = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return added;
    }

    public RemoteSettingsChange? Exchange(string id, RemoteSettingsReport report)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        UpdateSnapshot(db, transaction, id, report);
        if (report.Revision is { } revision) Complete(db, transaction, id, revision, report);
        var change = Pending(db, transaction, id);
        transaction.Commit();
        return change;
    }

    private static void UpdateSnapshot(SqliteConnection db, SqliteTransaction tx, string id, RemoteSettingsReport report)
    {
        using var command = Command(db, tx, """
            INSERT INTO stations (id, seen, names, settings) VALUES ($id, $seen, $names, $settings)
            ON CONFLICT(id) DO UPDATE SET seen = excluded.seen, names = excluded.names, settings = excluded.settings,
                version = stations.version + (stations.names <> excluded.names OR stations.settings <> excluded.settings)
            """, ("$id", id), ("$seen", Now()), ("$names", Write(report.Stations)), ("$settings", Write(report.Current)));
        command.ExecuteNonQuery();
    }

    private static void Complete(SqliteConnection db, SqliteTransaction tx, string id, long revision, RemoteSettingsReport report)
    {
        using var command = Command(db, tx, """
            UPDATE revisions SET status = $status, completed = $time
            WHERE station = $station AND id = $revision AND status = 'pending'
                AND ($status <> 'applied' OR desired = $current)
            """, ("$station", id), ("$revision", revision), ("$status", report.Status!),
            ("$time", Now()), ("$current", Write(report.Current)));
        command.ExecuteNonQuery();
    }

    private static RemoteSettingsChange? Pending(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var command = Command(db, tx, """
            SELECT id, previous, desired FROM revisions WHERE station = $station AND status = 'pending'
            """, ("$station", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), Read<RemoteStationSettings>(reader.GetString(1)),
            Read<RemoteStationSettings>(reader.GetString(2))) : null;
    }

    private static StationSnapshot? Find(SqliteConnection db, SqliteTransaction tx, string id)
    {
        using var command = Command(db, tx, "SELECT id, seen, names, settings, version FROM stations WHERE id = $id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStation(reader) : null;
    }

    private static StationSnapshot ReadStation(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        Read<string[]>(reader.GetString(2)), Read<RemoteStationSettings>(reader.GetString(3)), reader.GetInt64(4));

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }


    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Write<T>(T data) => JsonSerializer.Serialize(data);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
}
