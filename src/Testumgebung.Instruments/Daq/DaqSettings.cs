namespace Testumgebung.Instruments;

/// <summary>DAQ mappings are explicit: malformed or duplicate safety/channel assignments must never disappear silently.</summary>
internal static class DaqSettings
{
    public static Dictionary<string,string> Map(InstrumentOptions options, string name)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (options.Text(name) ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
                throw new InstrumentException($"{name}: Zuordnung als Kanal=Wert angeben; ungültig: {entry}.");
            if (!result.TryAdd(pair[0], pair[1])) throw new InstrumentException($"{name}: {pair[0]} wurde mehrfach definiert.");
        }
        return result;
    }
}
