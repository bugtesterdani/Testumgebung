using System.Xml.Linq;

namespace Testumgebung.Persistence.Programs;

/// <summary>SVN-Export mit fester operativer und Peg-Revision. Externals werden bewusst nicht implizit nachgeladen.</summary>
public sealed class SvnProgramSource(string executable = "svn", bool allowLocalRepositories = false, Func<ProgramSource, SourceCredentials?>? credentials = null) : IProgramSourceProvider
{
    public string Id => "SVN";
    public async Task<SourceSnapshot> ExportAsync(ProgramSource source, string? revision, CancellationToken ct = default)
    {
        SourcePaths.Validate(source, allowLocalRepositories); SourcePaths.Relative(source.Line);
        if (!string.IsNullOrEmpty(revision) && (!long.TryParse(revision, out var number) || number <= 0)) throw new ArgumentException("Positive SVN-Revision angeben.");
        var url = source.Repository.TrimEnd('/') + "/" + string.Join("/", source.Line.Split('/').Select(Uri.EscapeDataString));
        var requested = string.IsNullOrEmpty(revision) ? "HEAD" : revision;
        var xml = XDocument.Parse(await Run(source, ["info", "--xml", "--non-interactive", "-r", requested, url + "@" + requested], ct));
        var entry = xml.Root?.Element("entry") ?? throw new IOException("SVN-Information unvollständig.");
        var resolved = entry.Attribute("revision")?.Value ?? throw new IOException("SVN-Revision fehlt.");
        var identity = entry.Element("repository")?.Element("uuid")?.Value ?? throw new IOException("SVN-Repository-UUID fehlt.");
        var directory = SourcePaths.Temporary();
        try
        {
            var export = Path.Combine(directory, "export");
            await Run(source, ["export", "--non-interactive", "--ignore-externals", "-r", resolved, url + "@" + resolved, export], ct);
            var file = SourcePaths.ProjectFile(export, source.ProjectPath);
            return new(new(source, resolved, identity, SourcePaths.Hash(file)), file, directory);
        }
        catch { SourcePaths.Remove(directory); throw; }
    }
    private Task<string> Run(ProgramSource source, string[] arguments, CancellationToken ct) => SourceCommand.Run(executable, arguments, ct, source, credentials?.Invoke(source));
}
