using Testumgebung.Contracts;
using Testumgebung.Persistence.Programs;

namespace Testumgebung.Persistence.Releases;

public sealed partial class ReleaseService
{
    public IProgramCatalogStore? ProgramCatalog { get; set; }
    public ProgramSourceProviders SourceProviders { get; set; } = ProgramSourceProviders.Default();
    /// <summary>Lokaler Cache für geprüfte Inhalte; wird nie in der Prüfdatenbank gespeichert.</summary>
    public string SourceCacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "SourceReleaseCache");

    private async Task<OpenedPackage> OpenReferenceAsync(byte[] reference, ReleaseInfo info, CancellationToken ct)
    {
        var manifest = ReleasePackage.ReadReference(reference)!;
        var source = manifest.Source!;
        // Nicht aus externen IDs/Pfaden ableiten: der bereits geprüfte SHA-256 identifiziert den Inhalt.
        var cacheFile = Path.Combine(SourceCacheDirectory, ReleasePackage.Sha256(reference) + ".zip");
        try
        {
            if (File.Exists(cacheFile))
                return ReleasePackage.OpenReferenced(reference, await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false), _serializer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ReleasePackageException or System.Text.Json.JsonException)
        {
            // Ungültiger/unlesbarer Cache darf nur durch einen geprüften Remote-Export ersetzt werden.
        }

        byte[] materialized;
        OpenedPackage opened;
        try
        {
            using var snapshot = await SourceProviders.Get(source.Source.Provider)
                .ExportAsync(source.Source, source.Revision, ct).ConfigureAwait(false);
            if (snapshot.Version != source)
                throw new ReleasePackageException("Repository-Identität, Revision oder Projekt-Hash weicht von der Freigabe ab.");
            var project = _serializer.Load(snapshot.ProjectFile);
            project.Definition.Version = manifest.Version;
            (materialized, _) = ReleasePackage.Build(project, manifest.CreatedBy, manifest.CreatedAt, _serializer,
                ReleasePackage.ReferenceEnvironment(reference), source, SourceArchive.Build(snapshot));
            opened = ReleasePackage.OpenReferenced(reference, materialized, _serializer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ReleasePackageException($"Version {info.Version} konnte nicht aus der festen Quellrevision geladen werden. " +
                "Repository nicht erreichbar oder Inhalt ungültig; kein gültiger lokaler Cache vorhanden. " + ex.Message, ex);
        }

        string? temporary = null;
        try
        {
            Directory.CreateDirectory(SourceCacheDirectory);
            temporary = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, materialized, ct).ConfigureAwait(false);
            File.Move(temporary, cacheFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ein nicht beschreibbarer Cache verhindert keinen bereits verifizierten Online-Start.
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return opened;
    }

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
        var expected = opened.Manifest.Entries.SingleOrDefault(manifestEntry => manifestEntry.Area == "Programmquelle" && manifestEntry.Name == "source.zip");
        if (expected?.Sha256 != ReleasePackage.Sha256(SourceArchive.Build(snapshot))) throw new ReleaseRuleException("Remote-Dateien weichen vom geprüften Quellarchiv ab.");
    }
}
