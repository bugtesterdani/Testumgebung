using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;

namespace Testumgebung.Persistence.Results;

/// <summary>Version 2 stores only raw trace samples, one axis, and the frozen assessment/configuration.</summary>
public static class TraceResultStorage
{
    public const string Prefix = "TraceRaw:1:";
    private sealed record StoredTrace(string Snapshot, Dictionary<string, string> Ids);

    public static IReadOnlyList<ResultWaveform> Collapse(IReadOnlyList<ResultWaveform> waveforms)
    {
        var stored = new List<ResultWaveform>();
        foreach (var group in waveforms.GroupBy(wave => wave.EventSequence))
        {
            var owner = group.FirstOrDefault(wave => wave.Recording is not null);
            if (owner?.Recording is not { } source) { stored.AddRange(group); continue; }
            var ids = group.ToDictionary(wave => wave.Data.Channel, wave => wave.Id, StringComparer.Ordinal);
            var quality = Prefix + JsonSerializer.Serialize(new StoredTrace(source.Snapshot, ids));
            var sync = Prefix + owner.EventSequence;
            for (var index = 0; index < source.RawChannels.Count; index++)
            {
                var raw = source.RawChannels[index];
                var name = raw.Channel + " · Messwert";
                var id = ids.GetValueOrDefault(name, owner.Id + "/raw/" + index);
                stored.Add(new(id, owner.EventSequence, owner.NodeId, raw with
                {
                    Channel = name, Quality = index == 0 ? quality : null,
                    TimeValues = index == 0 ? raw.TimeValues : null, SyncGroup = sync
                }));
            }
        }
        return stored;
    }

    public static List<ResultWaveform> Expand(IReadOnlyList<ResultWaveform> waveforms, ICollection<string>? problems = null)
    {
        var expanded = new List<ResultWaveform>();
        foreach (var group in waveforms.GroupBy(wave => wave.EventSequence))
        {
            var owner = group.FirstOrDefault(wave => wave.Data.Quality?.StartsWith(Prefix, StringComparison.Ordinal) == true);
            if (owner is null) { expanded.AddRange(group); continue; }
            try { expanded.AddRange(ExpandGroup(group.ToArray(), owner)); }
            catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException)
            {
                problems?.Add("Aufnahme kann nicht rekonstruiert werden: " + error.Message);
                expanded.AddRange(group.Select(wave => wave with { Data = wave.Data with { DisplayWarning = "Rohdaten vorhanden; Rekonstruktion fehlgeschlagen: " + error.Message } }));
            }
        }
        return expanded;
    }

    private static IEnumerable<ResultWaveform> ExpandGroup(ResultWaveform[] group, ResultWaveform owner)
    {
        var stored = JsonSerializer.Deserialize<StoredTrace>(owner.Data.Quality![Prefix.Length..]) ?? throw new FormatException("Aufnahme fehlt.");
        var snapshot = TraceRecordingSnapshot.Parse(stored.Snapshot);
        var raw = snapshot.Channels.Select(channel =>
        {
            var wave = group.Single(item => item.Data.Channel == channel.Name + " · Messwert");
            return wave.Data with { Channel = channel.Name, Quality = null, TimeValues = owner.Data.TimeValues, SyncGroup = null };
        }).ToArray();
        var recording = new TraceRecordingData(stored.Snapshot, raw);
        var display = snapshot.Reconstruct(raw);
        var index = 0;
        foreach (var wave in display.Channels)
            yield return new ResultWaveform(stored.Ids.GetValueOrDefault(wave.Channel, owner.Id + "/display/" + index),
                owner.EventSequence, owner.NodeId, wave) { Recording = index++ == 0 ? recording : null };
    }
}
