using System.Data.Common;
using System.Text.Json;
using Testumgebung.Contracts;
using Writer = Testumgebung.DatabaseAdapters.SqlRunDatabase.Writer;

namespace Testumgebung.DatabaseAdapters;

public sealed record StationSnapshot(string Id, string Seen, string[] Stations, RemoteStationSettings Settings, long Version, bool Pending);
public sealed record StationRevision(long Id, string Requested, string Actor, RemoteStationSettings Before,
    RemoteStationSettings After, string Status, string? Completed);

/// <summary>Configuration snapshots and audit revisions in the same database as the run results.</summary>
public sealed class SqlRemoteSettingsStore(SqlRunDatabase database)
{
    private volatile bool _initialized;

    public async Task<List<StationSnapshot>> StationsAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        var read = new Writer(db, null, ct);
        await using var command = read.Command("""
            SELECT station_id, seen, names, settings, generation, pending_revision
            FROM tu_station_settings WHERE revision = 0 ORDER BY station_id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var stations = new List<StationSnapshot>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) stations.Add(Snapshot(reader));
        return stations;
    }

    public async Task<List<StationRevision>> HistoryAsync(string id, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        var read = new Writer(db, null, ct);
        await using var command = read.Command("""
            SELECT revision, seen, actor, previous, settings, status, completed
            FROM tu_station_settings WHERE station_id = @id AND revision > 0 ORDER BY revision DESC
            """, ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var history = new List<StationRevision>();
        while (history.Count < 50 && await reader.ReadAsync(ct).ConfigureAwait(false))
            history.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                Read<RemoteStationSettings>(reader.GetString(3)), Read<RemoteStationSettings>(reader.GetString(4)),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return history;
    }

    public async Task<bool> RequestAsync(string id, long version, string actor, RemoteStationSettings desired, CancellationToken ct)
    {
        if (!desired.IsValid) return false;
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var write = new Writer(db, tx, ct);
        var station = await FindAsync(write, id, ct).ConfigureAwait(false);
        if (!CanRequest(station, version, desired)) return false;

        var next = version + 1;
        var updated = await write.ExecCountAsync("""
            UPDATE tu_station_settings SET generation = @next, pending_revision = @next
            WHERE revision = 0 AND station_id = @id AND generation = @version AND pending_revision = 0
            """, ("id", id), ("version", version), ("next", next)).ConfigureAwait(false);
        if (updated != 1) return false;

        await write.ExecAsync("""
            INSERT INTO tu_station_settings (station_id, revision, generation, pending_revision, seen, settings, actor, previous, status)
            VALUES (@id, @next, 0, 0, @seen, @settings, @actor, @previous, 'pending')
            """, ("id", id), ("next", next), ("seen", Now()), ("settings", Write(desired)),
            ("actor", actor), ("previous", Write(station!.Settings))).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static bool CanRequest(StationSnapshot? station, long version, RemoteStationSettings desired)
        => station is { Pending: false } && station.Version == version && desired != station.Settings && desired.MatchesStations(station.Stations);

    public async Task<RemoteSettingsChange?> ExchangeAsync(string id, RemoteSettingsReport report, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await db.BeginTransactionAsync(ct).ConfigureAwait(false);
        var write = new Writer(db, tx, ct);
        var station = await FindAsync(write, id, ct).ConfigureAwait(false);
        if (station is null)
            await InsertSnapshotAsync(write, id, report).ConfigureAwait(false);
        else
            await UpdateSnapshotAsync(write, id, station, report).ConfigureAwait(false);

        var change = await PendingAsync(write, id, ct).ConfigureAwait(false);
        if (CanComplete(report, change))
        {
            await CompleteAsync(write, id, change!.Revision, report.Status!).ConfigureAwait(false);
            change = null;
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return change;
    }

    private static Task InsertSnapshotAsync(Writer write, string id, RemoteSettingsReport report) => write.ExecAsync("""
        INSERT INTO tu_station_settings (station_id, revision, generation, pending_revision, seen, names, settings)
        VALUES (@id, 0, 1, 0, @seen, @names, @settings)
        """, ("id", id), ("seen", Now()), ("names", Write(report.Stations)), ("settings", Write(report.Current)));

    private static Task UpdateSnapshotAsync(Writer write, string id, StationSnapshot station, RemoteSettingsReport report)
    {
        var changed = station.Settings != report.Current || !station.Stations.SequenceEqual(report.Stations);
        return write.ExecAsync("""
            UPDATE tu_station_settings SET seen = @seen, names = @names, settings = @settings, generation = generation + @delta
            WHERE station_id = @id AND revision = 0
            """, ("id", id), ("seen", Now()), ("names", Write(report.Stations)), ("settings", Write(report.Current)), ("delta", changed ? 1L : 0L));
    }

    private static bool CanComplete(RemoteSettingsReport report, RemoteSettingsChange? change) =>
        change is not null && report.Revision == change.Revision && (report.Status switch
        {
            "applied" => report.Current == change.Desired,
            "conflict" or "invalid" => true,
            _ => false,
        });

    private static async Task CompleteAsync(Writer write, string id, long revision, string status)
    {
        await write.ExecAsync("""
            UPDATE tu_station_settings SET status = @status, completed = @completed
            WHERE station_id = @id AND revision = @revision AND status = 'pending'
            """, ("id", id), ("revision", revision), ("status", status), ("completed", Now())).ConfigureAwait(false);
        await write.ExecAsync("""
            UPDATE tu_station_settings SET pending_revision = 0, generation = generation + 1
            WHERE station_id = @id AND revision = 0 AND pending_revision = @revision
            """, ("id", id), ("revision", revision)).ConfigureAwait(false);
    }

    private static async Task<RemoteSettingsChange?> PendingAsync(Writer write, string id, CancellationToken ct)
    {
        await using var command = write.Command("""
            SELECT r.revision, r.previous, r.settings FROM tu_station_settings s
            JOIN tu_station_settings r ON r.station_id = s.station_id AND r.revision = s.pending_revision
            WHERE s.station_id = @id AND s.revision = 0 AND s.pending_revision > 0
            """, ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new(reader.GetInt64(0),
            Read<RemoteStationSettings>(reader.GetString(1)), Read<RemoteStationSettings>(reader.GetString(2))) : null;
    }

    private static async Task<StationSnapshot?> FindAsync(Writer write, string id, CancellationToken ct)
    {
        await using var command = write.Command("""
            SELECT station_id, seen, names, settings, generation, pending_revision
            FROM tu_station_settings WHERE station_id = @id AND revision = 0
            """, ("id", id));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Snapshot(reader) : null;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                var write = new Writer(connection, null, ct);
                await EnsureTableAsync(write).ConfigureAwait(false);
                _initialized = true;
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureTableAsync(Writer write)
    {
        try { await write.ScalarAsync("SELECT COUNT(*) FROM tu_station_settings WHERE revision = -1").ConfigureAwait(false); }
        catch (DbException) { await write.ExecAsync(database.Dialect.CreateRemoteSettingsTable).ConfigureAwait(false); }
    }

    private static StationSnapshot Snapshot(DbDataReader reader) => new(reader.GetString(0), reader.GetString(1),
        Read<string[]>(reader.GetString(2)), Read<RemoteStationSettings>(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5) > 0);

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static string Write<T>(T data) => JsonSerializer.Serialize(data);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)!;
}
