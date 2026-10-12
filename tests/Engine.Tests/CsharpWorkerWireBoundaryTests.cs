using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Testumgebung.Engine.Tests;

public sealed class CsharpWorkerWireBoundaryTests
{
    [Fact]
    public async Task Worker_handles_missing_optional_fields_invalid_assemblies_and_continues_after_errors()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "workers", "csharp", "Testumgebung.Workers.CSharp.dll");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(worker)!, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(worker);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var hello = JsonNode.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!)!;
            Assert.Equal("hello", (string?)hello["t"]);
            var sequence = 0;
            async Task<JsonObject> Exchange(JsonObject request)
            {
                var id = (++sequence).ToString(); request["id"] = id;
                await process.StandardInput.WriteLineAsync(request.ToJsonString()); await process.StandardInput.FlushAsync();
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(line); var response = JsonNode.Parse(line)!.AsObject(); Assert.Equal(id, (string?)response["id"]); return response;
            }
            var empty = await Exchange(new JsonObject { ["t"] = "compile" }); Assert.True((bool)empty["ok"]!);
            foreach (var bad in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["name"] = "bad.dll", ["data"] = "?" }, new JsonObject { ["name"] = "bad.dll", ["data"] = Convert.ToBase64String([1, 2, 3]) } })
            {
                var response = await Exchange(new JsonObject { ["t"] = "invoke", ["source"] = "return Pass();", ["assemblies"] = new JsonArray(bad?.DeepClone()) });
                Assert.Equal("error", (string?)response["t"]); Assert.NotEmpty((string)response["message"]!);
            }
            var assemblyFile = Path.Combine(AppContext.BaseDirectory, "fixtures", "OrderLibrary.dll");
            if (!File.Exists(assemblyFile)) assemblyFile = Path.Combine(AppContext.BaseDirectory, "OrderLibrary.dll");
            var image = new JsonObject { ["name"] = "fixture.dll", ["data"] = Convert.ToBase64String(File.ReadAllBytes(assemblyFile)) };
            var duplicate = await Exchange(new JsonObject { ["t"] = "invoke", ["source"] = "return Pass();", ["assemblies"] = new JsonArray(image.DeepClone(), image.DeepClone()) });
            Assert.Equal("error", (string?)duplicate["t"]); Assert.Contains("DLL doppelt", (string)duplicate["message"]!);
            var invalidBuild = await Exchange(new JsonObject { ["t"] = "invoke", ["source"] = "return MissingName;" });
            Assert.Equal("error", (string?)invalidBuild["t"]); Assert.Contains("Build fehlgeschlagen", (string)invalidBuild["message"]!);
            foreach (var debug in new JsonObject?[] { null, new(), new() { ["enabled"] = false }, new() { ["enabled"] = true } })
            {
                var response = await Exchange(new JsonObject { ["t"] = "invoke", ["source"] = "return Pass(42);", ["debug"] = debug });
                Assert.Equal("done", (string?)response["t"]); Assert.Equal("Pass", (string?)response["rating"]); Assert.Equal(42, (int)response["value"]!);
            }
            var values = await Exchange(new JsonObject { ["t"] = "invoke", ["source"] = "return Pass(text);", ["vars"] = new JsonArray(new JsonObject { ["name"] = "text", ["type"] = "String" }, new JsonObject { ["name"] = "missing", ["type"] = "Int" }, new JsonObject { ["name"] = "empty", ["type"] = "Bool" }), ["values"] = new JsonObject { ["text"] = 123, ["empty"] = null }, ["assemblies"] = new JsonArray(image.DeepClone()) });
            Assert.Equal("done", (string?)values["t"]); Assert.Equal("123", (string?)values["value"]); Assert.Equal(0, (int)values["values"]!["missing"]!); Assert.False((bool)values["values"]!["empty"]!);
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
        }
        Assert.Equal(0, process.ExitCode); Assert.Equal("", await errors);
    }
}
