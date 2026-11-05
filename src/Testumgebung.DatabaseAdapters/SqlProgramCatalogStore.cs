using System.Data.Common;
using System.Text.Json;
using Testumgebung.Persistence.Programs;

namespace Testumgebung.DatabaseAdapters;

/// <summary>Additive catalog schema v1. Existing result/release tables are never altered; upgrade is explicit.</summary>
public sealed class SqlProgramCatalogStore(SqlRunDatabase database) : IProgramCatalogStore
{
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = database.Dialect.Provider switch
        {
            DatabaseProvider.Sqlite => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'tu_program_catalog'",
            DatabaseProvider.SqlServer => "SELECT CASE WHEN OBJECT_ID(N'tu_program_catalog', N'U') IS NULL THEN 0 ELSE 1 END",
            DatabaseProvider.PostgreSql => "SELECT CASE WHEN to_regclass('tu_program_catalog') IS NULL THEN 0 ELSE 1 END",
            _ => "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'tu_program_catalog' AND table_schema = DATABASE()"
        };
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }
    public async Task UpgradeAsync(CancellationToken ct = default)
    {
        // Use the same dialect's key/big/text definitions via a dedicated public migration statement.
        await using var connection = await database.OpenMigratedAsync(ct);
        if (await IsReadyAsync(ct)) return;
        await using var tx = await connection.BeginTransactionAsync(ct);
        var writer = new SqlRunDatabase.Writer(connection, tx, ct);
        await writer.ExecAsync(database.Dialect.CreateProgramCatalog);
        await writer.ExecAsync("INSERT INTO tu_program_catalog (version, generation, payload) SELECT 1, 0, @payload WHERE NOT EXISTS (SELECT 1 FROM tu_program_catalog WHERE version = 1)", ("payload", JsonSerializer.Serialize(ProgramCatalog.Empty)));
        await tx.CommitAsync(ct);
    }
    public async Task<ProgramCatalog> ReadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var value = await new SqlRunDatabase.Writer(connection, null, ct).ValueAsync("SELECT payload FROM tu_program_catalog WHERE version = 1");
        return JsonSerializer.Deserialize<ProgramCatalog>(value as string ?? throw new InvalidOperationException("Programmkatalog-Schema v1 fehlt. Datenbank aktualisieren."))!;
    }
    public async Task SaveAsync(ProgramCatalog catalog, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct);
        var next = catalog with { Generation = catalog.Generation + 1 };
        await using var cmd = new SqlRunDatabase.Writer(connection, null, ct).Command(
            "UPDATE tu_program_catalog SET generation = @next, payload = @payload WHERE version = 1 AND generation = @previous",
            ("next", next.Generation), ("payload", JsonSerializer.Serialize(next)), ("previous", catalog.Generation));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Programmquellen wurden gleichzeitig geändert. Neu laden.");
    }
}
