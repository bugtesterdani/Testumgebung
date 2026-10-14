/**
 * @file
 * @brief Begrenzt den Speicherbedarf großer Aufnahmen durch blockweises Lesen und temporäre Dateipuffer.
 */

using System.Collections;
using System.Runtime.InteropServices;

namespace Testumgebung.Contracts;

/// <summary>Unveränderliche Samples mit begrenztem Lesepuffer; große Aufnahmen liegen in einer temporären Datei.</summary>
public sealed class SampleBuffer : IReadOnlyList<double>
{
    public const int BlockSize = 65536;
    public const int MemoryLimit = 262144;
    public static SampleBuffer Empty { get; } = new(Array.Empty<double>());
    private readonly double[]? _memory;
    private readonly FileStream? _file;
    private readonly object _gate = new();
    private double[]? _cache;
    private int _cacheStart = -1;

    public SampleBuffer(double[] values) { _memory = values; Count = values.Length; }
    internal SampleBuffer(FileStream file, int count) { _file = file; Count = count; }
    public int Count { get; }
    public int Length => Count;
    public bool IsFileBacked => _file is not null;
    public double this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            if (_memory is not null) return _memory[index];
            lock (_gate)
            {
                if (_cacheStart < 0 || index < _cacheStart || index >= _cacheStart + _cache!.Length)
                {
                    _cacheStart = index / BlockSize * BlockSize;
                    _cache = ReadBlock(_cacheStart, Math.Min(BlockSize, Count - _cacheStart));
                }
                return _cache![index - _cacheStart];
            }
        }
    }

    public double[] ReadBlock(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > Count - count) throw new ArgumentOutOfRangeException(nameof(offset));
        var values = new double[count];
        if (_memory is not null) _memory.AsSpan(offset, count).CopyTo(values);
        else
        {
            var bytes = MemoryMarshal.AsBytes(values.AsSpan());
            var done = 0;
            while (done < bytes.Length)
            {
                var n = RandomAccess.Read(_file!.SafeFileHandle, bytes[done..], (long)offset * sizeof(double) + done);
                if (n == 0) throw new EndOfStreamException("Kurvenpuffer ist unvollständig.");
                done += n;
            }
        }
        return values;
    }

    /// <summary>Explizite Materialisierung, nur für Bearbeitung/Legacy-Adapter; der Live-Pfad liest Blöcke.</summary>
    public double[] ToArray() => _memory ?? ReadBlock(0, Count);
    public IEnumerator<double> GetEnumerator()
    {
        for (var offset = 0; offset < Count; offset += BlockSize)
        {
            var block = ReadBlock(offset, Math.Min(BlockSize, Count - offset));
            foreach (var value in block) yield return value;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Sequentieller Aufnahmespeicher. Unvollständige Puffer werden beim Abbruch sofort geschlossen/gelöscht.</summary>
public sealed class SampleBufferWriter : IDisposable
{
    private readonly double[]? _memory;
    private FileStream? _file;
    private readonly int _expected;
    private int _written;
    private bool _completed;

    public SampleBufferWriter(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _expected = count;
        if (count <= SampleBuffer.MemoryLimit) _memory = new double[count];
        else
        {
            var folder = Path.Combine(Path.GetTempPath(), "Testumgebung", "waveforms");
            Directory.CreateDirectory(folder);
            _file = new FileStream(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".samples"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096,
                FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }
    }

    public void Append(ReadOnlySpan<double> values)
    {
        if (_completed || _written > _expected - values.Length) throw new InvalidOperationException("Ungültiger Kurvenblock.");
        if (_memory is not null) values.CopyTo(_memory.AsSpan(_written));
        else _file!.Write(MemoryMarshal.AsBytes(values));
        _written += values.Length;
    }

    public SampleBuffer Complete()
    {
        if (_completed || _written != _expected) throw new InvalidOperationException("Kurvenaufnahme ist unvollständig.");
        _completed = true;
        if (_memory is not null) return new SampleBuffer(_memory);
        _file!.Flush();
        var result = new SampleBuffer(_file, _written);
        _file = null; // Besitz geht an den gemeinsamen SampleBuffer; FileStream löscht beim Freigeben.
        return result;
    }
    public void Dispose() { _file?.Dispose(); _file = null; }
}
