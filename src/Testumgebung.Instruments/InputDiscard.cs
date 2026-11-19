using System.Diagnostics;
using System.Text;

namespace Testumgebung.Instruments;

public sealed class CustomInputDiscardOptions
{
    public int SettleMilliseconds { get; set; } = 20;
}

/// <summary>Bounded diagnostic data; never retain the entire discarded input stream.</summary>
public sealed record DiscardedInput(long ByteCount, byte[] Preview);

internal static class InputDiscard
{
    internal static async Task<DiscardedInput> DrainAsync(TimeSpan settleTime,
        Func<TimeSpan, CancellationToken, Task<byte[]>> read, CancellationToken token)
    {
        if (settleTime < TimeSpan.Zero || settleTime > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(settleTime));
        var watch = Stopwatch.StartNew();
        long total = 0;
        var preview = new List<byte>(200);
        do
        {
            token.ThrowIfCancellationRequested();
            var remaining = settleTime - watch.Elapsed;
            var slice = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, 10));
            var data = await read(slice, token).ConfigureAwait(false);
            total += data.Length;
            preview.AddRange(data.Take(Math.Max(0, 200 - preview.Count)));
            if (total > 1_048_576 || watch.Elapsed >= settleTime + TimeSpan.FromMilliseconds(250)) throw new InstrumentException("Eingangspuffer liefert fortlaufend Daten; vor dem Senden keine Ruhe erreicht.");
            if (data.Length == 0 && watch.Elapsed >= settleTime) break;
            if (data.Length == 0) await Task.Delay(1, token).ConfigureAwait(false);
        } while (true);
        return new(total, preview.ToArray());
    }

    internal static string Describe(DiscardedInput input)
    {
        var text = Encoding.ASCII.GetString(input.Preview).Replace("\r", "\\r").Replace("\n", "\\n");
        return $"Eingang verworfen: {input.ByteCount} Bytes; „{text}“{(input.ByteCount > input.Preview.Length ? " …" : "")}";
    }
}
