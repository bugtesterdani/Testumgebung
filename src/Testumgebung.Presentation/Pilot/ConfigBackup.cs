/**
 * @file
 * @brief Prüft und überträgt Konfigurationssicherungen mit Manifest und Integritätsinformationen.
 */

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testumgebung.Presentation.Pilot;

/// <summary>Eine sicherbare Konfigurationsdatei des Rechners.</summary>
public sealed record BackupSource(string Name, string Path, string Description);

public sealed record BackupFileEntry(string Name, string Description, string Sha256, long Size, bool ContainsProtectedSecrets);

public sealed class BackupManifest
{
    public string Format { get; set; } = ConfigBackup.FormatName;
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset Created { get; set; }
    public string Machine { get; set; } = "";
    public string User { get; set; } = "";
    public string Application { get; set; } = "";
    public List<BackupFileEntry> Files { get; set; } = [];
}

/// <summary>Inhalt einer Sicherung nach Prüfung (Hashes, JSON gültig).</summary>
public sealed record BackupInspection(BackupManifest Manifest, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool IsIntact => Problems.Count == 0;
}

public sealed record RestoreResult(IReadOnlyList<string> Restored, IReadOnlyList<string> PreviousCopies, IReadOnlyList<string> Warnings);

/// <summary>
/// Sicherung und Wiederherstellung der Rechnerkonfiguration als ZIP: einstellungen.json (Geheimnisse bleiben DPAPI-geschützt
/// und sind nur für dasselbe Windows-Konto auf demselben Rechner lesbar), zugang.json, pruefplaetze.json.
/// Wiederherstellen prüft Hashes und JSON, legt vorher Kopien der aktuellen Dateien an und schreibt atomar.
/// </summary>
public static class ConfigBackup
{
    public const string FormatName = "testumgebung.sicherung";
    public const string ManifestName = "sicherung.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Standardquellen des Rechners.</summary>
    public static IReadOnlyList<BackupSource> DefaultSources(string settingsPath, string accessPath, string stationsPath) =>
    [
        new("einstellungen.json", settingsPath, "Ergebnisordner, Datenbank, Outbox, MQTT (Benutzerprofil)"),
        new("zugang.json", accessPath, "Entra-App und Rechte je Konto (ProgramData)"),
        new("pruefplaetze.json", stationsPath, "Geräte, Prüfplätze, Zuordnung, Auswahl-/Auftragsablauf (ProgramData)"),
    ];

    public static BackupManifest Create(string zipPath, IEnumerable<BackupSource> sources, DateTimeOffset now)
    {
        var manifest = new BackupManifest
        {
            Created = now,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            Application = "Testumgebung " + (typeof(ConfigBackup).Assembly.GetName().Version?.ToString() ?? ""),
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(zipPath))!);
        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var source in sources)
            {
                if (!File.Exists(source.Path)) continue;
                var data = File.ReadAllBytes(source.Path);
                var entry = zip.CreateEntry(source.Name, CompressionLevel.Optimal);
                using (var s = entry.Open()) s.Write(data);
                manifest.Files.Add(new BackupFileEntry(source.Name, source.Description, Convert.ToHexStringLower(SHA256.HashData(data)), data.Length,
                    HasProtectedSecrets(data)));
            }
            var m = zip.CreateEntry(ManifestName);
            using var ms = m.Open();
            JsonSerializer.Serialize(ms, manifest, Json);
        }
        File.Move(temp, zipPath, overwrite: true);
        return manifest;
    }

    /// <summary>Sicherung prüfen, ohne etwas zu verändern.</summary>
    public static BackupInspection Inspect(string zipPath)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        BackupManifest? manifest;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            manifest = ReadManifest(zip);
            if (manifest is null) return new BackupInspection(new BackupManifest(), ["Keine Sicherung der Testumgebung (sicherung.json fehlt)."], []);
            foreach (var f in manifest.Files)
            {
                if (zip.GetEntry(f.Name) is not { } entry)
                {
                    problems.Add($"{f.Name}: fehlt im Archiv.");
                    continue;
                }
                var data = ReadAll(entry);
                if (Convert.ToHexStringLower(SHA256.HashData(data)) != f.Sha256) problems.Add($"{f.Name}: Prüfsumme stimmt nicht (beschädigt oder verändert).");
                else if (!IsJson(data)) problems.Add($"{f.Name}: kein gültiges JSON.");
                if (f.ContainsProtectedSecrets && (manifest.Machine != Environment.MachineName || manifest.User != Environment.UserName))
                    warnings.Add($"{f.Name}: Kennwörter/Verbindungsstring sind per DPAPI an {manifest.User}@{manifest.Machine} gebunden – nach dem Wiederherstellen unter „Einstellungen“ neu eingeben.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return new BackupInspection(new BackupManifest(), [$"Sicherung nicht lesbar: {ex.Message}"], []);
        }
        return new BackupInspection(manifest, problems, warnings);
    }

    /// <summary>
    /// Ausgewählte Dateien wiederherstellen (Name → Zielpfad). Nur intakte Sicherungen; vorhandene Dateien werden vorher als
    /// „*.vor-wiederherstellung-JJJJMMTT-HHMMSS“ daneben abgelegt.
    /// </summary>
    public static RestoreResult Restore(string zipPath, IReadOnlyDictionary<string, string> targets, DateTimeOffset now)
    {
        var inspection = Inspect(zipPath);
        if (!inspection.IsIntact) throw new InvalidDataException(string.Join(" ", inspection.Problems));
        var restored = new List<string>();
        var copies = new List<string>();
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var f in inspection.Manifest.Files)
        {
            if (!targets.TryGetValue(f.Name, out var target)) continue;
            var data = ReadAll(zip.GetEntry(f.Name)!);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                var copy = $"{target}.vor-wiederherstellung-{now.LocalDateTime:yyyyMMdd-HHmmss}";
                File.Copy(target, copy, overwrite: true);
                copies.Add(copy);
            }
            var temp = target + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, target, overwrite: true);
            restored.Add(target);
        }
        return new RestoreResult(restored, copies, inspection.Warnings);
    }

    private static BackupManifest? ReadManifest(ZipArchive zip)
    {
        if (zip.GetEntry(ManifestName) is not { } entry) return null;
        var manifest = JsonSerializer.Deserialize<BackupManifest>(ReadAll(entry), Json);
        return manifest is { Format: FormatName } ? manifest : null;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static bool IsJson(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasProtectedSecrets(byte[] data)
    {
        try
        {
            var node = JsonNode.Parse(data);
            return node?["database"]?["protectedConnectionString"] is not null || node?["mqtt"]?["protectedPassword"] is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
