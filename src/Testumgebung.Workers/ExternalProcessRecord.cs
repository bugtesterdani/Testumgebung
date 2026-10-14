/**
 * @file
 * @brief Liest beide Prozessausgaben gleichzeitig mit begrenztem Speicher.
 */

using System.Diagnostics;
using System.Text;
using Testumgebung.Domain.Processes;

namespace Testumgebung.Workers;

/// <summary>Drains both streams continuously while retaining bounded output for one invocation.</summary>
internal sealed class ExternalProcessRecord : IDisposable
{
    private readonly Process _process;
    private readonly ProcessJob? _job;
    private readonly string _directory;
    private readonly int _limit;
    private readonly object _gate = new();
    private readonly StringBuilder _output = new(), _error = new();
    private bool _truncated;
    private int _stopped, _disposed;
    private string? _hostError;
    public string Handle { get; } = Guid.NewGuid().ToString("N");
    public string Owner { get; }
    public bool Bound { get; }
    private int ProcessId { get; }
    public Task Completion { get; }

    public ExternalProcessRecord(Process process, ProcessJob? job, string directory, string owner, ProcessLaunch request)
    {
        _process = process; _job = job; _directory = directory; Owner = owner; Bound = request.Bound;
        _limit = request.OutputLimit; ProcessId = process.Id;
        Completion = CompleteAsync(request.CaptureOutput);
    }
    private async Task CompleteAsync(bool capture)
    {
        var output = capture ? DrainAsync(_process.StandardOutput, _output) : Task.CompletedTask;
        var error = capture ? DrainAsync(_process.StandardError, _error) : Task.CompletedTask;
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        var file = Path.Combine(_directory, "error.txt");
        if (File.Exists(file)) _hostError = File.ReadAllText(file);
    }
    private async Task DrainAsync(StreamReader reader, StringBuilder target)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
        {
            lock (_gate)
            {
                var keep = Math.Min(count, _limit - target.Length);
                target.Append(buffer, 0, keep);
                if (keep < count) _truncated = true;
            }
        }
    }
    public ProcessSnapshot Snapshot()
    {
        lock (_gate)
            return new(Handle, ProcessId, Completion.IsCompleted,
                Completion.IsCompleted && _process.HasExited ? _process.ExitCode : null,
                _output.ToString(), _error.ToString(), _truncated, Volatile.Read(ref _stopped) != 0, _hostError ?? Completion.Exception?.GetBaseException().Message);
    }
    public void Stop()
    {
        if (!_process.HasExited) Interlocked.Exchange(ref _stopped, 1);
        // Closing the job also reaps children after the original target has exited.
        _job?.Dispose();
        if (_job is not null || _process.HasExited) return;
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (_process.HasExited) { }
        catch (System.ComponentModel.Win32Exception) when (_process.HasExited) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _job?.Dispose();
        _process.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
    public void ReleaseWhenComplete()
    {
        _ = Completion.ContinueWith(task => { _ = task.Exception; Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
