using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Testumgebung.Domain.Code;

namespace Testumgebung.Workers;

/// <summary>GDB/MI-Schnappschuss eines am Host-Rückruf wartenden C++-Workers. Der Prozess wird anschließend abgetrennt.</summary>
public static class GdbInspector
{
    public static async Task<NativeDebugSnapshot> CaptureAsync(string debugger, int processId, CancellationToken ct,
        string? nativeFrame = null, string? nativePc = null, string? nativeThread = null)
    {
        var info = new ProcessStartInfo(debugger)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "--quiet", "--nx", "--interpreter=mi2" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("GDB konnte nicht gestartet werden.");
        var stderr = process.StandardError.ReadToEndAsync();
        var token = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        async Task<JsonObject> Command(string command, CancellationToken cancel)
        {
            var prefix = (++token).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await process.StandardInput.WriteLineAsync(prefix + command);
            await process.StandardInput.FlushAsync(cancel);
            var size = 0;
            while (await process.StandardOutput.ReadLineAsync(cancel) is { } line)
            {
                size += line.Length;
                if (size > 4 * 1024 * 1024) throw new InvalidDataException("GDB-Antwort überschreitet das Limit.");
                if (!line.StartsWith(prefix + "^", StringComparison.Ordinal)) continue;
                var split = line.IndexOf(',');
                var kind = line[(prefix.Length + 1)..(split < 0 ? line.Length : split)];
                var values = split < 0 ? new JsonObject() : new MiReader(line[(split + 1)..]).Fields();
                if (kind == "error") throw new InvalidOperationException((string?)values["msg"] ?? "GDB-Fehler.");
                return values;
            }
            throw new IOException("GDB-Verbindung beendet.");
        }
        async Task<NativeDebugSnapshot?> CaptureNativeFrameAsync(JsonObject threads)
        {
            if (ulong.TryParse(nativeFrame, System.Globalization.NumberStyles.HexNumber, null, out var fp) && fp > 0
                && ulong.TryParse(nativePc, System.Globalization.NumberStyles.HexNumber, null, out var pc) && pc > 0
                && uint.TryParse(nativeThread, out var osThread))
            {
                var native = (threads["threads"] as JsonArray ?? []).OfType<JsonObject>()
                    .FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch((string?)t["target-id"] ?? "",
                        @"\.(?:" + osThread + "|0x" + osThread.ToString("x") + @")(?:\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (native is not null)
                {
                    await Command("-thread-select " + (string?)native["id"], timeout.Token);
                    // Reine Frame-Auswahl: keine Register oder Prozessdaten verändern. Überbrückt verwaltete Callback-Frames.
                    await Command($"-interpreter-exec console \"frame view 0x{fp:x} 0x{pc:x}\"", timeout.Token);
                    var data = await Command("-stack-list-variables --all-values", timeout.Token);
                    var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var v in (data["variables"] as JsonArray ?? []).OfType<JsonObject>())
                        if ((string?)v["name"] is { } name && !name.StartsWith("__", StringComparison.Ordinal))
                            variables.TryAdd(name, (string?)v["value"] ?? "<nicht verfügbar>");
                    var frames = new List<string>();
                    for (var i = 0; i < 12; i++)
                    {
                        var frameData = await Command("-stack-info-frame", timeout.Token);
                        if (frameData["frame"] is not JsonObject f) break;
                        frames.Add($"#{i} {f["func"]} ({f["file"]}:{f["line"]})");
                        try { await Command("-interpreter-exec console \"up-silently\"", timeout.Token); }
                        catch (InvalidOperationException) { break; }
                    }
                    return new(variables, frames);
                }
            }
            return null;
        }

        try
        {
            await Command("-gdb-set confirm off", timeout.Token);
            await Command("-gdb-set print elements 64", timeout.Token);
            await Command("-gdb-set print repeats 8", timeout.Token);
            await Command("-target-attach " + processId, timeout.Token);
            var threads = await Command("-thread-info", timeout.Token);
            var nativeSnapshot = await CaptureNativeFrameAsync(threads);
            if (nativeSnapshot is not null) return nativeSnapshot;

            return await CaptureStackAsync(threads);

            async Task<NativeDebugSnapshot> CaptureStackAsync(JsonObject threads)
            {
                foreach (var thread in (threads["threads"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var threadId = (string?)thread["id"];
                    if (threadId is null || !int.TryParse(threadId, out _)) continue;
                    JsonObject stack;
                    try { stack = await Command($"-stack-list-frames --thread {threadId} 0 80", timeout.Token); }
                    catch (InvalidOperationException) { continue; }
                    var frames = (stack["stack"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                    var frame = frames.FirstOrDefault(f => ((string?)f["file"]) is "CodeBlock"
                        || ((string?)f["fullname"])?.EndsWith("/CodeBlock", StringComparison.Ordinal) == true
                        || ((string?)f["fullname"])?.EndsWith("\\CodeBlock", StringComparison.Ordinal) == true);
                    if (frame is null) continue;
                    var level = (string?)frame["level"];
                    if (!int.TryParse(level, out _)) continue;
                    var data = await Command($"-stack-list-variables --thread {threadId} --frame {level} --all-values", timeout.Token);
                    var variables = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var variable in (data["variables"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var name = (string?)variable["name"];
                        if (name is not null && !name.StartsWith("__", StringComparison.Ordinal))
                            variables.TryAdd(name, (string?)variable["value"] ?? "<nicht verfügbar>");
                    }
                    return new(variables, frames.Select(f => $"#{f["level"]} {f["func"]} ({f["file"]}:{f["line"]})").ToList());
                }
                return new(new Dictionary<string, string>(), [], "No user code frame found.");
            }

        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or FormatException)
        {
            return new(new Dictionary<string, string>(), [], "Native Inspektion nicht verfügbar: " + ex.Message);
        }
        finally
        {
            // Auch bei Abbruch unabhängig vom Aufruf-Token abtrennen, damit der Worker weiterlaufen kann.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Command("-target-detach", cleanup.Token); } catch (Exception) { }
            try { await Command("-gdb-exit", cleanup.Token); } catch (Exception) { }
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(); }
            await stderr;
        }
    }

    // MI unterscheidet Ergebnislisten (frame={...}) und Wertlisten ({...}); beide werden hier zu JSON-Wertlisten.
    private sealed class MiReader(string text)
    {
        private int _position;
        public JsonObject Fields(char end = '\0')
        {
            var result = new JsonObject();
            while (_position < text.Length && text[_position] != end)
            {
                var start = _position;
                while (_position < text.Length && text[_position] != '=') _position++;
                if (_position >= text.Length) throw new FormatException("Ungültige GDB/MI-Felder.");
                var name = text[start.._position++];
                result[name] = Value();
                if (_position < text.Length && text[_position] == ',') _position++;
                else break;
            }
            return result;
        }
        private JsonNode Value()
        {
            if (_position >= text.Length) throw new FormatException("GDB/MI-Wert fehlt.");
            var c = text[_position++];
            if (c == '{') { var obj = Fields('}'); Expect('}'); return obj; }
            if (c == '[')
            {
                return ListValue();
            }
            if (c != '"') throw new FormatException("Ungültiger GDB/MI-Wert.");
            var value = new StringBuilder();
            while (_position < text.Length)
            {
                c = text[_position++];
                if (c == '"') return JsonValue.Create(value.ToString())!;
                if (c == '\\')
                {
                    if (_position >= text.Length) break;
                    c = text[_position++];
                    if (c is >= '0' and <= '7')
                    {
                        var number = c - '0';
                        for (var i = 0; i < 2 && _position < text.Length && text[_position] is >= '0' and <= '7'; i++)
                            number = number * 8 + text[_position++] - '0';
                        c = (char)number;
                    }
                    else c = c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c };
                }
                value.Append(c);
            }
            throw new FormatException("GDB/MI-Zeichenkette unvollständig.");
        }
        private JsonNode ListValue()
        {
            var list = new JsonArray();
            while (_position < text.Length && text[_position] != ']')
            {
                if (text[_position] is not ('{' or '[' or '"'))
                {
                    while (_position < text.Length && text[_position] != '=') _position++;
                    Expect('=');
                }
                list.Add(Value());
                if (_position < text.Length && text[_position] == ',') _position++;
                else break;
            }
            Expect(']');
            return list;
        }

        private void Expect(char c)
        {
            if (_position >= text.Length || text[_position++] != c) throw new FormatException("GDB/MI-Struktur unvollständig.");
        }
    }
}
