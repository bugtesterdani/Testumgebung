/**
 * @file
 * @brief Wartet ohne Polling auf konsumierbare Prozesssignale.
 */
using System.Text;
using System.Text.RegularExpressions;
using Testumgebung.Domain.Processes;

namespace Testumgebung.Workers;

/// <summary>Bounded unread stream with one ordered consumer and chunk-change notifications.</summary>
internal sealed class ProcessOutputBuffer
{
    private const int Capacity = 1048576;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _consumer = new(1);
    private readonly StringBuilder _text = new();
    private TaskCompletionSource _changed = Signal();
    private bool _closed, _overflow;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            // Never silently skip an unread prompt when the memory bound is reached.
            if (text.Length > Capacity - _text.Length) _overflow = true;
            else _text.Append(text);
            Notify();
        }
    }
    public void Close() { lock (_gate) { _closed = true; Notify(); } }
    private void Notify() { var previous = _changed; _changed = Signal(); previous.TrySetResult(); }
    public async Task<ProcessOutputMatch> WaitAsync(string pattern, bool regex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pattern)) throw new ArgumentException("Suchmuster darf nicht leer sein.", nameof(pattern));
        var expression = regex ? new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) : null;
        await _consumer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                lock (_gate)
                {
                    if (_overflow) throw new InvalidOperationException("Mehr als 1048576 ungelesene Ausgabezeichen; Prozessdialog nicht mehr eindeutig.");
                    var text = _text.ToString();
                    var match = Find(text, pattern, expression);
                    if (match is { } found)
                    {
                        _text.Remove(0, found.Index + found.Value.Length);
                        return new(found.Value);
                    }
                    if (_closed) throw new EndOfStreamException("Prozessausgabe beendet, bevor das Suchmuster gefunden wurde.");
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _consumer.Release(); }
    }
    private static (int Index, string Value)? Find(string text, string pattern, Regex? expression)
    {
        if (expression is null)
        {
            var index = text.IndexOf(pattern, StringComparison.Ordinal);
            return index < 0 ? null : (index, pattern);
        }
        var match = expression.Match(text);
        if (!match.Success) return null;
        if (match.Length == 0) throw new ArgumentException("Regex muss mindestens ein Zeichen erkennen.");
        return (match.Index, match.Value);
    }
}
