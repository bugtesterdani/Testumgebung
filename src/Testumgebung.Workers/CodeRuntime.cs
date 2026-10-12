using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testumgebung.Contracts;
using Testumgebung.Domain.Code;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Workers;

public sealed class CodeRuntimeOptions
{
    /// <summary>Optionaler eigener Python-Interpreter. Standard: mitgelieferte portable Laufzeit.</summary>
    public string? PythonExecutable { get; set; }

    /// <summary>Verzeichnis mit workers/csharp und workers/python (Standard: neben der Anwendung).</summary>
    public string WorkerDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "workers");

    /// <summary>dotnet-Host für den C#-Worker, falls kein ausführbarer Starter vorhanden ist.</summary>
    public string? DotnetHost { get; set; }

    /// <summary>C++-Compiler (cl.exe, clang++ oder g++); null = automatisch (MSVC → clang++ → g++).</summary>
    public string? CppCompiler { get; set; }

    /// <summary>Ablage gebauter C++-Bibliotheken; null = %TEMP%\Testumgebung\cpp.</summary>
    public string? CppCacheDirectory { get; set; }

    /// <summary>Maximal vorgehaltene, untätige Worker je Sprache.</summary>
    public int MaxIdleWorkers { get; set; } = 2;

    public static string DefaultPython => ResolvePython(Path.Combine(AppContext.BaseDirectory, "workers"));

    public static string ResolvePython(string workerDirectory, string? configuredExecutable = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable)) return configuredExecutable;
        var bundled = Path.Combine(workerDirectory, "python", "runtime", "python.exe");
        return OperatingSystem.IsWindows() && File.Exists(bundled)
            ? Path.GetFullPath(bundled)
            : OperatingSystem.IsWindows() ? "python" : "python3";
    }
}

/// <summary>
/// Ausführungsumgebung für Codebausteine: je Aufruf ein eigener, isolierter Worker-Prozess aus einem Pool.
/// Kooperativer Abbruch, danach Prozessabbruch nach Timeout. Rückrufe (Variablen, Hardware, Werker)
/// laufen über den Host, damit Ressourcenlocks sprachübergreifend gelten.
/// </summary>
public sealed class CodeRuntime(CodeRuntimeOptions? options = null) : ICodeRuntime, IDisposable
{
    private readonly CodeRuntimeOptions _options = options ?? new CodeRuntimeOptions();
    private readonly ConcurrentDictionary<CodeLanguage, ConcurrentQueue<WorkerProcess>> _idle = new();
    private readonly ConcurrentDictionary<WorkerProcess, byte> _all = new();
    private int _disposed;
    private readonly object _poolGate = new();
    private readonly Dictionary<string, CodeRuntime> _pythonEnvironments = new(StringComparer.OrdinalIgnoreCase);

    public CodeRuntimeOptions Options => _options;

