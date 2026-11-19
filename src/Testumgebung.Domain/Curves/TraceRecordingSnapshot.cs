using System.Text.Json;
using Testumgebung.Contracts;

namespace Testumgebung.Domain.Curves;

public sealed record FrozenTraceCurve(string Key, string Hash, double[] Times, double[] Lower, double[] Upper)
{
    internal CurveDefinition ToCurve(string unit)
    {
        var separator = Key.LastIndexOf('@');
        return new() { Id = separator < 0 ? Key : Key[..separator], Name = Key, Version = separator < 0 ? "1" : Key[(separator + 1)..],
            Times = Times, Channels = [new("Untergrenze", unit, Lower), new("Obergrenze", unit, Upper)] };
    }
}

public sealed record TraceReplayChannel(string Name, string Unit, FrozenTraceCurve? Limits, FrozenTraceCurve? AbortLimits,
    double? SafetyMinimum, double? SafetyMaximum);

/// <summary>Frozen configuration and authoritative assessment, never copies of the sample time axis.</summary>
public sealed record TraceRecordingSnapshot(int AlgorithmVersion, TraceReplayChannel[] Channels,
    SlopeLimits? Slope, string? LowerFormula, string? UpperFormula, double? Duration, bool TouchAllowed,
    int MaxPoints, double MaxGap, int MinimumPoints, double MinimumDuration, TraceTargetBand? Target,
    double? StopPercent, double? StopAbsolute, int AssessedRawPoints, bool Stopped, bool Complete,
    EnvelopeTraceMetadata[] Assessments)
{
    public const int CurrentAlgorithmVersion = 1;

    public static TraceRecordingSnapshot Parse(string json)
    {
        var snapshot = JsonSerializer.Deserialize<TraceRecordingSnapshot>(json) ?? throw new FormatException("Aufnahme-Momentaufnahme fehlt.");
        if (snapshot.Channels is null || snapshot.Channels.Length is < 1 or > 8 || snapshot.Assessments is null
            || snapshot.AssessedRawPoints is < 0 or > 100000 || snapshot.MaxPoints is < 2 or > 100000)
            throw new FormatException("Ungültige Aufnahme-Momentaufnahme.");
        return snapshot;
    }

    public WaveformSet Reconstruct(IReadOnlyList<Waveform> raw)
    {
        ValidateRaw(raw);
        var definitions = Channels.Select(channel => new TraceChannelDefinition(channel.Name, channel.Unit, channel.Limits?.ToCurve(channel.Unit))).ToArray();
        // Disable stopping rules for display replay: saved violations and the saved rating remain authoritative.
        var replay = new EnvelopeRecording(definitions, raw[0].Start, MaxPoints, MaxGap, MinimumDuration, TouchAllowed, null,
            Duration, Slope, LowerFormula is null ? null : new FormulaLimits(LowerFormula, UpperFormula!), null, MinimumPoints, null, false);
        for (var index = 0; index < AssessedRawPoints; index++)
            replay.Add(raw[0].TimeOf(index), raw.Select(channel => channel.Values[index]).ToArray(), raw[0].Start);
        var calculated = replay.Evaluate(Stopped);
        var changed = AlgorithmVersion != CurrentAlgorithmVersion || AssessmentChanged(calculated.Channels);
        var waves = calculated.Data.Channels.Select(wave =>
        {
            var metadata = EnvelopeTraceMetadata.Decode(wave.Quality);
            var saved = Assessments.FirstOrDefault(assessment => assessment.Channel == wave.Channel);
            return metadata is not null && saved is not null
                ? wave with { Quality = (saved with { OriginalTimes = metadata.OriginalTimes }).Encode() } : wave;
        }).ToList();
        if (changed)
        {
            const string warning = "Neuberechnete Auswertung weicht von der gespeicherten Bewertung ab. Gespeicherte Bewertung und Grenzverletzungen bleiben maßgeblich.";
            if (waves.Count > 0) waves[0] = waves[0] with { DisplayWarning = warning };
            else if (raw.Count > 0) raw = raw.Select(wave => wave with { DisplayWarning = warning }).ToArray();
        }
        return AddMeasurements(waves, raw);
    }

    private bool AssessmentChanged(IReadOnlyList<EnvelopeTraceMetadata> calculated)
        => Assessments.Any(saved => calculated.FirstOrDefault(channel => channel.Channel == saved.Channel) is not { } current
            || JsonSerializer.Serialize(saved.Details) != JsonSerializer.Serialize(current.Details)
            || JsonSerializer.Serialize(saved.SuppressedSlopes) != JsonSerializer.Serialize(current.SuppressedSlopes));

    private void ValidateRaw(IReadOnlyList<Waveform> raw)
    {
        if (raw.Count != Channels.Length || raw.Any(wave => wave.Count > MaxPoints || wave.TimeValues is null
            || wave.Count != raw[0].Count || wave.TimeValues.Count != wave.Count || AssessedRawPoints > wave.Count))
            throw new FormatException("Rohmesswerte und Aufnahme-Momentaufnahme passen nicht zusammen.");
    }

    public WaveformSet AddMeasurements(IEnumerable<Waveform> evaluated, IReadOnlyList<Waveform> raw)
    {
        ValidateRaw(raw);
        var waves = evaluated.ToList();
        for (var index = 0; index < raw.Count; index++)
        {
            var channel = Channels[index]; var source = raw[index];
            waves.Add(source with { Channel = channel.Name + " · Messwert", Quality = null });
            AddConstant(waves, source, channel.Name + " · Sicherheit Untergrenze", channel.SafetyMinimum);
            AddConstant(waves, source, channel.Name + " · Sicherheit Obergrenze", channel.SafetyMaximum);
            if (index == 0 && Target is { } target)
            {
                AddConstant(waves, source, channel.Name + " · Zielband Untergrenze", target.Minimum);
                AddConstant(waves, source, channel.Name + " · Zielband Obergrenze", target.Maximum);
            }
            if (channel.AbortLimits is { } abort && Slope is not null)
            {
                var curve = new CurveEnvelope(abort.ToCurve(channel.Unit), channel.Unit);
                waves.Add(new(channel.Name + " · Abbruch Untergrenze", channel.Unit, 0, source.Start, source.Values.Select((_, i) => curve.At(source.TimeOf(i)).Lower).ToArray(), source.Times));
                waves.Add(new(channel.Name + " · Abbruch Obergrenze", channel.Unit, 0, source.Start, source.Values.Select((_, i) => curve.At(source.TimeOf(i)).Upper).ToArray(), source.Times));
            }
        }
        return new(waves);
    }

    private static void AddConstant(List<Waveform> waves, Waveform source, string name, double? value)
    {
        if (value is not { } bound) return;
        waves.Add(new(name, source.Unit, 0, source.Start, Enumerable.Repeat(bound, source.Count).ToArray(), source.Times));
    }

    public static WaveformSet ForDisplay(WaveformSet data)
        => data.Recording is { } recording
            ? Parse(recording.Snapshot).AddMeasurements(data.Channels, recording.RawChannels) : data;
}
