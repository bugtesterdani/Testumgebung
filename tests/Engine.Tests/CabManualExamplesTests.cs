using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

internal sealed class CabManualFactAttribute : FactAttribute
{
    public CabManualFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TESTUMGEBUNG_CAB_MANUAL")))
            Skip = "Lokales Hersteller-PDF nicht über TESTUMGEBUNG_CAB_MANUAL bereitgestellt.";
    }
}

public sealed class CabManualExamplesTests
{
    [CabManualFact]
    public async Task Local_manual_examples_are_extracted_validated_and_written_losslessly()
    {
        var pdf = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CAB_MANUAL")!;
        Assert.True(File.Exists(pdf), "TESTUMGEBUNG_CAB_MANUAL muss auf das lokale PDF zeigen.");
        var root = CabJScriptCatalogCoverageTests.RepositoryRoot();
        var output = Path.Combine(root, "artifacts/cab/manual/examples.json");
        var python = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CAB_MANUAL_PYTHON") ?? "python";
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-X", "utf8", Path.Combine(root, "build/extract-cab-manual-examples.py"), pdf, output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, "Lokale PDF-Extraktion fehlgeschlagen: " + await stderr);
        _ = await stdout;
        using var data = JsonDocument.Parse(File.ReadAllText(output));
        var records = data.RootElement.GetProperty("examples").EnumerateArray().ToArray();
        Assert.Equal(287, records.Length);
        using var policy = JsonDocument.Parse(File.ReadAllText(CabJScriptCatalogCoverageTests.Fixture("manual-expected-diagnostics.json")));
        Assert.Equal(1, policy.RootElement.GetProperty("version").GetInt32());
        var expected = policy.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(e => (e.GetProperty("page").GetInt32(), e.GetProperty("ordinal").GetInt32()));
        var failures = new List<object>();
        var knownNegative = 0;
        foreach (var record in records)
        {
            var diagnostics = Check(record);
            var key = (record.GetProperty("page").GetInt32(), record.GetProperty("ordinal").GetInt32());
            if (!expected.Remove(key, out var exception))
            {
                if (diagnostics.Length > 0) failures.Add(new { page = key.Item1, ordinal = key.Item2, diagnostics });
                continue;
            }
            Assert.False(string.IsNullOrWhiteSpace(exception.GetProperty("reason").GetString()));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.GetProperty("source").GetString()!)));
            Assert.Equal(exception.GetProperty("sourceSha256").GetString(), hash);
            var declared = exception.GetProperty("diagnostics").Deserialize<ManualDiagnostic[]>()!;
            Assert.NotEmpty(declared);
            Assert.Equal(declared, diagnostics);
            knownNegative++;
        }
        Assert.Empty(expected);
        var report = Path.Combine(root, "artifacts/cab/manual/validation.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = records.Length, valid = records.Length - knownNegative - failures.Count,
            knownNegative, failures }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, $"{failures.Count} lokale Handbuchfälle benötigen Prüfung; Details ohne Beispieltexte: {report}");
    }

    private sealed record ManualDiagnostic(string Code, string? CatalogId, string Message, int Line, int Column);

    private static ManualDiagnostic[] Check(JsonElement record)
    {
        Assert.True(record.GetProperty("extracted").GetBoolean(), "Kein Codeblock extrahiert.");
        var source = record.GetProperty("source").GetString()!;
        var bytes = Encoding.UTF8.GetBytes(source);
        var document = CabParser.Parse(bytes);
        Assert.Equal(bytes, document.Write());
        var validation = SelectGeneration(document);
        return validation.Diagnostics.Where(d => d.Severity == CabDiagnosticSeverity.Error)
            .Select(d => new ManualDiagnostic(d.Code, d.CatalogId, d.Message, d.Line, d.Column)).ToArray();
    }

    private static CabValidationResult SelectGeneration(CabDocument document)
    {
        var candidates = new[] { CabPrinterGeneration.X4, CabPrinterGeneration.X3, CabPrinterGeneration.X2, CabPrinterGeneration.Ax }
            .Select(g => CabValidator.Validate(document, new() { DefaultGeneration = g })).ToArray();
        // The manual spans four generations. Do not select an older generation merely
        // to hide a range error while its commands are explicitly unsupported there.
        var compatible = candidates.Where(c => !c.Diagnostics.Any(d => d.Code == "generation")).ToArray();
        return (compatible.Length > 0 ? compatible : candidates).MinBy(c => c.Diagnostics.Count(d => d.Severity == CabDiagnosticSeverity.Error))!;
    }
}