    public ICodeRuntime WithPythonEnvironment(string? programVenv, string? variableVenv)
    {
        lock (_poolGate)
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            var executable = PythonEnvironmentResolver.Resolve(_options.WorkerDirectory, programVenv, variableVenv, _options.PythonExecutable);
            if (!_pythonEnvironments.TryGetValue(executable, out var python))
            {
                python = new CodeRuntime(new CodeRuntimeOptions
                {
                    PythonExecutable = executable, WorkerDirectory = _options.WorkerDirectory,
                    MaxIdleWorkers = _options.MaxIdleWorkers,
                });
                _pythonEnvironments.Add(executable, python);
            }
            return new PythonRunRuntime(this, python);
        }
    }

    private bool HasActiveWorkers()
    {
        lock (_poolGate)
            return _all.Keys.Except(_idle.Values.SelectMany(queue => queue)).Any()
                || _pythonEnvironments.Values.Any(runtime => runtime.HasActiveWorkers());
    }

    /// <summary>Wechselt Python bei ruhendem Ablauf und verwirft gepoolte Prozesse mit alten Paketständen.</summary>
    public void SelectPython(string? executable)
    {
        lock (_poolGate)
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            if (HasActiveWorkers())
                throw new InvalidOperationException("Ein Codebaustein wird noch ausgeführt. Python erst danach wechseln.");
            foreach (var runtime in _pythonEnvironments.Values) runtime.Dispose();
            _pythonEnvironments.Clear();
            if (_idle.TryGetValue(CodeLanguage.Python, out var queue))
                while (queue.TryDequeue(out var worker))
                {
                    Forget(worker);
                    worker.Dispose();
                }
            _options.PythonExecutable = executable;
        }
    }

    // ------------------------------------------------------------------ Pool

    private static CodeLanguage PoolKey(CodeLanguage language) => language == CodeLanguage.LabView ? CodeLanguage.Cpp : language;

    private WorkerProcess Lease(CodeLanguage language)
    {
        lock (_poolGate)
        {
            language = PoolKey(language);
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            var queue = _idle.GetOrAdd(language, _ => new ConcurrentQueue<WorkerProcess>());
            while (queue.TryDequeue(out var w))
            {
                if (w.IsAlive) return w;
                Forget(w);
            }
            var worker = StartWorker(language);
            _all[worker] = 0;
            worker.Exited += Forget;
            return worker;
        }
    }

    private void Return(CodeLanguage language, WorkerProcess worker)
    {
        lock (_poolGate)
        {
            language = PoolKey(language);
            var queue = _idle.GetOrAdd(language, _ => new ConcurrentQueue<WorkerProcess>());
            if (_disposed == 0 && worker.IsAlive && queue.Count < _options.MaxIdleWorkers) queue.Enqueue(worker);
            else
            {
                Forget(worker);
                worker.Dispose();
            }
        }
    }

    private void Forget(WorkerProcess worker) => _all.TryRemove(worker, out _);

    private WorkerProcess StartWorker(CodeLanguage language)
    {
        switch (language)
        {
            case CodeLanguage.Python:
                {
                    var script = Path.Combine(_options.WorkerDirectory, "python", "worker.py");
                    if (!File.Exists(script)) throw new CodeExecutionException($"Python-Worker nicht gefunden: {script}");
                    var python = CodeRuntimeOptions.ResolvePython(_options.WorkerDirectory, _options.PythonExecutable);
                    try
                    {
                        return WorkerProcess.Start(python, ["-u", script]);
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        throw new CodeExecutionException($"Python-Interpreter '{python}' konnte nicht gestartet werden: {ex.Message}");
                    }
                }
            case CodeLanguage.Cpp or CodeLanguage.LabView:
                {
                    var dir = Path.Combine(_options.WorkerDirectory, "native");
                    var dll = Path.Combine(dir, "Testumgebung.Workers.Native.dll");
                    var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "Testumgebung.Workers.Native.exe" : "Testumgebung.Workers.Native");
                    if (!File.Exists(dll)) throw new CodeExecutionException($"C++-Worker nicht gefunden: {dll}");
                    var env = new Dictionary<string, string>();
                    var compiler = _options.CppCompiler ?? BundledToolchain.Compiler;
                    if (compiler is { Length: > 0 } cxx)
                    {
                        env["TESTUMGEBUNG_CXX"] = cxx;
                        // DLL-Abhängigkeiten der portablen Toolchain nur für diesen Worker auflösen.
                        env["PATH"] = Path.GetDirectoryName(cxx) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                    }
                    if (BundledToolchain.Debugger is { } gdb) env["TESTUMGEBUNG_GDB"] = gdb;
                    if (_options.CppCacheDirectory is { Length: > 0 } cache) env["TESTUMGEBUNG_CPP_CACHE"] = cache;
                    if (File.Exists(exe) && OperatingSystem.IsWindows()) return WorkerProcess.Start(exe, [], env);
                    return WorkerProcess.Start(DotnetHost(), ["exec", dll], env);
                }
            case CodeLanguage.CSharp:
                {
                    var dir = Path.Combine(_options.WorkerDirectory, "csharp");
                    var dll = Path.Combine(dir, "Testumgebung.Workers.CSharp.dll");
                    var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "Testumgebung.Workers.CSharp.exe" : "Testumgebung.Workers.CSharp");
                    if (!File.Exists(dll)) throw new CodeExecutionException($"C#-Worker nicht gefunden: {dll}");
                    if (File.Exists(exe) && OperatingSystem.IsWindows()) return WorkerProcess.Start(exe, []);
                    return WorkerProcess.Start(DotnetHost(), ["exec", dll]);
                }
            default:
                throw new CodeExecutionException($"Sprache {language} wird nicht unterstützt.");
        }
    }

    private string DotnetHost()
    {
        if (_options.DotnetHost is { } configured) return configured;
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath)) return hostPath;
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return current;
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    // ------------------------------------------------------------------ ICodeRuntime

    public async Task<string> GetRuntimeInfoAsync(CodeLanguage language, CancellationToken cancellationToken)
    {
        var worker = Lease(language);
        try
        {
            return await worker.RuntimeInfo.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Return(language, worker);
        }
    }

    /// <summary>LabVIEW: Einstellungen prüfen und C++-Glue erzeugen; andere Sprachen unverändert.</summary>
    private static CodeRequest Translate(CodeRequest request, out IReadOnlyList<CodeDiagnostic> diagnostics)
    {
        diagnostics = [];
        if (request.Language != CodeLanguage.LabView) return request;
        var settings = LabViewSettings.Parse(request.Source);
        diagnostics = settings.Check(request.Variables);
        return request with { Language = CodeLanguage.Cpp, Source = settings.GenerateGlue(request.Variables) };
    }

    public async Task<CompileResult> CompileAsync(CodeRequest request, CancellationToken cancellationToken)
    {
        var translated = Translate(request, out var pre);
        if (pre.Any(d => d.IsError))
        {
            var info = await GetRuntimeInfoAsync(CodeLanguage.Cpp, cancellationToken).ConfigureAwait(false);
            return new CompileResult(false, pre, "LabVIEW-Prototyp über " + info);
        }
        var result = await CompileCoreAsync(translated, cancellationToken).ConfigureAwait(false);
        return pre.Count == 0 ? result : result with
        {
            Diagnostics = [.. pre, .. result.Diagnostics],
            RuntimeInfo = "LabVIEW-Prototyp über " + result.RuntimeInfo,
        };
    }

    private async Task<CompileResult> CompileCoreAsync(CodeRequest request, CancellationToken cancellationToken)
    {
        var worker = Lease(request.Language);
        var id = NewId("compile");
        var tcs = new TaskCompletionSource<CompileResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExit(WorkerProcess w) => tcs.TrySetException(new CodeExecutionException("Worker beendet: " + w.StandardError.Trim()));
        worker.Exited += OnExit;
        worker.Register(id, msg =>
        {
            if ((string?)msg["t"] == "compiled")
            {
                var diagnostics = (msg["diagnostics"] as JsonArray ?? [])
                    .Where(d => d is not null)
                    .Select(d => new CodeDiagnostic(
                        (int?)d!["line"] ?? 0, (int?)d["column"] ?? 0, (string?)d["severity"] ?? "error",
                        (string?)d["message"] ?? "", (string?)d["code"]))
                    .ToList();
                tcs.TrySetResult(new CompileResult((bool?)msg["ok"] ?? false, diagnostics, ""));
            }
            else if ((string?)msg["t"] == "error")
            {
                tcs.TrySetException(new CodeExecutionException((string?)msg["message"] ?? "Fehler im Worker", (string?)msg["details"]));
            }
        });
        try
        {
            if (!worker.IsAlive) OnExit(worker);
            else worker.Send(Request("compile", id, request));
            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            var info = await worker.RuntimeInfo.ConfigureAwait(false);
            return result with { RuntimeInfo = info };
        }
        finally
        {
            worker.Unregister(id);
            worker.Exited -= OnExit;
            Return(request.Language, worker);
        }
    }

    public async Task<CodeResult> InvokeAsync(
        CodeRequest request,
        IReadOnlyDictionary<string, object?> values,
        ICodeHost host,
        TimeSpan abortTimeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request = Translate(request, out var pre);
        if (pre.FirstOrDefault(d => d.IsError) is { } blocking) throw new CodeExecutionException(blocking.Message);
        var worker = Lease(request.Language);
        var id = string.IsNullOrEmpty(request.InvocationId) ? NewId("inv") : request.InvocationId;
        var tcs = new TaskCompletionSource<CodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = 0;
        var types = request.Variables.ToDictionary(v => v.Name, v => v.Type);

        void OnExit(WorkerProcess w)
        {
            if (Volatile.Read(ref killed) == 1) return;
            tcs.TrySetException(new CodeExecutionException("Worker-Prozess unerwartet beendet.", w.StandardError));
        }

        worker.Exited += OnExit;
        worker.Register(id, msg =>
        {
            switch ((string?)msg["t"])
            {
                case "call":
                    _ = HandleCallAsync(worker, id, msg, host, request.Language, cancellationToken);
                    break;
                case "done":
                    try
                    {
                        tcs.TrySetResult(ParseResult(msg, types));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    break;
                case "error":
                    tcs.TrySetException(new CodeExecutionException((string?)msg["message"] ?? "Fehler im Code", (string?)msg["details"]));
                    break;
            }
        });

        var message = Request("invoke", id, request);
        var json = new JsonObject();
        foreach (var (name, value) in values)
            if (types.ContainsKey(name)) json[name] = ToJson(value);
        message["values"] = json;
        if (request.Debug is { Enabled: true } dbg)
        {
            var bps = new JsonArray();
            foreach (var line in dbg.Breakpoints) bps.Add(line);
            message["debug"] = new JsonObject
            {
                ["enabled"] = true,
                ["breakpoints"] = bps,
                ["entry"] = dbg.StopOnEntry,
                ["step"] = dbg.Step,
            };
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                worker.Send(new JsonObject { ["t"] = "cancel", ["id"] = id });
            }
            catch (InvalidOperationException)
            {
            }
            _ = Task.Delay(abortTimeout).ContinueWith(_ =>
            {
                if (tcs.Task.IsCompleted) return;
                Volatile.Write(ref killed, 1);
                worker.Kill();
                tcs.TrySetResult(new CodeResult(ResultRating.NResult, new Dictionary<string, object?>(),
                    Message: $"Code reagierte nicht auf den Abbruch – Worker-Prozess nach {abortTimeout.TotalSeconds:0.#} s beendet.",
                    Cancelled: true));
            }, TaskScheduler.Default);
        });

        try
        {
            if (!worker.IsAlive) OnExit(worker);
            else worker.Send(message);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            worker.Unregister(id);
            worker.Exited -= OnExit;
            if (Volatile.Read(ref killed) == 1)
            {
                Forget(worker);
                worker.Dispose();
            }
            else Return(request.Language, worker);
        }
    }

    // ------------------------------------------------------------------ Rückrufe

    private static async Task HandleCallAsync(WorkerProcess worker, string id, JsonObject msg, ICodeHost host, CodeLanguage language, CancellationToken cancellationToken)
    {
        var cid = (string?)msg["cid"];
        var args = msg["args"] as JsonObject ?? [];
        var reply = new JsonObject { ["t"] = "reply", ["id"] = id, ["cid"] = cid };
        async Task HandleOperatorPromptAsync()
        {
            var kind = Enum.TryParse<OperatorPromptKind>((string?)args["kind"], out var k) ? k : OperatorPromptKind.Instruction;
            VarType? type = Enum.TryParse<VarType>((string?)args["type"], out var t) ? t : null;
            var prompt = new OperatorPrompt(kind, (string?)args["text"] ?? "", kind == OperatorPromptKind.Input ? type ?? VarType.String : null,
                (bool?)args["required"] ?? false, Min: (double?)args["min"], Max: (double?)args["max"], Unit: (string?)args["unit"]);
            reply["value"] = ToJson(await host.AskAsync(prompt).ConfigureAwait(false));
        }

        try
        {
            switch ((string?)msg["op"])
            {
                case "get":
                    reply["value"] = ToJson(host.Get((string)args["path"]!));
                    break;
                case "set":
                    host.Set((string)args["path"]!, FromJson(args["value"]));
                    break;
                case "read":
                    reply["value"] = await host.ReadAsync((string)args["resource"]!).ConfigureAwait(false);
                    break;
                case "write":
                    await host.WriteAsync((string)args["resource"]!, args["value"]!.GetValue<double>()).ConfigureAwait(false);
                    break;
                case "log":
                    host.Log((string?)args["message"] ?? "");
                    break;
                case "ask":
                    await HandleOperatorPromptAsync();
                    break;
                case "select.test":
                    host.SelectTest((string?)args["test"] ?? "", (string?)args["version"], (string?)args["order"], (string?)args["parameters"], (string?)args["pythonVenv"]);
                    break;
                case "debug.at":
                    {
                        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
                        if (args["vars"] is JsonObject vobj)
                            foreach (var (key, node) in vobj)
                                if (!key.StartsWith("__gdb_", StringComparison.Ordinal)) vars[key] = FromJson(node);
                        var decision = await host.DebugAsync(new CodeDebugEvent(
                            (int?)args["line"] ?? 0, (string?)args["kind"] ?? "line", vars)
                        {
                            InspectNativeAsync = language == CodeLanguage.Cpp && BundledToolchain.Debugger is { } debugger
                                ? ct => GdbInspector.CaptureAsync(debugger, worker.ProcessId, ct,
                                    (string?)args["vars"]?["__gdb_frame"], (string?)args["vars"]?["__gdb_pc"],
                                    (string?)args["vars"]?["__gdb_thread"]) : null,
                        }).ConfigureAwait(false);
                        var bps = new JsonArray();
                        foreach (var line in decision.Breakpoints) bps.Add(line);
                        reply["value"] = new JsonObject { ["cmd"] = decision.Command, ["breakpoints"] = bps, ["step"] = decision.Step };
                        break;
                    }
                case var op when op is not null && IsDeviceOperation(op):
                    {
                        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var (key, node) in args) arguments[key] = FromJson(node);
                        reply["value"] = ToJsonDeep(await host.DeviceAsync(op, arguments).ConfigureAwait(false));
                        break;
                    }
                default:
                    reply["error"] = $"Unbekannte Operation '{(string?)msg["op"]}'.";
                    break;
            }
        }
        catch (Exception ex)
        {
            reply["error"] = ex.Message;
        }
        try
        {
            worker.Send(reply);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static CodeResult ParseResult(JsonObject msg, IReadOnlyDictionary<string, VarType> types)
    {
        var rating = Enum.TryParse<ResultRating>((string?)msg["rating"], ignoreCase: true, out var r) ? r : ResultRating.NResult;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (msg["values"] is JsonObject obj)
        {
            foreach (var (name, node) in obj)
            {
                if (!types.TryGetValue(name, out var type) || node is null) continue;
                try
                {
                    values[name] = Convert(node, type);
                }
                catch (Exception ex) when (ex is TypeMismatchException or FormatException or InvalidOperationException)
                {
                    throw new CodeExecutionException($"Variable '{name}': {ex.Message}");
                }
            }
        }
        return new CodeResult(
            rating,
            values,
            FromJson(msg["value"]),
            (string?)msg["unit"],
            (double?)msg["low"],
            (double?)msg["high"],
            (string?)msg["message"],
            (bool?)msg["cancelled"] ?? false);
    }

    // ------------------------------------------------------------------ Werte

    private static JsonObject Request(string type, string id, CodeRequest request)
    {
        var vars = new JsonArray();
        foreach (var v in request.Variables) vars.Add(new JsonObject { ["name"] = v.Name, ["type"] = v.Type.ToString() });
        return new JsonObject
        {
            ["t"] = type,
            ["id"] = id,
            ["source"] = request.Source,
            ["assemblies"] = request.Language == CodeLanguage.CSharp
                ? new JsonArray(request.Assemblies.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["data"] = System.Convert.ToBase64String(a.Data) }).ToArray()) : new JsonArray(),
            ["vars"] = vars,
        };
    }

    private static long _counter;

    private static string NewId(string prefix) => $"{prefix}-{Interlocked.Increment(ref _counter)}";

    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create((long)i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        DateTime dt => JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>Kommunikationsoperationen (comm., modbus., can., uds, j1939., lin.) – Ausführung im Host.</summary>
    private static bool IsDeviceOperation(string op)
        => op == "uds" || op.StartsWith("comm.", StringComparison.Ordinal) || op.StartsWith("modbus.", StringComparison.Ordinal)
           || op.StartsWith("can.", StringComparison.Ordinal) || op.StartsWith("j1939.", StringComparison.Ordinal) || op.StartsWith("lin.", StringComparison.Ordinal)
           || op.StartsWith("opcua.", StringComparison.Ordinal)
           || op is "http.request" or "ssh.exec" or "sftp.upload" or "sftp.download";

    private static JsonNode? ToJsonDeep(object? value)
    {
        if (value is IEnumerable<KeyValuePair<string, object?>> dict)
        {
            var obj = new JsonObject();
            foreach (var (k, v) in dict) obj[k] = ToJsonDeep(v);
            return obj;
        }
        return ToJson(value);
    }

    /// <summary>JSON ohne Typinformation → bool, long, double oder string.</summary>
    internal static object? FromJson(JsonNode? node)
    {
        if (node is null) return null;
        return node.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => node.ToJsonString() is var text && (text.Contains('.') || text.Contains('e') || text.Contains('E'))
                ? node.GetValue<double>()
                : long.TryParse(node.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : node.GetValue<double>(),
            JsonValueKind.String => (string?)node,
            _ => node.ToJsonString(),
        };
    }

    private static object Convert(JsonNode node, VarType type)
    {
        var raw = FromJson(node);
        if (type == VarType.DateTime && raw is string s)
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == VarType.Double && raw is string ds && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d; // NaN/Infinity
        if (type == VarType.Int && raw is double dbl && dbl == Math.Floor(dbl))
            return (long)dbl;
        return TypedValue.Coerce(raw, type);
    }

    public void Dispose()
    {
        lock (_poolGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            foreach (var runtime in _pythonEnvironments.Values) runtime.Dispose();
            _pythonEnvironments.Clear();
            foreach (var w in _all.Keys) w.Dispose();
            _all.Clear();
            _idle.Clear();
        }
    }
}
