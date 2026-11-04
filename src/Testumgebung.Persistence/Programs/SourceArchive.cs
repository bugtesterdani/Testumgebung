using System.IO.Compression;

namespace Testumgebung.Persistence.Programs;

/// <summary>Deterministic archive of all exported files, including scripts and DLLs. No client metadata or machine paths.</summary>
public static class SourceArchive
{
    public static byte[] Build(SourceSnapshot snapshot)
    {
        var root = Path.Combine(snapshot.Directory, "export");
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfungen im Quellexport sind nicht erlaubt.");
            var entry = zip.CreateEntry(Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
            entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var output = entry.Open(); using var input = File.OpenRead(file); input.CopyTo(output);
        }
        return stream.ToArray();
    }
}
