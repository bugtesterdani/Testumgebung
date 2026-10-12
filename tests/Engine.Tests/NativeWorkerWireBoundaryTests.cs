using System.Diagnostics;
using System.Text.Json.Nodes;
using Testumgebung.Workers;

namespace Testumgebung.Engine.Tests;

public sealed class NativeWorkerWireBoundaryTests
{
    [Fact]
    public async Task Native_worker_handles_raw_abi_outcomes_host_errors_and_reduced_debugging()
    {
        Assert.NotNull(BundledToolchain.Compiler);
        var directory = Path.Combine(Path.GetTempPath(), "native-wire-" + Guid.NewGuid());
        var worker = Path.Combine(AppContext.BaseDirectory, "workers", "native", "Testumgebung.Workers.Native.dll");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(worker)!, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["PATH"] = Path.GetDirectoryName(BundledToolchain.Compiler!) + Path.PathSeparator + start.Environment["PATH"]; start.ArgumentList.Add(worker); start.Environment["TESTUMGEBUNG_CXX"] = BundledToolchain.Compiler!; start.Environment["TESTUMGEBUNG_CPP_CACHE"] = directory;
        using var process = Process.Start(start)!; var errors = process.StandardError.ReadToEndAsync();
        var calls = new List<string>();
        try
        {
            Assert.Equal("hello", (string?)JsonNode.Parse((await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)))!)!["t"]);
            var sequence = 0;
            async Task<JsonObject> Exchange(string source, JsonObject? debug = null, bool hostError = false, bool stop = false, JsonObject? inputs = null)
            {
                var id = (++sequence).ToString();
                await process.StandardInput.WriteLineAsync(new JsonObject { ["t"] = "invoke", ["id"] = id, ["source"] = source, ["debug"] = debug, ["values"] = inputs }.ToJsonString()); await process.StandardInput.FlushAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)); Assert.NotNull(line);
                    var message = JsonNode.Parse(line)!.AsObject(); Assert.Equal(id, (string?)message["id"]);
                    if ((string?)message["t"] != "call") return message;
                    var op = (string)message["op"]!; calls.Add(op);
                    JsonNode? value = op == "debug.at" ? new JsonObject { ["cmd"] = stop ? "stop" : "continue" } : op == "operator.ask" && (string?)message["args"]?["kind"] == "confirm" ? JsonValue.Create(true) : JsonValue.Create(2.5);
                    var reply = new JsonObject { ["t"] = "reply", ["cid"] = message["cid"]!.DeepClone(), ["value"] = value };
                    if (hostError && op != "debug.at") reply["error"] = "fixture host failure";
                    await process.StandardInput.WriteLineAsync(reply.ToJsonString()); await process.StandardInput.FlushAsync();
                }
            }
            static string Raw(string body) => "//#tu-raw\nTU_EXPORT int tu_invoke(const tu_host* h) { " + body + " }";
            foreach (var rc in new[] { 0, 1, 2 })
            {
                var response = await Exchange(Raw("return " + rc + ";"));
                Assert.True((string?)response["t"] == (rc == 1 ? "error" : "done"), response.ToJsonString());
                if (rc == 1) Assert.Contains("Fehler im nativen Code", (string)response["message"]!);
                if (rc == 2) Assert.True((bool)response["cancelled"]!);
                if (rc == 0) Assert.Equal("NResult", (string?)response["rating"]);
            }
            var callbackBody = """
                h->set_double(h->ctx,"d",h->get_double(h->ctx,"d"));
                h->set_int(h->ctx,"i",h->get_int(h->ctx,"i"));
                h->set_bool(h->ctx,"b",h->get_bool(h->ctx,"b"));
                h->set_string(h->ctx,"s",h->get_string(h->ctx,"s"));
                double v = 0; h->read(h->ctx,"resource",&v); h->write(h->ctx,"resource",v);
                h->log(h->ctx,"message"); h->ask_number(h->ctx,"number",0,0,0,0,nullptr,&v);
                h->confirm(h->ctx,"confirm"); h->var_get(h->ctx,"value",&v); h->var_get_string(h->ctx,"text");
                h->var_set_double(h->ctx,"value",3); h->var_set_string(h->ctx,"text","value");
                h->device(h->ctx,"fixture","{}"); h->cancelled(h->ctx);
                h->set_outcome(h->ctx,1,1,42,"message","V",1,0,1,100); return 0;
                """;
            foreach (var hostError in new[] { false, true })
            {
                var input = new JsonObject { ["d"] = 1.5, ["i"] = 42L, ["b"] = true, ["s"] = "text" };
                var response = await Exchange(Raw(callbackBody), new JsonObject { ["enabled"] = true }, hostError, inputs: input);
                Assert.Equal("done", (string?)response["t"]); Assert.Equal("Pass", (string?)response["rating"]); Assert.Equal(42, (int)response["value"]!);
                Assert.Equal(1.5, (double)response["values"]!["d"]!); Assert.Equal(42, (int)response["values"]!["i"]!); Assert.True((bool)response["values"]!["b"]!); Assert.Equal("text", (string?)response["values"]!["s"]);
                Assert.Equal("V", (string?)response["unit"]); Assert.Equal(0, (int)response["low"]!); Assert.Equal(100, (int)response["high"]!);
            }
            foreach (var input in new JsonObject?[] { null, new() { ["d"] = "bad", ["i"] = "bad", ["b"] = "bad", ["s"] = 42 } })
            {
                var response = await Exchange(Raw("h->set_double(h->ctx,\"d\",h->get_double(h->ctx,\"d\")); h->set_int(h->ctx,\"i\",h->get_int(h->ctx,\"i\")); h->set_bool(h->ctx,\"b\",h->get_bool(h->ctx,\"b\")); h->set_string(h->ctx,\"s\",h->get_string(h->ctx,\"s\")); return 0;"), inputs: input);
                Assert.Equal("done", (string?)response["t"]); if (input is null) Assert.Equal(0, (int)response["values"]!["d"]!); else Assert.Equal("NaN", (string?)response["values"]!["d"]); Assert.Equal(0, (int)response["values"]!["i"]!); Assert.False((bool)response["values"]!["b"]!);
            }
            var stopped = await Exchange(Raw("return 0;"), new JsonObject { ["enabled"] = true }, stop: true);
            Assert.True((bool)stopped["cancelled"]!); Assert.Contains("debug.at", calls); Assert.Contains("fixture", calls);
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        Assert.Equal(0, process.ExitCode); Assert.Equal("", await errors);
    }
}




