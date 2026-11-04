using System.Text.Json;

namespace Testumgebung.Persistence.Programs;

public sealed record ProgramSourceClientSettings(string Git = "git", string Svn = "svn")
{
    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "versionsverwaltungsclients.json");
    public static ProgramSourceClientSettings Load(string? file = null)
        => File.Exists(file ?? DefaultFile) ? JsonSerializer.Deserialize<ProgramSourceClientSettings>(File.ReadAllText(file ?? DefaultFile)) ?? new() : new();
    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(this));
    }
    public ProgramSourceProviders Providers() => new([new GitProgramSource(Git), new SvnProgramSource(Svn)]);
}
