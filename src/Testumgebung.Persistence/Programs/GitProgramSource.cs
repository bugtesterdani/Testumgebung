using System.IO.Compression;

namespace Testumgebung.Persistence.Programs;

/// <summary>Frischer Bare-Clone der konfigurierten Remote-Linie. Keine Hooks, kein Checkout und kein Vertrauen in lokale Commits.</summary>
public sealed class GitProgramSource(string executable = "git", bool allowLocalRepositories = false, Func<ProgramSource, SourceCredentials?>? credentials = null) : IProgramSourceProvider
{
    public string Id => "Git";
    public async Task<SourceSnapshot> ExportAsync(ProgramSource source, string? revision, CancellationToken ct = default)
    {
        SourcePaths.Validate(source, allowLocalRepositories);
        if (source.Line.Any(c => char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\') || source.Line.Contains("..")) throw new ArgumentException("Ungültiger Git-Branch.");
        if (!string.IsNullOrEmpty(revision) && !SourcePaths.GitHash().IsMatch(revision)) throw new ArgumentException("Vollständigen Git-Commit-Hash angeben.");
        var directory = SourcePaths.Temporary();
        try
        {
            var repository = Path.Combine(directory, "repository.git"); var export = Path.Combine(directory, "export");
            await Run(source, ["clone", "--bare", "--no-local", "--single-branch", "--branch", source.Line, "--", source.Repository, repository], ct);
            var head = await Run(source, ["--git-dir", repository, "rev-parse", "refs/heads/" + source.Line], ct);
            var commit = string.IsNullOrEmpty(revision) ? head : revision.ToLowerInvariant();
            await Run(source, ["--git-dir", repository, "merge-base", "--is-ancestor", commit, head], ct);
            var archive = Path.Combine(directory, "source.zip");
            await Run(source, ["--git-dir", repository, "archive", "--format=zip", "--output=" + archive, commit], ct);
            ZipFile.ExtractToDirectory(archive, export);
            var file = SourcePaths.ProjectFile(export, source.ProjectPath);
            return new(new(source, commit, source.Repository, SourcePaths.Hash(file)), file, directory);
        }
        catch { SourcePaths.Remove(directory); throw; }
    }
    private Task<string> Run(ProgramSource source, string[] arguments, CancellationToken ct) => SourceCommand.Run(executable, arguments, ct, source, credentials?.Invoke(source));
}
