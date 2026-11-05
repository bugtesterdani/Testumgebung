using Testumgebung.Contracts;
using Testumgebung.Persistence.Programs;

namespace Testumgebung.Persistence.Releases;

public sealed partial class ReleaseService
{
    public IProgramCatalogStore? ProgramCatalog { get; set; }
    public ProgramSourceProviders SourceProviders { get; set; } = ProgramSourceProviders.Default();

    public async Task<ReleaseInfo> CreateRemoteDraftAsync(ProgramSource source, string? revision, string version, Actor actor, CancellationToken ct = default)
    {
        Require(actor, UserRights.Develop, "Remote-Entwurf erstellen");
        using var snapshot = await SourceProviders.Get(source.Provider).ExportAsync(source, revision, ct).ConfigureAwait(false);
        var project = _serializer.Load(snapshot.ProjectFile);
        if (project.Definition.Name != source.Name) throw new ReleaseRuleException("Programmname in der Remote-Datei stimmt nicht mit der konfigurierten Quelle überein.");
        return await CreateDraftCoreAsync(project, version, actor, snapshot.Version, SourceArchive.Build(snapshot), ct).ConfigureAwait(false);
    }

    private async Task<byte[]> PackageBytesAsync(ReleaseInfo info, CancellationToken ct)
    {
        try { return await Store.GetPackageAsync(info.ReleaseId, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
        {
            if (_verifiedPackages.TryGetValue(Store.Name + "\n" + info.PackageSha256, out var cached)) return cached;
            throw;
        }
    }

    private async Task VerifyRemoteReleaseAsync(ReleaseInfo info, OpenedPackage opened, CancellationToken ct)
    {
        var source = opened.Manifest.Source;
        if (source is null)
        {
            if (ProgramCatalog is not null && await ProgramCatalog.IsReadyAsync(ct).ConfigureAwait(false))
            {
                throw new ReleaseRuleException("Programmkatalog ist aktiviert. Endgültige Freigabe erfordert einen Remote-Entwurf aus einer gespeicherten Git-/SVN-Quelle.");
            }
            return;
        }
        using var snapshot = await SourceProviders.Get(source.Source.Provider).ExportAsync(source.Source, source.Revision, ct).ConfigureAwait(false);
        if (snapshot.Version != source) throw new ReleaseRuleException("Remote-Version oder Repository-Identität weicht vom geprüften Paket ab.");
        var expected = opened.Manifest.Entries.SingleOrDefault(e => e.Area == "Programmquelle" && e.Name == "source.zip");
        if (expected?.Sha256 != ReleasePackage.Sha256(SourceArchive.Build(snapshot))) throw new ReleaseRuleException("Remote-Dateien weichen vom geprüften Quellarchiv ab.");
    }
}
