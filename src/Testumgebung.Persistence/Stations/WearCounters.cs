using System.Text.Json;

namespace Testumgebung.Persistence.Stations;

public sealed class WearCounterConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public long WarningAt { get; set; }
    public long StopAt { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) return "Jeder Verschleißzähler benötigt einen Namen und eine Kennung.";
        if (WarningAt < 0 || StopAt < 0) return "Grenzen für Verschleißzähler müssen positiv sein oder 0 (ausgeschaltet).";
        return StopAt > 0 && WarningAt >= StopAt ? "Die Warngrenze muss unter der Sperrgrenze liegen." : null;
    }
}

public sealed record WearReset(string Station, string Counter, string Name, long PreviousCount, string User, DateTimeOffset At);
public sealed record WearReading(WearCounterConfig Counter, long Count)
{
    public bool Blocked => Counter.StopAt > 0 && Count >= Counter.StopAt;
    public bool Warning => Counter.WarningAt > 0 && Count >= Counter.WarningAt;
}

/// <summary>Speichert Zählerstände getrennt von der bearbeitbaren Prüfplatzkonfiguration.</summary>
public sealed class WearCounterStore(string? path = null)
{
    private readonly object _gate = new();
    private State _memory = new();

    private sealed class State
    {
        public Dictionary<string, long> Counts { get; init; } = new(StringComparer.Ordinal);
        public List<WearReset> Resets { get; init; } = [];
    }

    public IReadOnlyList<WearReading> Read(IReadOnlyList<WearCounterConfig> counters)
    {
        lock (_gate) return Readings(ReadState(), counters);
    }

    public IReadOnlyList<WearReset> Resets(string station)
    {
        lock (_gate) return ReadState().Resets.Where(reset => reset.Station == station).Reverse().ToArray();
    }

    public IReadOnlyList<WearReading> Increment(IReadOnlyList<WearCounterConfig> counters)
        => Update(state =>
        {
            foreach (var counter in counters)
            {
                var count = state.Counts.GetValueOrDefault(counter.Id);
                state.Counts[counter.Id] = count == long.MaxValue ? count : count + 1;
            }
            return Readings(state, counters);
        });

    public WearReset Reset(string station, WearCounterConfig counter, string user, DateTimeOffset now)
        => Update(state =>
        {
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Für das Rücksetzen ist ein Benutzer erforderlich.", nameof(user));
            var previous = state.Counts.GetValueOrDefault(counter.Id);
            var reset = new WearReset(station, counter.Id, counter.Name, previous, user, now);
            state.Counts[counter.Id] = 0;
            state.Resets.Add(reset);
            return reset;
        });

    private static WearReading[] Readings(State state, IReadOnlyList<WearCounterConfig> counters)
        => counters.Select(counter => new WearReading(counter, state.Counts.GetValueOrDefault(counter.Id))).ToArray();

    private State ReadState()
    {
        if (path is null) return _memory;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<State>(file) ?? throw new InvalidDataException("Verschleißzählerdatei ist leer.");
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    private T Update<T>(Func<State, T> change)
    {
        lock (_gate)
        {
            if (path is null) return change(_memory);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            var state = ReadState();
            var result = change(state);
            var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, state);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return result;
        }
    }
}
