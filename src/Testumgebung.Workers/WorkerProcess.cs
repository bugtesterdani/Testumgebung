using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Testumgebung.Workers;

/// <summary>
/// Ein Worker-Prozess (C# oder Python) mit JSON-Lines-Protokoll über stdin/stdout.
/// Nachrichten mit einer <c>id</c> werden an den registrierten Empfänger verteilt.
/// </summary>
public sealed class WorkerProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, Action<JsonObject>> _handlers = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<string> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderr = new();
    private int _disposed;

    private WorkerProcess(Process process)
    {
        _process = process;
    }

    /// <summary>Wird ausgelöst, wenn der Prozess endet (regulär, Absturz oder Kill).</summary>
    public event Action<WorkerProcess>? Exited;

    public bool IsAlive => _disposed == 0 && !_process.HasExited;

    public int ProcessId => _process.Id;

    /// <summary>Beschreibung der Laufzeit aus der „hello“-Nachricht.</summary>
    public Task<string> RuntimeInfo => _hello.Task;

    public string StandardError
    {
        get { lock (_stderr) return _stderr.ToString(); }
    }

    public static WorkerProcess Start(string fileName, IEnumerable<string> arguments, IDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (environment is not null)
            foreach (var (k, v) in environment) info.Environment[k] = v;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var worker = new WorkerProcess(process);
        process.Exited += (_, _) => worker.OnExited();
        if (!process.Start()) throw new InvalidOperationException($"Worker '{fileName}' konnte nicht gestartet werden.");
        process.StandardInput.AutoFlush = true;
        _ = Task.Run(worker.ReadLoopAsync);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (worker._stderr)
            {
                if (worker._stderr.Length < 20_000) worker._stderr.AppendLine(e.Data);
            }
        };
        process.BeginErrorReadLine();
        return worker;
    }

    public void Register(string id, Action<JsonObject> handler) => _handlers[id] = handler;

    public void Unregister(string id) => _handlers.TryRemove(id, out _);

    public void Send(JsonObject message)
    {
        var text = message.ToJsonString();
        lock (_writeLock)
        {
            if (!IsAlive) throw new InvalidOperationException("Worker-Prozess läuft nicht.");
            _process.StandardInput.WriteLine(text);
        }
    }

    /// <summary>Prozessabbruch (Timeout-Strategie für nicht kooperativen Fremdcode).</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject msg;
                try
                {
                    msg = JsonNode.Parse(line)!.AsObject();
                }
                catch (System.Text.Json.JsonException)
                {
                    continue; // Fremdausgabe (z. B. print im Nutzercode vor dem Protokoll) ignorieren
                }
                if ((string?)msg["t"] == "hello")
                {
                    _hello.TrySetResult((string?)msg["runtime"] ?? "");
                    continue;
                }
                if ((string?)msg["id"] is { } id && _handlers.TryGetValue(id, out var handler)) handler(msg);
            }
        }
        catch (Exception) when (!IsAlive)
        {
        }
    }

    private void OnExited()
    {
        _hello.TrySetException(new InvalidOperationException("Worker beendet: " + StandardError.Trim()));
        Exited?.Invoke(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (!_process.HasExited)
            {
                lock (_writeLock) _process.StandardInput.WriteLine("{\"t\":\"exit\"}");
                if (!_process.WaitForExit(1000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
        _process.Dispose();
    }
}
