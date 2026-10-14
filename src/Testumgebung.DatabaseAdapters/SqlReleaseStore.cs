/**
 * @file
 * @brief Speichert Release-Pakete, Zustandswechsel und Audit-Einträge in der Prüfdatenbank.
 */

using System.Data.Common;
using System.Globalization;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.DatabaseAdapters;

/// <summary>
/// Release-Pakete und Audit-Log in der Prüfdatenbank (<c>tu_releases</c>, <c>tu_audit_log</c>, Schema v2).
/// Pakete werden nur eingefügt, nie geändert; Zustandswechsel sind bedingte Updates (nur aus dem erwarteten Zustand).
/// Audit-Einträge werden fortlaufend nummeriert und verkettet; parallele Schreiber wiederholen bei Schlüsselkonflikt.
/// </summary>
public sealed class SqlReleaseStore(SqlRunDatabase database) : IReleaseStore
{
    private const string Columns = "release_id, project_name, version, state, package_sha256, package_size, created_by, created_utc, " +
                                   "reviewed_by, reviewed_utc, released_by, released_utc, archived_utc, review_report";

    public string Name => database.Name;

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlRunDatabase.Writer(connection, null, cancellationToken).Command($"SELECT {Columns} FROM tu_releases");
        var list = new List<ReleaseInfo>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ReleaseInfo(
                Str(r, 0)!, Str(r, 1)!, Str(r, 2)!, ReleaseStateText.Parse(Str(r, 3)!), Str(r, 4)!, Lng(r, 5) ?? 0,
                Str(r, 6)!, Time(Lng(r, 7))!.Value, Str(r, 8), Time(Lng(r, 9)), Str(r, 10), Time(Lng(r, 11)), Time(Lng(r, 12)), Str(r, 13)));
        }
        return list.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task<byte[]> GetPackageAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenMigratedAsync(cancellationToken).ConfigureAwait(false);
        var v = await new SqlRunDatabase.Writer(connection, null, cancellationToken)
            .ValueAsync("SELECT package FROM tu_releases WHERE release_id = @id", ("id", releaseId)).ConfigureAwait(false);
        return v as byte[] ?? throw new ReleasePackageException($"Paket {releaseId} nicht gefunden.");
    }

    public Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken cancellationToken = default)
        => InTransaction(async w =>
        {
            if (await w.ScalarAsync("SELECT COUNT(*) FROM tu_releases WHERE project_name = @p AND version = @v",
                    ("p", info.ProjectName), ("v", info.Version)).ConfigureAwait(false) > 0)
                throw new InvalidOperationException($"Version {info.Version} von '{info.ProjectName}' existiert bereits.");
            await w.ExecAsync(
                """
                INSERT INTO tu_releases (release_id, project_name, version, state, package, package_sha256, package_size, created_by, created_utc)
                VALUES (@id, @project, @version, @state, @package, @sha, @size, @by, @at)
                """,
                ("id", info.ReleaseId), ("project", info.ProjectName), ("version", info.Version), ("state", ReleaseStateText.Code(info.State)),
                ("package", package), ("sha", info.PackageSha256), ("size", (long)package.Length), ("by", info.CreatedBy),
                ("at", info.CreatedAt.UtcTicks)).ConfigureAwait(false);
            await AppendAsync(w, audit).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<bool> TransitionAsync(ReleaseTransition t, AuditDraft audit, CancellationToken cancellationToken = default)
        => InTransaction(async w =>
        {
            var project = await w.ValueAsync("SELECT project_name FROM tu_releases WHERE release_id = @id", ("id", t.ReleaseId)).ConfigureAwait(false) as string;
            if (project is null) return false;
            if (t.ArchiveOtherReleased)
                await w.ExecAsync("UPDATE tu_releases SET state = @archived, archived_utc = @t WHERE project_name = @p AND state = @released AND release_id <> @id",
                    ("archived", ReleaseStateText.Code(ReleaseState.Archived)), ("t", t.Time.UtcTicks), ("p", project),
                    ("released", ReleaseStateText.Code(ReleaseState.Released)), ("id", t.ReleaseId)).ConfigureAwait(false);
            var (setBy, setAt) = t.To switch
            {
                ReleaseState.Review => ("reviewed_by", "reviewed_utc"),
                ReleaseState.Released => ("released_by", "released_utc"),
                _ => ((string?)null, "archived_utc"),
            };
            var sql = $"UPDATE tu_releases SET state = @to, {setAt} = @t" + (setBy is null ? "" : $", {setBy} = @user") +
                      (t.ReviewReport is null ? "" : ", review_report = @report") + " WHERE release_id = @id AND state = @from";
            var args = new List<(string, object?)>
            {
                ("to", ReleaseStateText.Code(t.To)), ("t", t.Time.UtcTicks), ("id", t.ReleaseId), ("from", ReleaseStateText.Code(t.From)),
            };
            if (setBy is not null) args.Add(("user", t.User));
            if (t.ReviewReport is not null) args.Add(("report", t.ReviewReport));
            if (await w.ExecCountAsync(sql, args.ToArray()).ConfigureAwait(false) != 1) throw new StaleReleaseException();
            await AppendAsync(w, audit).ConfigureAwait(false);
            return true;
        }, cancellationToken, staleIsFalse: true);

    public Task AppendAuditAsync(AuditDraft audit, CancellationToken cancellationToken = default)
        => InTransaction(async w =>
        {
            await AppendAsync(w, audit).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public async Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlRunDatabase.Writer(connection, null, cancellationToken)
            .Command("SELECT seq, time_utc, user_name, action, details, release_id, prev_hash, entry_hash FROM tu_audit_log ORDER BY seq");
        var list = new List<AuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(new AuditEntry(Lng(r, 0)!.Value, Time(Lng(r, 1))!.Value, Str(r, 2) ?? "", Str(r, 3) ?? "", Str(r, 4) ?? "", Str(r, 5), Str(r, 6) ?? "", Str(r, 7) ?? ""));
        return list;
    }

    private static async Task AppendAsync(SqlRunDatabase.Writer w, AuditDraft d)
    {
        var last = await w.ScalarAsync("SELECT COALESCE(MAX(seq), 0) FROM tu_audit_log").ConfigureAwait(false);
        var previous = last == 0
            ? AuditChain.Genesis
            : await w.ValueAsync("SELECT entry_hash FROM tu_audit_log WHERE seq = @s", ("s", last)).ConfigureAwait(false) as string ?? AuditChain.Genesis;
        var e = AuditChain.Create(last + 1, d.Time, d.User, d.Action, d.Details, d.ReleaseId, previous);
        await w.ExecAsync(
            """
            INSERT INTO tu_audit_log (seq, time_utc, time_text, user_name, action, details, release_id, prev_hash, entry_hash)
            VALUES (@seq, @t, @text, @user, @action, @details, @release, @prev, @hash)
            """,
            ("seq", e.Sequence), ("t", e.Time.UtcTicks), ("text", e.Time.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture)),
            ("user", e.User), ("action", e.Action), ("details", e.Details), ("release", e.ReleaseId), ("prev", e.PreviousHash), ("hash", e.Hash)).ConfigureAwait(false);
    }

    private sealed class StaleReleaseException : Exception;

    /// <summary>Transaktion mit Wiederholung bei Schlüsselkonflikt (zwei Prüfplätze schreiben gleichzeitig ins Audit-Log).</summary>
    private async Task<bool> InTransaction(Func<SqlRunDatabase.Writer, Task<bool>> work, CancellationToken ct, bool staleIsFalse = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var connection = await database.OpenMigratedAsync(ct).ConfigureAwait(false);
            // Zustandswechsel und Audit-Nachweis dürfen nicht unabhängig voneinander dauerhaft werden.
            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await work(new SqlRunDatabase.Writer(connection, tx, ct)).ConfigureAwait(false);
                if (result) await tx.CommitAsync(ct).ConfigureAwait(false);
                else await tx.RollbackAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (StaleReleaseException) when (staleIsFalse)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
            catch (DbException) when (attempt < 3)
            {
                try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
                catch (DbException) { }
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);

    private static long? Lng(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(long? ticks) => ticks is { } t ? new DateTimeOffset(t, TimeSpan.Zero).ToLocalTime() : null;
}
