/**
 * @file
 * @brief Definiert Prozessaufträge, Ergebniszustände und die laufbezogene Verwaltungsgrenze.
 */

using Testumgebung.Domain.Definitions;

namespace Testumgebung.Domain.Processes;

/// <summary>Explicit process lifecycle and argument contract; no command shell is inferred.</summary>
public enum ExternalProcessKind { Executable, Python, ManagedEntryPoint, ManagedMain }
public sealed record ProcessLaunch(ExternalProcessKind Kind, string File, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, bool Bound = true,
    bool ShowWindow = false, bool CaptureOutput = true, int OutputLimit = 65536, string? TypeName = null,
    string? PythonExecutable = null, string OutputEncoding = "utf-8");
/// <summary>Each stream consumes matched text independently; patterns never cross stdout/stderr.</summary>
public enum ProcessOutputStream { StandardOutput, StandardError }
public sealed record ProcessOutputMatch(string Value);
public sealed record ProcessSnapshot(string Handle, int ProcessId, bool Completed, int? ExitCode,
    string StandardOutput, string StandardError, bool OutputTruncated, bool Stopped, string? HostError);

/// <summary>One session per test run. Handles cannot be used by another station or run.</summary>
public interface IProcessSession : IDisposable
{
    ProcessSnapshot Start(ProcessLaunch request, string owner, string workerDirectory, string? pythonExecutable, CancellationToken cancellationToken = default);
    ProcessSnapshot Snapshot(string handle);
    Task<ProcessSnapshot> WaitAsync(string handle, TimeSpan timeout, CancellationToken cancellationToken);
    Task<ProcessOutputMatch> WaitForOutputAsync(string handle, string pattern, bool regex, ProcessOutputStream stream, TimeSpan timeout, CancellationToken cancellationToken);
    Task SendAsync(string handle, string text, bool closeInput, TimeSpan timeout, CancellationToken cancellationToken);
    void Stop(string handle);
    void CloseOwner(string owner);
}
