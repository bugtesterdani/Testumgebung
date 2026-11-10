using Testumgebung.Persistence;
using Testumgebung.Persistence.Programs;
using Testumgebung.Persistence.Releases;

namespace Testumgebung.Presentation.Releases;

public sealed partial class ReleaseHub
{
    private string? _catalogStoreName;
    private void CheckProgramStore()
    {
        var name = Service.Store.Name;
        if (_catalogStoreName == name) return;
        _catalogStoreName = name; Catalog = null;
        lock (_gate) { _all = []; _released.Clear(); }
    }
    public ProgramCatalog? Catalog { get; private set; }
    private async Task RefreshCatalogAsync(CancellationToken ct)
    {
        if (Service.ProgramCatalog is not { } store) return;
        Catalog = await store.IsReadyAsync(ct).ConfigureAwait(false) ? await store.ReadAsync(ct).ConfigureAwait(false) : null;
    }
    public ResolvedProgram? ResolveStation(string station, string fallbackProgram)
        => Catalog is { } catalog ? ProgramAssignmentResolver.Resolve(catalog, All, station, fallbackProgram) : null;
    public IReadOnlyList<ReleaseInfo> ForStation(string station)
    {
        if (Catalog is null) return All;
        if (Catalog.Stations.Any(assignment => assignment.Station == station))
        {
            try { var chosen = ResolveStation(station, ""); return chosen is null ? [] : [chosen.Release]; }
            catch (InvalidOperationException) { return []; }
        }
        var available = All.Where(release => release.State == ReleaseState.Released).ToList();
        if (Catalog.DefaultReleaseId is not null)
        {
            try
            {
                if (ResolveStation(station, "") is { } standard)
                {
                    available.RemoveAll(release => release.ProjectName == standard.Release.ProjectName); available.Add(standard.Release);
                }
            }
            catch (InvalidOperationException) { return []; }
        }
        return available;
    }
    public async Task<(ReleaseInfo Info, TestProject Project)> OpenResolvedAsync(ReleaseInfo info, CancellationToken ct = default)
    {
        var opened = await Service.OpenAsync(info, ct).ConfigureAwait(false);
        if (!opened.IsIntact) throw new ReleasePackageException("Freigabepaket ist beschädigt: " + string.Join("; ", opened.Problems));
        var differences = await Service.VerifyEnvironmentAsync(info, ct).ConfigureAwait(false);
        if (differences.Count > 0) throw new ReleasePackageException(string.Join("; ", differences));
        return (info, opened.Project);
    }
}
