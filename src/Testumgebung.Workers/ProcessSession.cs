/**
 * @file
 * @brief Verwaltet Prozesse je Prüflauf und ordnet deren Lebensdauer und Ausgaben zu.
 */

using System.Diagnostics;
using System.Text.Json;
using Testumgebung.Domain.Processes;

namespace Testumgebung.Workers;

/// <summary>Owns a run's process handles; independent processes outlive session disposal.</summary>
public sealed class ProcessSession : IProcessSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ExternalProcessRecord> _records = new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly int _capacity;
    /// <summary>Bounds retained handles and active processes; smaller limits are useful for constrained hosts.</summary>
    public ProcessSession(int capacity = 128)
    {
        if (capacity is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public ProcessSnapshot Start(ProcessLaunch request, string owner, string workerDirectory, string? pythonExecutable, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            Prune();
            Validate(request);
            var directory = Path.Combine(Path.GetTempPath(), "Testumgebung", "processes", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Process? process = null;
            ProcessJob? job = null;
            try
            {
                var command = Command(request, workerDirectory, pythonExecutable, Path.Combine(directory, "error.txt"));
                var gated = request.Bound && OperatingSystem.IsWindows();
                var start = gated ? StartInfo(Worker(workerDirectory), ["--process-proxy"], request with { ShowWindow = false, Environment = new Dictionary<string, string>() })
                    : StartInfo(command.Executable, command.Arguments, request);
                start.RedirectStandardInput = gated;
                if (gated) start.StandardInputEncoding = new System.Text.UTF8Encoding(false);
                process = Process.Start(start) ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden.");
                if (gated)
                {
                    job = ProcessJob.Attach(process);
                    using var startupCancellation = cancellationToken.Register(job.Dispose);
                    cancellationToken.ThrowIfCancellationRequested();
                    process.StandardInput.WriteLine(JsonSerializer.Serialize(command));
                    process.StandardInput.Close();
                }
                var record = new ExternalProcessRecord(process, job, directory, owner, request);
                _records.Add(record.Handle, record);
                return record.Snapshot();
            }
            catch
            {
                job?.Dispose();
                if (process is not null) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
                Directory.Delete(directory, recursive: true);
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }
    }
    private void Prune()
    {
        if (_records.Count < _capacity) return;
        foreach (var record in _records.Values.Where(r => r.Completion.IsCompleted).ToList())
        { _records.Remove(record.Handle); record.Dispose(); }
        if (_records.Count >= _capacity) throw new InvalidOperationException($"Maximal {_capacity} laufende Prozesse je Prüflauf erlaubt.");
    }
    private static void Validate(ProcessLaunch request)
    {
        if (!Path.IsPathFullyQualified(request.File) || !File.Exists(request.File)) throw new FileNotFoundException("Absoluter Programm-/Skript-/DLL-Pfad fehlt oder existiert nicht.", request.File);
        if (!Path.IsPathFullyQualified(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory)) throw new DirectoryNotFoundException("Absolutes Arbeitsverzeichnis fehlt oder existiert nicht.");
        if (request.OutputLimit is < 0 or > 1048576) throw new ArgumentOutOfRangeException(nameof(request.OutputLimit), "Ausgabelimit muss zwischen 0 und 1048576 Zeichen liegen.");
        if (request.Kind == ExternalProcessKind.ManagedMain && string.IsNullOrWhiteSpace(request.TypeName)) throw new ArgumentException("DLL-Main benötigt den vollständigen Typnamen.");
    }
    private sealed record LaunchCommand(string Executable, string[] Arguments, string WorkingDirectory,
        Dictionary<string, string> Environment, bool ShowWindow, bool CaptureOutput, string ErrorFile);
    private static LaunchCommand Command(ProcessLaunch request, string workers, string? python, string error)
    {
        var arguments = request.Arguments.ToList();
        var executable = request.File;
        if (request.Kind == ExternalProcessKind.Python)
        {
            executable = request.PythonExecutable ?? python ?? CodeRuntimeOptions.ResolvePython(workers);
            // Embedded Python's _pth omits the script directory. Restore normal local
            // imports explicitly; the script path remains a separate argv value.
            const string bootstrap = "import os,sys,runpy; path=sys.argv[1]; sys.path.insert(0,os.path.dirname(os.path.abspath(path))); sys.argv=sys.argv[1:]; runpy.run_path(path,run_name='__main__')";
            arguments.InsertRange(0, ["-X", "utf8", "-c", bootstrap, request.File]);
        }
        if (request.Kind is ExternalProcessKind.ManagedEntryPoint or ExternalProcessKind.ManagedMain)
        {
            executable = Worker(workers);
            arguments.InsertRange(0, ["--managed-main", request.File, request.Kind == ExternalProcessKind.ManagedMain ? request.TypeName! : "", error, request.OutputEncoding]);
        }
        return new(executable, arguments.ToArray(), request.WorkingDirectory, request.Environment.ToDictionary(), request.ShowWindow, request.CaptureOutput, error);
    }
    private static string Worker(string workers)
    {
        var file = Path.Combine(workers, "csharp", OperatingSystem.IsWindows() ? "Testumgebung.Workers.CSharp.exe" : "Testumgebung.Workers.CSharp");
        return File.Exists(file) ? file : throw new FileNotFoundException("Mitgelieferter .NET-Prozesshost fehlt.", file);
    }
    private static ProcessStartInfo StartInfo(string file, IEnumerable<string> arguments, ProcessLaunch request)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var encoding = System.Text.Encoding.GetEncoding(request.OutputEncoding);
        if (request.Kind == ExternalProcessKind.Python && encoding.CodePage != 65001) throw new ArgumentException("Python-Prozesse verwenden utf-8 als Ausgabekodierung.");
        var start = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = request.WorkingDirectory,
            CreateNoWindow = !request.ShowWindow, RedirectStandardOutput = request.CaptureOutput, RedirectStandardError = request.CaptureOutput };
        if (request.CaptureOutput) { start.StandardOutputEncoding = encoding; start.StandardErrorEncoding = encoding; }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var variable in request.Environment) start.Environment[variable.Key] = variable.Value;
        return start;
    }
    private ExternalProcessRecord Find(string handle)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _records.TryGetValue(handle, out var record) ? record : throw new KeyNotFoundException("Prozesskennung gehört nicht zu diesem Prüflauf oder wurde bereits freigegeben.");
        }
    }
    public ProcessSnapshot Snapshot(string handle) => Find(handle).Snapshot();
    public async Task<ProcessSnapshot> WaitAsync(string handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        var record = Find(handle);
        try { await record.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (record.Bound) record.Stop();
            throw;
        }
        return record.Snapshot();
    }
    public void Stop(string handle) => Find(handle).Stop();
    public void CloseOwner(string owner)
    {
        lock (_gate)
            foreach (var record in _records.Values.Where(r => r.Bound && r.Owner == owner).ToList()) Release(record, stop: true);
    }
    private void Release(ExternalProcessRecord record, bool stop)
    {
        if (stop) record.Stop();
        _records.Remove(record.Handle);
        record.ReleaseWhenComplete();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var record in _records.Values.ToList()) Release(record, record.Bound);
        }
    }
}
