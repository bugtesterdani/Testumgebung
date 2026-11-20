using System.Diagnostics;
using System.Text.Json;

namespace Testumgebung.Engine.Tests;

public sealed class CabJScriptCatalogSchemaTests
{
    [Fact]
    public void Catalog_contains_each_manual_section_exactly_once()
    {
        // Manual 07/2026, table of contents pp. 3–6; 5.83 is deliberately absent.
        var groups = new (string Prefix, int First, int Last)[]
        {
            ("1", 3, 6), ("2", 1, 25), ("3", 1, 17), ("4", 1, 18),
            ("4.2", 1, 45), ("4.5", 1, 7), ("4.7", 1, 3), ("4.11", 1, 7),
            ("5", 1, 108), ("6", 1, 4)
        };
        var expected = groups.SelectMany(g => Enumerable.Range(g.First, g.Last - g.First + 1)
            .Select(n => $"JS-{g.Prefix}.{n}" )).Where(id => id != "JS-5.83").Order().ToArray();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "docs", "cab", "jscript-befehlskatalog.json")));
        var actual = document.RootElement.GetProperty("eintraege").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).Order().ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Catalog_matches_schema_and_invalid_metadata_is_rejected()
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(RepositoryRoot(), "build", "test-cab-catalog-schema.ps1") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }

    [Fact]
    public void Catalog_identifiers_page_ranges_and_statistics_are_consistent()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "docs", "cab", "jscript-befehlskatalog.json")));
        var entries = document.RootElement.GetProperty("eintraege").EnumerateArray().ToArray();
        Assert.Equal(entries.Length, entries.Select(e => e.GetProperty("id").GetString()).Distinct().Count());
        foreach (var entry in entries)
        {
            Assert.Equal("JS-" + entry.GetProperty("abschnitt").GetString(), entry.GetProperty("id").GetString());
            var pages = entry.GetProperty("seiten");
            Assert.True(pages[0].GetInt32() <= pages[1].GetInt32());
        }
        var statistics = document.RootElement.GetProperty("statistik");
        Assert.Equal(entries.Length, statistics.GetProperty("eintraege").GetInt32());
        var parameters = entries.SelectMany(e => e.GetProperty("parameter").EnumerateArray()).ToArray();
        Assert.Equal(parameters.Length, statistics.GetProperty("parameter").GetInt32());
        Assert.Equal(parameters.Sum(p => p.GetProperty("werte").GetArrayLength()), statistics.GetProperty("werte").GetInt32());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Testumgebung.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repositorywurzel fehlt.");
    }
}
