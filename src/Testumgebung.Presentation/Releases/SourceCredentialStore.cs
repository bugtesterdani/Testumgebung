using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testumgebung.Persistence.Programs;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Presentation.Releases;

/// <summary>Per-Windows-user profiles, separate from the shared program catalog and from release packages.</summary>
public sealed class SourceCredentialStore(string? file = null, ISecretProtector? protector = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceCredentials> _session = [];
    private sealed record Payload(string Key, SourceLoginMode Mode, string Username, string Secret);
    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testumgebung", "quellen-zugang.json");
    private static string Key(ProgramSource source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id + "\n" + source.Provider + "\n" + source.Repository)));
    private Dictionary<string, string> Read() => file is not null && File.Exists(file)
        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? throw new IOException("Lokale Zugangsprofile sind beschädigt.") : [];
    public SourceCredentials? Get(ProgramSource source)
    {
        lock (_gate)
        {
            var key = Key(source);
            if (_session.TryGetValue(key, out var value)) return value;
            if (!Read().TryGetValue(key, out var stored)) return null;
            var plain = protector?.Unprotect(stored) ?? throw new IOException("Lokale Zugangsdaten können unter diesem Windows-Konto nicht entschlüsselt werden. Bitte neu hinterlegen.");
            var entry = JsonSerializer.Deserialize<Payload>(plain) ?? throw new IOException("Lokales Zugangsprofil ist beschädigt.");
            if (entry.Key != key) throw new IOException("Lokales Zugangsprofil gehört zu einer anderen Programmquelle.");
            return new(entry.Mode, entry.Username, entry.Secret);
        }
    }
    public bool IsRemembered(ProgramSource source) { lock (_gate) return !_session.ContainsKey(Key(source)) && Read().ContainsKey(Key(source)); }
    public void Save(ProgramSource source, SourceCredentials credentials, bool remember)
    {
        credentials.Validate(source);
        lock (_gate)
        {
            var key = Key(source); var entries = Read();
            entries.Remove(key);
            if (remember)
            {
                if (file is null || protector is null) throw new IOException("Geschützter lokaler Speicher ist nicht eingerichtet.");
                entries[key] = protector.Protect(JsonSerializer.Serialize(new Payload(key, credentials.Mode, credentials.Username, credentials.Secret)));
            }
            Write(entries);
            _session[key] = credentials;
            if (remember) _session.Remove(key);
        }
    }
    public void Remove(ProgramSource source)
    {
        lock (_gate) { var key = Key(source); var entries = Read(); entries.Remove(key); Write(entries); _session.Remove(key); }
    }
    private void Write(Dictionary<string, string> entries)
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries)); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}