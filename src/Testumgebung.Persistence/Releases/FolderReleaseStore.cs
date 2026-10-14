/**
 * @file
 * @brief Bewahrt Pakete, Zustände und Audit-Daten in einer Ordnerablage statt einer SQL-Datenbank.
 */

using System.Text.Json;
using System.Text.Json.Serialization;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Releases;

/// <summary>
/// Release-Ablage als Ordner (simulierte bzw. keine Datenbank): je Release <c>&lt;id&gt;.tpkg</c> (nie überschrieben)
/// und <c>&lt;id&gt;.json</c> (Zustand), dazu <c>audit.jsonl</c> (nur anhängen, Hash-Kette). Prozessweit serialisiert.
/// </summary>
public sealed class FolderReleaseStore(string folder) : IReleaseStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Folder { get; } = folder;
    public string Name => $"Ordner ({Folder})";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "Releases");

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return LoadAll(); }
        finally { Gate.Release(); }
    }

    public Task<byte[]> GetPackageAsync(string releaseId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Folder, Safe(releaseId) + ".tpkg");
        if (!File.Exists(path)) throw new ReleasePackageException($"Paket {releaseId} nicht gefunden.");
        return File.ReadAllBytesAsync(path, cancellationToken);
    }

    public async Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (LoadAll().Any(r => r.ProjectName == info.ProjectName && r.Version == info.Version))
                throw new InvalidOperationException($"Version {info.Version} von '{info.ProjectName}' existiert bereits.");
            ResultFileStore.WriteAtomic(Folder, Safe(info.ReleaseId) + ".tpkg", package); // nie überschreiben
            WriteInfo(info);
            Append(audit);
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> TransitionAsync(ReleaseTransition t, AuditDraft audit, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = LoadAll();
            var r = all.FirstOrDefault(x => x.ReleaseId == t.ReleaseId);
            if (r is null || r.State != t.From) return false;
            if (t.ArchiveOtherReleased)
                foreach (var other in all.Where(x => x.ProjectName == r.ProjectName && x.State == ReleaseState.Released && x.ReleaseId != r.ReleaseId))
                    WriteInfo(other with { State = ReleaseState.Archived, ArchivedAt = t.Time });
            WriteInfo(Apply(r, t));
            Append(audit);
            return true;
        }
        finally { Gate.Release(); }
    }

    public async Task AppendAuditAsync(AuditDraft audit, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Append(audit); }
        finally { Gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ReadAudit(); }
        finally { Gate.Release(); }
    }

    internal static ReleaseInfo Apply(ReleaseInfo r, ReleaseTransition t) => t.To switch
    {
        ReleaseState.Review => r with { State = t.To, ReviewedBy = t.User, ReviewedAt = t.Time, ReviewReport = t.ReviewReport },
        ReleaseState.Released => r with { State = t.To, ReleasedBy = t.User, ReleasedAt = t.Time },
        ReleaseState.Archived => r with { State = t.To, ArchivedAt = t.Time },
        _ => r with { State = t.To },
    };

    private List<ReleaseInfo> LoadAll()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*.json")
            .Select(f => JsonSerializer.Deserialize<ReleaseInfo>(File.ReadAllText(f), Json)!)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    private void WriteInfo(ReleaseInfo info)
        => ResultFileStore.WriteAtomic(Folder, Safe(info.ReleaseId) + ".json", JsonSerializer.SerializeToUtf8Bytes(info, Json), overwrite: true);

    private List<AuditEntry> ReadAudit()
    {
        var path = Path.Combine(Folder, "audit.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AuditEntry>(l, Json)!).ToList();
    }

    private void Append(AuditDraft d)
    {
        Directory.CreateDirectory(Folder);
        var existing = ReadAudit();
        var last = existing.LastOrDefault();
        var entry = AuditChain.Create((last?.Sequence ?? 0) + 1, d.Time, d.User, d.Action, d.Details, d.ReleaseId, last?.Hash ?? AuditChain.Genesis);
        var line = JsonSerializer.Serialize(entry, new JsonSerializerOptions(Json) { WriteIndented = false }) + "\n";
        using var fs = new FileStream(Path.Combine(Folder, "audit.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read);
        fs.Write(System.Text.Encoding.UTF8.GetBytes(line));
        fs.Flush(flushToDisk: true);
    }

    private static string Safe(string id) => new(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}

/// <summary>Austauschbare Release-Ablage (Datenbank ↔ Ordner, je nach Einstellung).</summary>
public sealed class SwitchableReleaseStore(IReleaseStore initial) : IReleaseStore
{
    private volatile IReleaseStore _inner = initial;

    public IReleaseStore Inner
    {
        get => _inner;
        set => _inner = value;
    }

    public string Name => _inner.Name;
    public Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);
    public Task<byte[]> GetPackageAsync(string releaseId, CancellationToken ct = default) => _inner.GetPackageAsync(releaseId, ct);
    public Task CreateAsync(ReleaseInfo info, byte[] package, AuditDraft audit, CancellationToken ct = default) => _inner.CreateAsync(info, package, audit, ct);
    public Task<bool> TransitionAsync(ReleaseTransition t, AuditDraft audit, CancellationToken ct = default) => _inner.TransitionAsync(t, audit, ct);
    public Task AppendAuditAsync(AuditDraft audit, CancellationToken ct = default) => _inner.AppendAuditAsync(audit, ct);
    public Task<IReadOnlyList<AuditEntry>> ReadAuditAsync(CancellationToken ct = default) => _inner.ReadAuditAsync(ct);
}
