/**
 * @file
 * @brief Hält den zum Release gehörenden Stand der Ausführungsumgebung fest.
 */

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Testumgebung.Domain.Code;

namespace Testumgebung.Persistence.Releases;

public sealed record RuntimeComponent(string Name, string Version, string? Sha256 = null);

/// <summary>Im Paket festgehaltene Ausführungsumgebung; Abweichungen erfordern eine erneute Entwicklungsprüfung.</summary>
public sealed record RuntimeSnapshot(IReadOnlyList<RuntimeComponent> Components)
{
    public static async Task<RuntimeSnapshot> CaptureAsync(TestProject project, ICodeRuntime? runtime, CancellationToken ct)
    {
        var components = new List<RuntimeComponent>
        {
            new(".NET", Environment.Version.ToString()),
            new("Architektur", RuntimeInformation.ProcessArchitecture.ToString()),
        };
        // Mitgelieferte Provider/Treiber, Compiler-Worker und Konfigurationen werden nach Inhalt gebunden.
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            components.Add(new(Path.GetFileName(file), System.Diagnostics.FileVersionInfo.GetVersionInfo(file).FileVersion ?? "",
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct))));
        }
        foreach (var folder in new[] { "workers", "toolchains" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, folder);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => folder == "toolchains" || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(file);
                components.Add(new(Path.GetRelativePath(AppContext.BaseDirectory, file).Replace('\\', '/'), "",
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct))));
            }
        }
        var languages = ReleasePackage.CodeFiles(project.Definition).Select(f => Path.GetExtension(f.Path) switch
        {
            ".py" => CodeLanguage.Python, ".cpp" => CodeLanguage.Cpp, ".txt" => CodeLanguage.LabView, _ => CodeLanguage.CSharp,
        }).Distinct();
        components.AddRange(await ExternalRuntimeInventory.CaptureAsync(ct));
        foreach (var language in languages)
        {
            if (runtime is null) throw new ReleaseRuleException($"Ausführungsumgebung für {language} fehlt.");
            components.Add(new("CodeRuntime/" + language, await runtime.GetRuntimeInfoAsync(language, ct)));
        }
        return new(components.OrderBy(c => c.Name, StringComparer.Ordinal).ToList());
    }

    public IReadOnlyList<string> Compare(RuntimeSnapshot actual)
    {
        var problems = new List<string>();
        foreach (var expected in Components)
        {
            var found = actual.Components.FirstOrDefault(c => c.Name == expected.Name);
            if (found is null) problems.Add($"{expected.Name} fehlt.");
            else if (expected != found) problems.Add($"{expected.Name}: Version oder Inhalt weicht vom Freigabepaket ab.");
        }
        foreach (var extra in actual.Components.Where(c => Components.All(e => e.Name != c.Name)))
            problems.Add($"{extra.Name}: im Freigabepaket nicht erfasst.");
        return problems;
    }
}
