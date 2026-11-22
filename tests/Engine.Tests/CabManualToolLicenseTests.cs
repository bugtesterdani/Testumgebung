using System.Security.Cryptography;
using System.Text.Json;

namespace Testumgebung.Engine.Tests;

public sealed class CabManualToolLicenseTests
{
    [Fact]
    public void Optional_tool_pins_have_original_license_texts_with_verified_hashes()
    {
        var root = CabJScriptCatalogCoverageTests.RepositoryRoot();
        var pins = File.ReadAllLines(Path.Combine(root, "build/requirements-cab-manual.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Order().ToArray();
        var directory = Path.Combine(root, "licenses/optional-cab-manual");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var rows = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(pins, rows.Select(r => r.GetProperty("package").GetString() + "==" + r.GetProperty("version").GetString()).Distinct().Order());
        foreach (var row in rows)
        {
            var file = Path.GetFullPath(Path.Combine(directory, row.GetProperty("file").GetString()!));
            Assert.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(row.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
        }
    }
}
