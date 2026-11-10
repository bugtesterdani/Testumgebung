using System.Text.RegularExpressions;

namespace Testumgebung.Persistence.Programs;

/// <summary>Zentrale Quelle ohne lokale Arbeitskopien oder Zugangsdaten. Line ist Git-Branch oder SVN-Pfad relativ zum Repository.</summary>
public sealed record ProgramSource(string Id, string Name, string Provider, string Repository, string Line, string ProjectPath)
{
    public string Title => $"{Name} · {Provider} · {Line}";
}
/// <summary>Unveränderlicher Remote-Nachweis. SVN benötigt neben Revision auch Repository-UUID und Pfad.</summary>
public sealed record SourceVersion(ProgramSource Source, string Revision, string RepositoryIdentity, string ProjectSha256);
public sealed record SourceSnapshot(SourceVersion Version, string ProjectFile, string Directory) : IDisposable
{
    public void Dispose() => SourcePaths.Remove(Directory);
}
/// <summary>Ein Provider muss eine exakte Remote-Version exportieren; lokale Arbeitskopien sind niemals Freigabequellen.</summary>
public interface IProgramSourceProvider
{
    string Id { get; }
    Task<SourceSnapshot> ExportAsync(ProgramSource source, string? revision, CancellationToken ct = default);
}

public sealed class ProgramSourceProviders(IEnumerable<IProgramSourceProvider> providers)
{
    private readonly Dictionary<string, IProgramSourceProvider> _providers = providers.ToDictionary(iProgramSourceProvider => iProgramSourceProvider.Id, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Names => _providers.Keys.ToArray();
    public IProgramSourceProvider Get(string id) => _providers.GetValueOrDefault(id)
        ?? throw new InvalidOperationException($"Versionsverwaltung '{id}' ist nicht eingerichtet.");
    public static ProgramSourceProviders Default() => new([new GitProgramSource(), new SvnProgramSource()]);
}

internal static partial class SourcePaths
{
    public static void Validate(ProgramSource source, bool allowLocalRepositories = false)
    {
        if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Name)) throw new ArgumentException("Quellen-ID und Programmname angeben.");
        if (string.IsNullOrWhiteSpace(source.Line) || source.Line.StartsWith('-')) throw new ArgumentException("Entwicklungslinie angeben.");
        ValidateRepository(source.Repository, allowLocalRepositories);
        Relative(source.ProjectPath);
    }
    private static void ValidateRepository(string repository, bool allowLocalRepositories)
    {
        // Credentials belong to the operating system's credential helper, never a shared database or process argument.
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh" or "svn" or "svn+ssh" or "file") || uri.UserInfo.Length > 0)
            throw new ArgumentException("Repository als HTTPS-, SSH-, SVN- oder file-URL ohne Zugangsdaten angeben.");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("Repository-URL ohne Query-Parameter oder Fragment angeben.");
        if (uri.IsFile && !uri.IsUnc && !allowLocalRepositories) throw new ArgumentException("Eine lokale Repository-URL ist keine zentrale Remote-Quelle. Netzwerk-URL oder UNC-Freigabe verwenden.");

    }
    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') || path.Replace('\\','/').Split('/').Any(pathSegment => pathSegment is ".." or "." or ""))
            throw new ArgumentException("Projektpfad muss ein relativer Dateipfad innerhalb des Repositorys sein.");
        return path.Replace('\\', '/');
    }
    public static string ProjectFile(string directory, string path)
    {
        var file = Path.Combine(directory, Relative(path));
        if (!File.Exists(file)) throw new FileNotFoundException("Projektdatei fehlt in der Remote-Version.", path);
        for (var item = new FileInfo(file) as FileSystemInfo; item is not null && item.FullName != directory; item = new DirectoryInfo(Path.GetDirectoryName(item.FullName)!))
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfungen sind als Projektquelle nicht zulässig.");
        return file;
    }
    public static string Temporary() { var path = Path.Combine(Path.GetTempPath(), "Testumgebung-Sources", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    public static void Remove(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, true);
    }
    public static string Hash(string file) => Releases.ReleasePackage.Sha256(File.ReadAllBytes(file));
    [GeneratedRegex("^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")] public static partial Regex GitHash();
}

/// <summary>Shared validation for UI and future provider configuration APIs.</summary>
public static class ProgramSourceValidation
{
    public static void Validate(ProgramSource source) => SourcePaths.Validate(source);
}
