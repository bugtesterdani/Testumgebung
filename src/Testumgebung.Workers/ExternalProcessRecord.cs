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
    private readonly ProcessOutputBuffer _liveOutput = new(), _liveError = new();
    private readonly ProcessOutputFeed _feedOutput = new(), _feedError = new();
    private readonly SemaphoreSlim _inputLock = new(1);
    private readonly bool _capture;
    private bool _inputClosed;
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
        _capture = request.CaptureOutput; _limit = request.OutputLimit; ProcessId = process.Id;
        Completion = CompleteAsync(request.CaptureOutput);
    }
    private async Task CompleteAsync(bool capture)
    {
        var output = capture ? DrainAsync(_process.StandardOutput, _output, _liveOutput, _feedOutput) : Task.CompletedTask;
        var error = capture ? DrainAsync(_process.StandardError, _error, _liveError, _feedError) : Task.CompletedTask;
        await _process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        var file = Path.Combine(_directory, "error.txt");
        if (File.Exists(file)) _hostError = File.ReadAllText(file);
    }
    private async Task DrainAsync(StreamReader reader, StringBuilder target, ProcessOutputBuffer live, ProcessOutputFeed feed)
    {
        var bytes = new byte[4096];
        var buffer = new char[reader.CurrentEncoding.GetMaxCharCount(bytes.Length)];
        var decoder = reader.CurrentEncoding.GetDecoder();
        try { while (true)
        {
            // StreamReader may wait to fill its requested character count. Read
            // available bytes instead, preserving split multibyte characters.
            var read = await reader.BaseStream.ReadAsync(bytes).ConfigureAwait(false);
            var count = decoder.GetChars(bytes, 0, read, buffer, 0, flush: read == 0);
            lock (_gate)
            {
                var keep = Math.Min(count, _limit - target.Length);
                target.Append(buffer, 0, keep);
                if (keep < count) _truncated = true;
            }
            live.Append(buffer.AsSpan(0, count));
            feed.Append(buffer.AsSpan(0, count));
            if (read == 0) break;
        } }
        finally { live.Close(); feed.Close(); }
    }
    public Task<ProcessOutputChunk> ReadOutputAsync(ProcessOutputStream stream, long offset, CancellationToken token)
    {
        if (!_capture) throw new InvalidOperationException("Live-Ausgabe benötigt aktivierte stdout/stderr-Erfassung.");
        return (stream switch { ProcessOutputStream.StandardOutput => _feedOutput, ProcessOutputStream.StandardError => _feedError,
            _ => throw new ArgumentOutOfRangeException(nameof(stream)) }).ReadAsync(offset, token);
    }
    public Task<ProcessOutputMatch> WaitForOutputAsync(string pattern, bool regex, ProcessOutputStream stream, CancellationToken cancellationToken)
    {
        if (!_capture) throw new InvalidOperationException("Warten benötigt aktivierte stdout/stderr-Erfassung.");
        return (stream switch { ProcessOutputStream.StandardOutput => _liveOutput, ProcessOutputStream.StandardError => _liveError,
            _ => throw new ArgumentOutOfRangeException(nameof(stream)) }).WaitAsync(pattern, regex, cancellationToken);
    }
    public async Task SendAsync(string text, bool closeInput, CancellationToken cancellationToken)
    {
        await _inputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_inputClosed || _process.HasExited) throw new InvalidOperationException("Prozesseingabe ist geschlossen oder Prozess beendet.");
            await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (closeInput) { _process.StandardInput.Close(); _inputClosed = true; }
        }
        finally { _inputLock.Release(); }
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
