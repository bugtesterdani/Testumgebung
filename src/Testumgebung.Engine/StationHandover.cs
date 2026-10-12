using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine;

/// <summary>Übergabekanäle einer gemeinsamen Auftragssitzung; keine globalen Freigaben.</summary>
public sealed class StationHandover : IStationHandover
{
    private sealed class Channel(int first)
    {
        public readonly int First = first;
        public int Turn = first;
        public int? Holder;
        public TaskCompletionSource Changed = NewSignal();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

    public async Task WaitAsync(int side, string channel, int firstSide, CancellationToken cancellationToken)
    {
        if (side is not (1 or 2) || firstSide is not (1 or 2) || string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Seite muss 1 oder 2 sein; Freigabekanal darf nicht leer sein.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                if (!_channels.TryGetValue(channel, out var state)) _channels[channel] = state = new Channel(firstSide);
                if (state.First != firstSide) throw new InvalidOperationException("Unterschiedliche erste Seiten im selben Freigabekanal.");
                if (state.Holder == side) throw new InvalidOperationException("Freigabe bereits gehalten. Zuerst die andere Seite freigeben.");
                if (state.Turn == side && state.Holder is null) { state.Holder = side; return; }
                changed = state.Changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Release(int side, string channel)
    {
        lock (_gate)
        {
            if (!_channels.TryGetValue(channel, out var state) || state.Holder != side)
                throw new InvalidOperationException("Die Seite hält keine Freigabe für diesen Kanal.");
            state.Holder = null;
            state.Turn = side == 1 ? 2 : 1;
            var changed = state.Changed;
            state.Changed = NewSignal();
            changed.TrySetResult();
        }
    }
}
