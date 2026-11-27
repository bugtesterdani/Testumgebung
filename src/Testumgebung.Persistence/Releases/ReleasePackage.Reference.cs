using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Testumgebung.Persistence.Releases;

public static partial class ReleasePackage
{
    public const string ReferenceFormat = "testumgebung.source-reference/1";

    /// <summary>Nur Manifest/Prüfsummen und Laufzeitnachweis speichern, niemals Projekt oder Quellarchiv.</summary>
    public static byte[] ToReference(byte[] package)
    {
        using var input = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(Read(input, "manifest.json"), Json)
            ?? throw new ReleasePackageException("Manifest fehlt.");
        if (manifest.Format != Format || manifest.Source is null)
            throw new ReleasePackageException("Quellreferenz benötigt ein versioniertes Release-Paket.");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTime;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
            Add("manifest.json", JsonSerializer.Serialize(manifest with { Format = ReferenceFormat }, Json));
            if (input.GetEntry("runtime.json") is not null) Add("runtime.json", Read(input, "runtime.json"));
        }
        return output.ToArray();
    }

    public static ReleaseManifest? ReadReference(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(Read(zip, "manifest.json"), Json)
                ?? throw new ReleasePackageException("Manifest fehlt.");
            if (manifest.Format != ReferenceFormat) return null;
            if (manifest.Source is null || manifest.Entries is null)
                throw new ReleasePackageException("Quellreferenz ist unvollständig.");
            return manifest;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new ReleasePackageException("Release-Manifest ist nicht lesbar.", ex);
        }
    }

    public static RuntimeSnapshot? ReferenceEnvironment(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.GetEntry("runtime.json") is null ? null
            : JsonSerializer.Deserialize<RuntimeSnapshot>(Read(zip, "runtime.json"), Json);
    }

    /// <summary>Auch lokale Cache-Inhalte gegen das unveränderliche SQL-Manifest prüfen.</summary>
    public static OpenedPackage OpenReferenced(byte[] reference, byte[] materialized, ProjectSerializer? serializer = null)
    {
        var expected = ReadReference(reference) ?? throw new ReleasePackageException("Keine Quellreferenz.");
        var opened = Open(materialized, serializer);
        if (!opened.IsIntact || opened.Manifest.Source != expected.Source ||
            opened.Manifest.ProjectName != expected.ProjectName || opened.Manifest.Version != expected.Version ||
            opened.Manifest.Entries.Count != expected.Entries.Count ||
            expected.Entries.Any(entry => !opened.Manifest.Entries.Contains(entry)))
            throw new ReleasePackageException("Quellinhalt stimmt nicht mit den freigegebenen Prüfsummen überein.");
        return opened with { Manifest = expected };
    }
}
