/** @file @brief Nicht konsumierender Live-Lesecursor neben dem bestehenden Prozessdialog. */
using System.Text;
using Testumgebung.Domain.Processes;
namespace Testumgebung.Workers;

internal sealed class ProcessOutputFeed
{
    private const int Capacity = 1048576;
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private long _start;
    private bool _closed;
    private TaskCompletionSource _changed = Signal();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Append(ReadOnlySpan<char> value)
    {
        lock (_gate)
        {
            _text.Append(value);
            var discard = Math.Max(0, _text.Length - Capacity);
            _text.Remove(0, discard); _start += discard;
            Notify();
        }
    }
    public void Close() { lock (_gate) { _closed = true; Notify(); } }
    private void Notify() { var previous = _changed; _changed = Signal(); previous.TrySetResult(); }
    public async Task<ProcessOutputChunk> ReadAsync(long offset, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                if (offset < _start) throw new InvalidOperationException("Live-Ausgabe konnte nicht schnell genug gelesen werden (mehr als 1 Mi Zeichen Rückstand). Ausgabe in eine Datei umleiten und diese verfolgen.");
                var end = _start + _text.Length;
                if (offset > end) throw new ArgumentOutOfRangeException(nameof(offset));
                var count = (int)Math.Min(65536, end - offset);
                if (count > 0 || _closed) return new(_text.ToString((int)(offset - _start), count), offset + count, _closed && offset + count == end);
                changed = _changed.Task;
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }
}
