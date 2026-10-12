using System.Security.Cryptography;
using System.Text;

namespace Testumgebung.Persistence.Releases;

/// <summary>Lebenszyklus (Abschnitt 10): Entwurf → Entwicklungsprüfung → ausdrückliche Freigabe; Vorgänger werden archiviert.</summary>
public enum ReleaseState
{
    Draft,
    Review,
    Released,
    Archived,
}

public static class ReleaseStateText
{
    public static string Of(ReleaseState s) => s switch
    {
        ReleaseState.Draft => "Entwurf",
        ReleaseState.Review => "Entwicklungsprüfung",
        ReleaseState.Released => "Freigegeben",
        ReleaseState.Archived => "Archiviert",
        _ => s.ToString(),
    };

    public static string Code(ReleaseState s) => s switch
    {
        ReleaseState.Draft => "draft",
        ReleaseState.Review => "review",
        ReleaseState.Released => "released",
        _ => "archived",
    };

    public static ReleaseState Parse(string code) => code switch
    {
        "draft" => ReleaseState.Draft,
        "review" => ReleaseState.Review,
        "released" => ReleaseState.Released,
        _ => ReleaseState.Archived,
    };
}

/// <summary>Metadaten eines Release-Pakets. Der Paketinhalt selbst ist unveränderlich (SHA-256 <see cref="PackageSha256"/>).</summary>
public sealed record ReleaseInfo(
    string ReleaseId,
    string ProjectName,
    string Version,
    ReleaseState State,
    string PackageSha256,
    long PackageSize,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? ReviewedBy = null,
    DateTimeOffset? ReviewedAt = null,
    string? ReleasedBy = null,
    DateTimeOffset? ReleasedAt = null,
    DateTimeOffset? ArchivedAt = null,
    string? ReviewReport = null)
{
    public string StateText => ReleaseStateText.Of(State);

    public string ShortHash => PackageSha256.Length > 12 ? PackageSha256[..12] + "…" : PackageSha256;

    /// <summary>Zeitpunkt der letzten Zustandsänderung (Anzeige in der Liste).</summary>
    public DateTimeOffset LastChange => ArchivedAt ?? ReleasedAt ?? ReviewedAt ?? CreatedAt;
}

/// <summary>
/// Unveränderlicher Audit-Eintrag. Jeder Eintrag enthält den Hash des Vorgängers (Hash-Kette),
/// nachträgliche Änderungen oder Löschungen fallen bei <see cref="AuditChain.Verify"/> auf.
/// </summary>
public sealed record AuditEntry(
    long Sequence,
    DateTimeOffset Time,
    string User,
    string Action,
    string Details,
    string? ReleaseId,
    string PreviousHash,
    string Hash);

public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(long seq, DateTimeOffset time, string user, string action, string details, string? releaseId, string previousHash)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{seq}\n{time.UtcTicks}\n{user}\n{action}\n{details}\n{releaseId}\n{previousHash}")));

    public static AuditEntry Create(long seq, DateTimeOffset time, string user, string action, string details, string? releaseId, string previousHash)
        => new(seq, time, user, action, details, releaseId, previousHash, ComputeHash(seq, time, user, action, details, releaseId, previousHash));

    /// <summary>Prüft Reihenfolge, Verkettung und Hashes. Liefert null bei Unversehrtheit, sonst die erste Abweichung.</summary>
    public static string? Verify(IReadOnlyList<AuditEntry> entries)
    {
        var previous = Genesis;
        long expected = 1;
        foreach (var e in entries.OrderBy(e => e.Sequence))
        {
            if (e.Sequence != expected) return $"Eintrag {expected} fehlt (gefunden: {e.Sequence}).";
            if (e.PreviousHash != previous) return $"Eintrag {e.Sequence}: Verkettung unterbrochen.";
            if (ComputeHash(e.Sequence, e.Time, e.User, e.Action, e.Details, e.ReleaseId, e.PreviousHash) != e.Hash)
                return $"Eintrag {e.Sequence}: Inhalt verändert.";
            previous = e.Hash;
            expected++;
        }
        return null;
    }
}

/// <summary>Ablage der Release-Pakete und des Audit-Logs (Datenbank oder Ordner).</summary>
public interface IReleaseStore
{
    string Name { get; }

    Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<byte[]> GetPackageAsync(string releaseId, CancellationToken cancellationToken = default);

    /// <summary>Neues Paket als Entwurf anlegen (Projektname + Version eindeutig) und Audit-Eintrag schreiben – atomar.</summary>
    Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Zustandswechsel atomar: gelingt nur, wenn der Release noch im erwarteten Zustand ist. Optional werden andere
    /// Releases desselben Projekts archiviert. Audit-Eintrag in derselben Transaktion. Das Paket wird nie geändert.
    /// </summary>
    Task<bool> TransitionAsync(ReleaseTransition transition, AuditDraft audit, CancellationToken cancellationToken = default);

    /// <summary>Nur Audit-Eintrag (z. B. Anmeldung, Run-Patch).</summary>
    Task AppendAuditAsync(AuditDraft audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditDraft(DateTimeOffset Time, string User, string Action, string Details, string? ReleaseId = null);

public sealed record ReleaseTransition(
    string ReleaseId,
    ReleaseState From,
    ReleaseState To,
    string User,
    DateTimeOffset Time,
    string? ReviewReport = null,
    bool ArchiveOtherReleased = false);
