/**
 * @file
 * @brief Erfasst externe Laufzeitdateien als Nachweise für reproduzierbare Release-Prüfungen.
 */

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Testumgebung.Persistence.Releases;

public sealed record ExternalRuntimeFile(string Name, string Path);

/// <summary>Herstellerdateien außerhalb der Anwendung: automatische bekannte Treiber und administrative Ergänzungen.</summary>
public static class ExternalRuntimeInventory
{
    public static string ConfigurationPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Testumgebung", "laufzeitdateien.json");

    public static async Task<IReadOnlyList<RuntimeComponent>> CaptureAsync(CancellationToken ct, string? configurationPath = null)
    {
        var files = new List<ExternalRuntimeFile>();
        AddKnownRuntimeFiles(files);
        var config = configurationPath ?? ConfigurationPath;
        if (File.Exists(config))
        {
            var configured = JsonSerializer.Deserialize<List<ExternalRuntimeFile>>(await File.ReadAllTextAsync(config, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Laufzeitdateiliste ist leer.");
            foreach (var file in configured)
            {
                if (string.IsNullOrWhiteSpace(file.Name) || !Path.IsPathFullyQualified(file.Path))
                    throw new InvalidDataException("Laufzeitdateien benötigen eindeutigen Namen und absoluten Pfad.");
                files.Add(file with { Name = "Konfiguriert/" + file.Name });
            }
        }
        if (files.GroupBy(f => f.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new InvalidDataException("Doppelte Namen in der Laufzeitdateiliste.");
        var result = new List<RuntimeComponent>();
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            // Fehlende konfigurierte Dateien blockieren die Freigabe und werden nicht still ignoriert.
            using var stream = File.OpenRead(file.Path);
            result.Add(new("Extern/" + file.Name, FileVersionInfo.GetVersionInfo(file.Path).FileVersion ?? "",
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct))));
        }
        return result;
    }
    private static void AddKnownRuntimeFiles(List<ExternalRuntimeFile> files)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "nicaiu.dll", "visa64.dll", "canlib32.dll", "vxlapi64.dll" })
            {
                var path = Path.Combine(Environment.SystemDirectory, name);
                if (File.Exists(path)) files.Add(new("Treiber/" + name, path));
            }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
            {
                var directory = Path.Combine(root, "National Instruments", "Shared", "LabVIEW Run-Time");
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "lvrt.dll", SearchOption.AllDirectories))
                    files.Add(new("LabVIEW/" + (root.EndsWith("(x86)", StringComparison.Ordinal) ? "x86/" : "x64/")
                        + Path.GetRelativePath(directory, file).Replace('\\', '/'), file));
            }
        }
    }

}
