/**
 * @file
 * @brief Startet den nativen Worker; übersetzte Bibliotheken und Callback-Speicher bleiben im isolierten Prozess.
 */

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testumgebung.Workers.Native;

/// <summary>
/// Native-Worker: JSON-Lines-Protokoll wie die anderen Worker (compile, invoke, cancel, reply).
/// Übersetzt C++-CodeBlocks zu Bibliotheken (Cache je Quelltext und Signatur) und ruft <c>tu_invoke</c> auf.
/// </summary>
// Native Bibliotheken und Callback-Zeiger leben in diesem Prozess. Ein erzwungenes
// Prozessende begrenzt den Schaden eines nicht kooperativ abbrechenden Codebausteins.
public static class Program
{
    private static readonly object WriteLock = new();
    private static readonly ConcurrentDictionary<string, Invocation> Running = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Calls = new();
    private static readonly ConcurrentDictionary<string, nint> Loaded = new();
    private static readonly ConcurrentDictionary<Task, byte> Pending = new();
    private static StreamWriter _out = null!;
    private static Toolchain? _toolchain;
    private static string? _toolchainError;
    private static long _callCounter;

    public static string CacheDirectory { get; private set; } = "";

    public static async Task<int> Main()
    {
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);
        CacheDirectory = Environment.GetEnvironmentVariable("TESTUMGEBUNG_CPP_CACHE") is { Length: > 0 } c
            ? c
            : Path.Combine(Path.GetTempPath(), "Testumgebung", "cpp");
        Directory.CreateDirectory(CacheDirectory);
        _toolchain = Toolchain.Detect(out _toolchainError);
        Send(new JsonObject
        {
            ["t"] = "hello",
            ["runtime"] = _toolchain is null
                ? $"C++-Worker – {_toolchainError}"
                : $"C++-Worker ({_toolchain}, ABI {CppSource.AbiVersion})",
        });

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject msg;
            try { msg = JsonNode.Parse(line)!.AsObject(); }
            catch (JsonException) { continue; }
            var id = (string?)msg["id"] ?? "";
            switch ((string?)msg["t"])
            {
                case "compile":
                    Track(Task.Run(() => Guard(id, () => Send(CompileMessage(id, msg)))));
                    break;
                case "invoke":
                    Track(Task.Run(() => Guard(id, () => Invoke(id, msg))));
                    break;
                case "cancel":
                    if (Running.TryGetValue(id, out var inv)) inv.Cancelled = true;
                    break;
                case "reply":
                    if (Calls.TryRemove((string?)msg["cid"] ?? "", out var tcs)) tcs.TrySetResult(msg);
                    break;
                case "exit":
                    return 0;
            }
        }
        await Task.WhenAny(Task.WhenAll(Pending.Keys), Task.Delay(TimeSpan.FromSeconds(5)));
        return 0;
    }

    private static void Track(Task task)
    {
        Pending[task] = 0;
        task.ContinueWith(t => Pending.TryRemove(t, out _), TaskScheduler.Default);
    }

    private static void Guard(string id, Action action)
    {
        try { action(); }
        catch (Exception ex) { SendError(id, $"{ex.GetType().Name}: {ex.Message}", ex.ToString()); }
    }

    internal static void Send(JsonObject message)
    {
        var text = message.ToJsonString();
        lock (WriteLock) _out.WriteLine(text);
    }

    private static void SendError(string id, string message, string? details)
        => Send(new JsonObject { ["t"] = "error", ["id"] = id, ["message"] = message, ["details"] = details });

    /// <summary>Reduzierter Debug-Halt (Eintritt/Austritt): meldet den Punkt an den Host und liefert den Befehl zurück.</summary>
    private static string DebugEmit(string id, int line, string kind, JsonObject vars)
    {
        var reply = Call(id, "debug.at", new JsonObject { ["line"] = line, ["kind"] = kind, ["vars"] = vars }) as JsonObject;
        return (string?)reply?["cmd"] ?? "continue";
    }

    /// <summary>Synchroner Rückruf an den Host.</summary>
    internal static JsonNode? Call(string invocationId, string op, JsonObject args)
    {
        var cid = $"n{Interlocked.Increment(ref _callCounter)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Calls[cid] = tcs;
        Send(new JsonObject { ["t"] = "call", ["id"] = invocationId, ["cid"] = cid, ["op"] = op, ["args"] = args });
        var reply = tcs.Task.GetAwaiter().GetResult();
        if (reply["error"] is { } error) throw new InvalidOperationException((string?)error ?? "Fehler im Host");
        return reply["value"]?.DeepClone();
    }

    // ------------------------------------------------------------------ Übersetzen

    private static List<CppVariable> ReadVariables(JsonObject msg)
        => (msg["vars"] as JsonArray ?? []).Select(v => new CppVariable((string)v!["name"]!, (string)v["type"]!)).ToList();

    private sealed record Built(string? Library, IReadOnlyList<CompilerDiagnostic> Diagnostics);

    private static readonly ConcurrentDictionary<string, Lazy<Built>> Builds = new();

    private static Built Build(string source, List<CppVariable> variables, bool instrument = false)
    {
        if (_toolchain is null) return new Built(null, [new CompilerDiagnostic(0, 0, "error", _toolchainError ?? "Keine Toolchain.", null)]);
        // LabVIEW-Glue übergibt fertigen Quelltext („//#tu-raw“), sonst Nutzercode einbetten (optional für den Debugger instrumentiert).
        var cpp = source.StartsWith("//#tu-raw", StringComparison.Ordinal) ? CppSource.Prelude + source : CppSource.Generate(source, variables, instrument);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_toolchain + "\u0001" + cpp)))[..24];
        return Builds.GetOrAdd(hash, _ => new Lazy<Built>(() =>
        {
            var dir = Path.Combine(CacheDirectory, hash);
            Directory.CreateDirectory(dir);
            var lib = Path.Combine(dir, "block" + _toolchain.LibraryExtension);
            if (File.Exists(lib)) return new Built(lib, []);
            var src = Path.Combine(dir, "block.cpp");
            File.WriteAllText(src, cpp, new UTF8Encoding(false));
            var output = _toolchain.Build(src, lib);
            return new Built(output.LibraryPath, output.Diagnostics);
        })).Value;
    }

    private static JsonObject CompileMessage(string id, JsonObject msg)
    {
        var built = Build((string?)msg["source"] ?? "", ReadVariables(msg));
        var diagnostics = new JsonArray();
        foreach (var d in built.Diagnostics)
            diagnostics.Add(new JsonObject { ["line"] = d.Line, ["column"] = d.Column, ["severity"] = d.Severity, ["message"] = d.Message, ["code"] = d.Code });
        return new JsonObject { ["t"] = "compiled", ["id"] = id, ["ok"] = built.Library is not null, ["diagnostics"] = diagnostics };
    }

    // ------------------------------------------------------------------ Ausführen

    /// <summary>Zustand eines Aufrufs; über ein GCHandle als ctx an den nativen Code übergeben.</summary>
    internal sealed unsafe class Invocation(string id, Dictionary<string, JsonNode?> values)
    {
        public string Id { get; } = id;
        public Dictionary<string, JsonNode?> Values { get; } = values;
        public JsonObject Outputs { get; } = [];
        public JsonObject? Outcome { get; set; }
        public volatile bool Cancelled;
        public string LastError { get; set; } = "";
        private readonly List<nint> _strings = [];

        /// <summary>UTF-8-Kopie, gültig bis zum Ende des Aufrufs.</summary>
        public byte* Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var ptr = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
            bytes.AsSpan().CopyTo(new Span<byte>(ptr, bytes.Length));
            ptr[bytes.Length] = 0;
            lock (_strings) _strings.Add((nint)ptr);
            return ptr;
        }

        public void Free()
        {
            lock (_strings)
            {
                foreach (var p in _strings) NativeMemory.Free((void*)p);
                _strings.Clear();
            }
        }
    }

    private static unsafe void Invoke(string id, JsonObject msg)
    {
        var variables = ReadVariables(msg);
        var source = (string?)msg["source"] ?? "";
        var debugEnabled = (msg["debug"] as JsonObject)?["enabled"]?.GetValue<bool>() == true;
        // C++-Zeilen-Debugger: Nutzercode instrumentieren (nicht den fertigen LabVIEW-Glue „//#tu-raw“).
        var instrumented = debugEnabled && !source.StartsWith("//#tu-raw", StringComparison.Ordinal);
        var built = Build(source, variables, instrumented);
        if (built.Library is null)
        {
            var first = built.Diagnostics.FirstOrDefault(d => d.Severity == "error");
            SendError(id, "Build fehlgeschlagen: " + (first is null ? "unbekannter Fehler" : first.Line > 0 ? $"Zeile {first.Line}: {first.Message}" : first.Message), null);
            return;
        }

        var handle = Loaded.GetOrAdd(built.Library, NativeLibrary.Load);
        var abi = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(handle, "tu_abi_version");
        if (abi() != CppSource.AbiVersion)
        {
            SendError(id, $"ABI-Version {abi()} wird nicht unterstützt (erwartet {CppSource.AbiVersion}).", null);
            return;
        }
        var invoke = (delegate* unmanaged[Cdecl]<HostTable*, int>)NativeLibrary.GetExport(handle, "tu_invoke");

        InvokeLibrary(id, msg, debugEnabled, instrumented, invoke);
    }

    private static unsafe void InvokeLibrary(string id, JsonObject msg, bool debugEnabled, bool instrumented,
        delegate* unmanaged[Cdecl]<HostTable*, int> invoke)
    {
        var values = (msg["values"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());
        var state = new Invocation(id, values);
        Running[id] = state;
        var gc = GCHandle.Alloc(state);
        var table = (HostTable*)NativeMemory.AllocZeroed((nuint)sizeof(HostTable));
        void EmitExitDebug()
        {
            // Austritt nur im reduzierten Modus (nicht instrumentiert, z. B. LabVIEW); instrumentierter C++-Code steppt über Zeilen.
            if (debugEnabled && !instrumented)
            {
                var exitVars = new JsonObject();
                foreach (var kv in values) exitVars[kv.Key] = kv.Value?.DeepClone();
                foreach (var kv in state.Outputs) exitVars[kv.Key] = kv.Value?.DeepClone();
                DebugEmit(id, 0, "exit", exitVars);
            }
        }

        try
        {
            // Eintritt (alle Debug-Läufe): erlaubt „bei Eintritt halten“ und einen ersten Halt vor dem Code.
            if (debugEnabled && StopAtEntry(id, values)) return;
            *table = HostTable.Create(GCHandle.ToIntPtr(gc));
            table->DbgActive = debugEnabled ? 1 : 0; // instrumentierter C++-Code meldet Zeilen über dbg_line
            int rc;
            rc = invoke(table);
            if (rc == 2 || (state.Cancelled && rc != 0))
            {
                Send(new JsonObject { ["t"] = "done", ["id"] = id, ["rating"] = "NResult", ["cancelled"] = true, ["values"] = new JsonObject() });
                return;
            }
            var outcome = state.Outcome ?? new JsonObject { ["rating"] = "NResult" };
            if (rc == 1)
            {
                SendError(id, (string?)outcome["message"] ?? "Fehler im nativen Code", null);
                return;
            }
            EmitExitDebug();
            Send(CompletionMessage(id, outcome, state));
        }
        finally
        {
            Running.TryRemove(id, out _);
            NativeMemory.Free(table);
            gc.Free();
            state.Free();
        }
    }

    private static bool StopAtEntry(string id, Dictionary<string, JsonNode?> values)
    {
        var entryVars = new JsonObject();
        foreach (var kv in values) entryVars[kv.Key] = kv.Value?.DeepClone();
        if (DebugEmit(id, 1, "entry", entryVars) == "stop")
        {
            Send(new JsonObject { ["t"] = "done", ["id"] = id, ["rating"] = "NResult", ["cancelled"] = true, ["values"] = new JsonObject() });
            return true;
        }
        return false;
    }

    private static JsonObject CompletionMessage(string id, JsonObject outcome, Invocation state)
    {
        return new JsonObject
        {
            ["t"] = "done",
            ["id"] = id,
            ["rating"] = outcome["rating"]?.DeepClone(),
            ["message"] = outcome["message"]?.DeepClone(),
            ["value"] = outcome["value"]?.DeepClone(),
            ["unit"] = outcome["unit"]?.DeepClone(),
            ["low"] = outcome["low"]?.DeepClone(),
            ["high"] = outcome["high"]?.DeepClone(),
            ["values"] = state.Outputs.DeepClone(),
            ["cancelled"] = state.Cancelled,
        };
    }

    // ------------------------------------------------------------------ Host-Tabelle (C-Funktionszeiger)

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct HostTable
    {
        public nint Ctx;
        public delegate* unmanaged[Cdecl]<nint, byte*, double> GetDouble;
        public delegate* unmanaged[Cdecl]<nint, byte*, long> GetInt;
        public delegate* unmanaged[Cdecl]<nint, byte*, int> GetBool;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*> GetString;
        public delegate* unmanaged[Cdecl]<nint, byte*, double, void> SetDouble;
        public delegate* unmanaged[Cdecl]<nint, byte*, long, void> SetInt;
        public delegate* unmanaged[Cdecl]<nint, byte*, int, void> SetBool;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*, void> SetString;
        public delegate* unmanaged[Cdecl]<nint, int, int, double, byte*, byte*, int, double, int, double, void> SetOutcome;
        public delegate* unmanaged[Cdecl]<nint, byte*, double*, int> Read;
        public delegate* unmanaged[Cdecl]<nint, byte*, double, int> Write;
        public delegate* unmanaged[Cdecl]<nint, byte*, void> Log;
        public delegate* unmanaged[Cdecl]<nint, int> Cancelled;
        public delegate* unmanaged[Cdecl]<nint, byte*, int, double, int, double, byte*, double*, int> AskNumber;
        public delegate* unmanaged[Cdecl]<nint, byte*, int> Confirm;
        public delegate* unmanaged[Cdecl]<nint, byte*, double*, int> VarGet;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*> VarGetString;
        public delegate* unmanaged[Cdecl]<nint, byte*, double, int> VarSetDouble;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*, int> VarSetString;
        public delegate* unmanaged[Cdecl]<nint, byte*> LastError;
        public delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*> Device;
        public delegate* unmanaged[Cdecl]<nint, int, byte*, nuint, nuint, uint, int> DbgLine;
        public int DbgActive;

        public static HostTable Create(nint ctx) => new()
        {
            Ctx = ctx,
            GetDouble = &Callbacks.GetDouble,
            GetInt = &Callbacks.GetInt,
            GetBool = &Callbacks.GetBool,
            GetString = &Callbacks.GetString,
            SetDouble = &Callbacks.SetDouble,
            SetInt = &Callbacks.SetInt,
            SetBool = &Callbacks.SetBool,
            SetString = &Callbacks.SetString,
            SetOutcome = &Callbacks.SetOutcome,
            Read = &Callbacks.Read,
            Write = &Callbacks.Write,
            Log = &Callbacks.Log,
            Cancelled = &Callbacks.IsCancelled,
            AskNumber = &Callbacks.AskNumber,
            Confirm = &Callbacks.Confirm,
            VarGet = &Callbacks.VarGet,
            VarGetString = &Callbacks.VarGetString,
            VarSetDouble = &Callbacks.VarSetDouble,
            VarSetString = &Callbacks.VarSetString,
            LastError = &Callbacks.LastError,
            Device = &Callbacks.Device,
            DbgLine = &Callbacks.DbgLine,
        };
    }

    /// <summary>Rückrufe aus C++. Ausnahmen dürfen nicht in den nativen Code gelangen.</summary>
    private static unsafe class Callbacks
    {
        private static Invocation S(nint ctx) => (Invocation)GCHandle.FromIntPtr(ctx).Target!;

        private static string Str(byte* p) => p is null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";

        private static JsonNode? Value(nint ctx, byte* name) => S(ctx).Values.GetValueOrDefault(Str(name));

        private static int Try(nint ctx, Action action)
        {
            try
            {
                action();
                return 0;
            }
            catch (Exception ex)
            {
                S(ctx).LastError = ex.Message;
                return -1;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static double GetDouble(nint ctx, byte* name)
        {
            try { return Value(ctx, name) is { } v ? v.GetValue<double>() : 0; }
            catch (Exception) { return double.NaN; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static long GetInt(nint ctx, byte* name)
        {
            try { return Value(ctx, name) is { } v ? (long)v.GetValue<double>() : 0; }
            catch (Exception) { return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int GetBool(nint ctx, byte* name)
        {
            try { return Value(ctx, name) is { } v && v.GetValue<bool>() ? 1 : 0; }
            catch (Exception) { return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static byte* GetString(nint ctx, byte* name)
        {
            try
            {
                var v = Value(ctx, name);
                return S(ctx).Utf8(v is null ? "" : v.GetValueKind() == JsonValueKind.String ? (string)v! : v.ToJsonString());
            }
            catch (Exception) { return S(ctx).Utf8(""); }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void SetDouble(nint ctx, byte* name, double value)
            => S(ctx).Outputs[Str(name)] = double.IsFinite(value) ? JsonValue.Create(value) : JsonValue.Create(value.ToString(CultureInfo.InvariantCulture));

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void SetInt(nint ctx, byte* name, long value) => S(ctx).Outputs[Str(name)] = value;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void SetBool(nint ctx, byte* name, int value) => S(ctx).Outputs[Str(name)] = value != 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void SetString(nint ctx, byte* name, byte* value) => S(ctx).Outputs[Str(name)] = Str(value);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void SetOutcome(nint ctx, int rating, int hasValue, double value, byte* message, byte* unit, int hasLow, double low, int hasHigh, double high)
        {
            var msg = Str(message);
            var u = Str(unit);
            S(ctx).Outcome = new JsonObject
            {
                ["rating"] = rating switch { 1 => "Pass", 2 => "Fail", 3 => "Error", _ => "NResult" },
                ["value"] = hasValue != 0 && double.IsFinite(value) ? value : null,
                ["message"] = msg.Length == 0 ? null : msg,
                ["unit"] = u.Length == 0 ? null : u,
                ["low"] = hasLow != 0 ? low : null,
                ["high"] = hasHigh != 0 ? high : null,
            };
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int Read(nint ctx, byte* resource, double* value)
        {
            var r = Str(resource);
            double result = 0;
            var rc = Try(ctx, () => result = Call(S(ctx).Id, "read", new JsonObject { ["resource"] = r })!.GetValue<double>());
            *value = result;
            return rc;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int Write(nint ctx, byte* resource, double value)
        {
            var r = Str(resource);
            return Try(ctx, () => Call(S(ctx).Id, "write", new JsonObject { ["resource"] = r, ["value"] = value }));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static void Log(nint ctx, byte* message)
        {
            var m = Str(message);
            Try(ctx, () => Call(S(ctx).Id, "log", new JsonObject { ["message"] = m }));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int IsCancelled(nint ctx) => S(ctx).Cancelled ? 1 : 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int AskNumber(nint ctx, byte* text, int hasMin, double min, int hasMax, double max, byte* unit, double* value)
        {
            var t = Str(text);
            var u = Str(unit);
            double result = 0;
            var rc = Try(ctx, () => result = Call(S(ctx).Id, "ask", new JsonObject
            {
                ["kind"] = "Input",
                ["text"] = t,
                ["type"] = "Double",
                ["required"] = true,
                ["min"] = hasMin != 0 ? min : null,
                ["max"] = hasMax != 0 ? max : null,
                ["unit"] = u.Length == 0 ? null : u,
            })!.GetValue<double>());
            *value = result;
            return rc;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int Confirm(nint ctx, byte* text)
        {
            var t = Str(text);
            return Try(ctx, () => Call(S(ctx).Id, "ask", new JsonObject { ["kind"] = "Instruction", ["text"] = t }));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int VarGet(nint ctx, byte* path, double* value)
        {
            var p = Str(path);
            double result = 0;
            var rc = Try(ctx, () =>
            {
                var node = Call(S(ctx).Id, "get", new JsonObject { ["path"] = p });
                result = node?.GetValueKind() switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    JsonValueKind.Number => node.GetValue<double>(),
                    _ => throw new InvalidOperationException($"Variable '{p}' ist keine Zahl – VarText verwenden."),
                };
            });
            *value = result;
            return rc;
        }

        /// <summary>Kommunikation: Argumente als JSON; Ergebnis als Text (Zeichenkette direkt, CAN-Rahmen: Datenbytes in Hex), null bei Fehler.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static byte* Device(nint ctx, byte* op, byte* args)
        {
            var o = Str(op);
            var a = Str(args);
            string? result = null;
            if (Try(ctx, () =>
                {
                    var obj = JsonNode.Parse(a) as JsonObject ?? throw new InvalidOperationException("Argumente sind kein JSON-Objekt.");
                    var node = Call(S(ctx).Id, o, obj);
                    result = node switch
                    {
                        null => "",
                        JsonObject f when f["data"] is { } d => (string?)d ?? "",
                        _ when node.GetValueKind() == JsonValueKind.String => (string)node!,
                        _ => node.ToJsonString(),
                    };
                }) != 0) return null;
            return S(ctx).Utf8(result ?? "");
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static byte* VarGetString(nint ctx, byte* path)
        {
            var p = Str(path);
            string? result = null;
            if (Try(ctx, () =>
                {
                    var node = Call(S(ctx).Id, "get", new JsonObject { ["path"] = p });
                    result = node is null ? "" : node.GetValueKind() == JsonValueKind.String ? (string)node! : node.ToJsonString();
                }) != 0) return null;
            return S(ctx).Utf8(result!);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int VarSetDouble(nint ctx, byte* path, double value)
        {
            var p = Str(path);
            return Try(ctx, () => Call(S(ctx).Id, "set", new JsonObject { ["path"] = p, ["value"] = value }));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int VarSetString(nint ctx, byte* path, byte* value)
        {
            var p = Str(path);
            var v = Str(value);
            return Try(ctx, () => Call(S(ctx).Id, "set", new JsonObject { ["path"] = p, ["value"] = v }));
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static byte* LastError(nint ctx) => S(ctx).Utf8(S(ctx).LastError);

        /// <summary>Zeilen-Debugger: instrumentierter C++-Code meldet eine Zeile; liefert 2 = stoppen, sonst 0.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        public static int DbgLine(nint ctx, int line, byte* varsJson, nuint frame, nuint pc, uint thread)
        {
            try
            {
                var vars = JsonNode.Parse(Str(varsJson)) as JsonObject ?? [];
                vars["__gdb_frame"] = frame.ToString("x");
                vars["__gdb_pc"] = pc.ToString("x");
                vars["__gdb_thread"] = thread.ToString();
                var cmd = DebugEmit(S(ctx).Id, line, "line", vars);
                return cmd == "stop" ? 2 : 0;
            }
            catch (Exception)
            {
                return 0; // Debugger-Fehler darf den Lauf nicht abbrechen
            }
        }
    }
}
