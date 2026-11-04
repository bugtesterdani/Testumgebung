using System.Text.Json;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Persistence.Programs;

/// <summary>AutoUpdate=false requires an exact released package; a moving branch alone is not a pin.</summary>
public sealed record ProgramAssignment(string Station, string SourceId, bool AutoUpdate = true, string? ReleaseId = null);
public sealed record ProgramCatalog(long Generation, IReadOnlyList<ProgramSource> Sources, IReadOnlyList<ProgramAssignment> Stations, string? DefaultReleaseId = null)
{
    public static ProgramCatalog Empty => new(0, [], []);
    public ProgramSource? Source(string id) => Sources.FirstOrDefault(s => s.Id == id);
}
public interface IProgramCatalogStore
{
    Task<bool> IsReadyAsync(CancellationToken ct = default);
    Task UpgradeAsync(CancellationToken ct = default);
    Task<ProgramCatalog> ReadAsync(CancellationToken ct = default);
    /// <summary>Optimistic concurrency: stale edits never overwrite changes from another station.</summary>
    Task SaveAsync(ProgramCatalog catalog, CancellationToken ct = default);
}
public sealed class SwitchableProgramCatalogStore(IProgramCatalogStore initial) : IProgramCatalogStore
{
    public IProgramCatalogStore Inner { get; set; } = initial;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Inner.IsReadyAsync(ct);
    public Task UpgradeAsync(CancellationToken ct = default) => Inner.UpgradeAsync(ct);
    public Task<ProgramCatalog> ReadAsync(CancellationToken ct = default) => Inner.ReadAsync(ct);
    public Task SaveAsync(ProgramCatalog catalog, CancellationToken ct = default) => Inner.SaveAsync(catalog, ct);
}
/// <summary>Same semantics without SQL; exclusive lock plus atomic replacement also works across application instances.</summary>
public sealed class FolderProgramCatalogStore(string directory) : IProgramCatalogStore
{
    private string FilePath => Path.Combine(directory, "programmquellen.json");
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(File.Exists(FilePath));
    public async Task UpgradeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        using var gate = Lock();
        if (!File.Exists(FilePath)) await Write(ProgramCatalog.Empty, ct);
    }
    public async Task<ProgramCatalog> ReadAsync(CancellationToken ct = default)
        => JsonSerializer.Deserialize<ProgramCatalog>(await File.ReadAllTextAsync(FilePath, ct)) ?? throw new IOException("Programmkatalog ist leer.");
    public async Task SaveAsync(ProgramCatalog catalog, CancellationToken ct = default)
    {
        using var gate = Lock();
        var current = await ReadAsync(ct);
        if (current.Generation != catalog.Generation) throw new InvalidOperationException("Programmquellen wurden an einem anderen Platz geändert. Neu laden.");
        await Write(catalog with { Generation = current.Generation + 1 }, ct);
    }
    private FileStream Lock() => new(Path.Combine(directory, "programmquellen.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    private async Task Write(ProgramCatalog catalog, CancellationToken ct)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(catalog), ct); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record ResolvedProgram(ReleaseInfo Release, string? Warning);
public static class ProgramAssignmentResolver
{
    public static ResolvedProgram? Resolve(ProgramCatalog catalog, IEnumerable<ReleaseInfo> releases, string station, string fallbackProgram)
    {
        var available = releases.Where(r => r.State is ReleaseState.Released or ReleaseState.Archived && r.ReleasedAt is not null).ToArray();
        var assignment = catalog.Stations.FirstOrDefault(s => s.Station == station);
        if (assignment is not null) return Assigned(catalog, available, assignment);
        if (catalog.DefaultReleaseId is { } defaultId)
        {
            var standard = available.SingleOrDefault(r => r.ReleaseId == defaultId) ?? throw new InvalidOperationException("Standardprogramm ist nicht freigegeben oder fehlt.");
            // A standard identifies the program/line, not a permanent global version pin.
            var latest = Candidates(available, standard.Source).Where(r => r.ProjectName == standard.ProjectName).OrderByDescending(r => r.ReleasedAt).FirstOrDefault();
            return new(latest ?? standard, null);
        }
        var fallback = available.Where(r => r.ProjectName == fallbackProgram && r.State == ReleaseState.Released).OrderByDescending(r => r.ReleasedAt).FirstOrDefault();
        return fallback is null ? null : new(fallback, null);
    }
    private static ResolvedProgram Assigned(ProgramCatalog catalog, ReleaseInfo[] releases, ProgramAssignment assignment)
    {
        var source = catalog.Source(assignment.SourceId) ?? throw new InvalidOperationException("Programmquelle des Prüfplatzes fehlt.");
        var candidates = releases.Where(r => r.Source?.Source == source).OrderByDescending(r => r.ReleasedAt).ToArray();
        var latest = candidates.FirstOrDefault() ?? throw new InvalidOperationException("Keine freigegebene Version für die zugeordnete Quelle vorhanden.");
        if (assignment.AutoUpdate) return new(latest, null);
        var pinned = candidates.FirstOrDefault(r => r.ReleaseId == assignment.ReleaseId) ?? throw new InvalidOperationException("Festgesetzte Version fehlt oder gehört nicht zur zugeordneten Quelle.");
        return new(pinned, latest.ReleaseId == pinned.ReleaseId ? null : $"Prüfplatz verwendet die festgesetzte Version {pinned.Version}. Neuere Freigabe {latest.Version} verfügbar; dieser Platz prüft möglicherweise mit einer veralteten Version.");
    }
    private static IEnumerable<ReleaseInfo> Candidates(IEnumerable<ReleaseInfo> releases, SourceVersion? source)
        => source is null ? releases : releases.Where(r => r.Source?.Source == source.Source);
}
