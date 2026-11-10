/**
 * @file
 * @brief Beschreibt Aufnahmen und Ausgabekurven einschließlich Einheiten, Zeitbasis und Geräteeignung.
 */

using System.Globalization;

namespace Testumgebung.Contracts;

/// <summary>
/// Kurve eines Kanals (Abschnitt 8): Kanal, Einheit, Zeitbasis (Abtastrate ab Start oder explizite Zeitpunkte),
/// Samples, Qualitätsinformation und Synchronisationsbezug.
/// </summary>
public sealed record Waveform
{
    public Waveform(string Channel, string Unit, double SampleRate, DateTimeOffset Start, double[] Samples,
        double[]? Times = null, string? Quality = null, string? SyncGroup = null)
        : this(Channel, Unit, SampleRate, Start, Samples.Length == 0 ? SampleBuffer.Empty : new SampleBuffer(Samples), Times, Quality, SyncGroup) { }

    public Waveform(string Channel, string Unit, double SampleRate, DateTimeOffset Start, SampleBuffer Values,
        double[]? Times = null, string? Quality = null, string? SyncGroup = null)
    {
        this.Channel = Channel; this.Unit = Unit; this.SampleRate = SampleRate; this.Start = Start;
        this.Values = Values; this.Times = Times; this.Quality = Quality; this.SyncGroup = SyncGroup;
    }
    public string Channel { get; init; }
    public string Unit { get; init; }
    public double SampleRate { get; init; }
    public DateTimeOffset Start { get; init; }
    public SampleBuffer Values { get; init; }
    /// <summary>Kompatibilitätszugriff; große Kurven ausdrücklich vollständig materialisieren.</summary>
    public double[] Samples { get => Values.ToArray(); init => Values = value.Length == 0 ? SampleBuffer.Empty : new SampleBuffer(value); }
    public SampleBuffer? TimeValues { get; init; }
    public double[]? Times { get => TimeValues?.ToArray(); init => TimeValues = value is null ? null : new SampleBuffer(value); }
    public string? Quality { get; init; }
    public string? SyncGroup { get; init; }
    public int Count => Values.Count;

    public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds(Count / SampleRate) : TimeSpan.Zero;

    /// <summary>Zeitpunkt eines Samples in Sekunden relativ zum Start.</summary>
    public double TimeOf(int index) => TimeValues is { } sampleBuffer ? sampleBuffer[index] : index / SampleRate;

    public double Rms()
    {
        if (Count == 0) return 0;
        double sum = 0;
        foreach (var sample in Values) sum += sample * sample;
        return Math.Sqrt(sum / Count);
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Channel}: {Count} Samples @ {SampleRate:0.##} Hz");
}

/// <summary>Mehrkanal-Datensatz mit gemeinsamer Zeitbasis (z. B. L1/L2/L3).</summary>
public sealed record WaveformSet(IReadOnlyList<Waveform> Channels)
{
    public override string ToString()
        => Channels.Count == 0 ? "keine Kurve"
            : string.Create(CultureInfo.InvariantCulture, $"{Channels.Count} Kanal/Kanäle × {Channels[0].Count} Samples");
}

/// <summary>Adapter, der Kurven aufnehmen kann. Kanäle eines Aufrufs haben eine gemeinsame Zeitbasis.</summary>
public interface IWaveformAdapter
{
    Task<WaveformSet> CaptureAsync(IReadOnlyList<string> channels, double sampleRate, int samples, CancellationToken cancellationToken);
}

/// <summary>Kanal einer Ausgabekurve für das Gerät.</summary>
public sealed record OutputChannelData(string Id, string Unit, double[] Samples);

/// <summary>Kurve, wie sie an ein Ausgabegerät geht (gemeinsame Zeitbasis). <see cref="Key"/> = „Id@Version“.</summary>
public sealed record OutputCurve(string Key, double SampleRate, double[]? Times, IReadOnlyList<OutputChannelData> Channels)
{
    public int Count => Channels.Count == 0 ? 0 : Channels[0].Samples.Length;

    public double Duration => Count == 0 ? 0 : Times is { } samples ? samples[^1] - samples[0] + (samples.Length > 1 ? samples[^1] - samples[^2] : 0) : Count / SampleRate;

    /// <summary>Wert bei t (periodische Wiedergabe, lineare Interpolation).</summary>
    public double ValueAt(int channel, double t)
    {
        var samples = Channels[channel].Samples;
        if (samples.Length == 0) return 0;
        var durationValue = Duration;
        if (durationValue > 0) t %= durationValue;
        if (t < 0) t += durationValue;
        if (Times is null)
        {
            var xValue = t * SampleRate;
            var index = (int)Math.Floor(xValue);
            if (index >= samples.Length - 1) return samples[^1] + (samples[0] - samples[^1]) * Math.Clamp(xValue - (samples.Length - 1), 0, 1);
            return samples[index] + (samples[index + 1] - samples[index]) * (xValue - index);
        }
        var times = Times;
        var tt = times[0] + t;
        if (tt <= times[0]) return samples[0];
        if (tt >= times[^1]) return samples[^1];
        var index2 = Array.BinarySearch(times, tt);
        if (index2 >= 0) return samples[index2];
        index2 = ~index2;
        return samples[index2 - 1] + (samples[index2] - samples[index2 - 1]) * (tt - times[index2 - 1]) / (times[index2] - times[index2 - 1]);
    }
}

/// <summary>Fähigkeiten eines Ausgabegeräts (z. B. programmierbare AC-Quelle).</summary>
public sealed record OutputCapabilities(
    string Device,
    string Unit,
    double Min,
    double Max,
    IReadOnlyList<string> Channels,
    IReadOnlyList<double> SampleRates,
    bool ExplicitTimes,
    int MaxSamples,
    double MinSampleRate = 0,
    double MaxSampleRate = 0)
{
    /// <summary>Gerät mit frei einstellbarer Abtastrate (z. B. Arbiträrgenerator, DAQ-Analogausgang) im Bereich Min…Max.</summary>
    public bool HasRateRange => MaxSampleRate > 0;

    public bool SupportsRate(double rate)
        => HasRateRange ? rate >= MinSampleRate && rate <= MaxSampleRate
            : SampleRates.Any(sampleRate => Math.Abs(sampleRate - rate) <= 1e-9 * sampleRate);

    public string RatesText => HasRateRange
        ? string.Create(CultureInfo.InvariantCulture, $"{MinSampleRate:0.###} … {MaxSampleRate:0.###}")
        : string.Join(" / ", SampleRates.Select(sampleRate => sampleRate.ToString("0.###", CultureInfo.InvariantCulture)));
}

public sealed record SuitabilityIssue(bool IsError, string Text);

public enum OutputState
{
    Idle,
    Loaded,
    Transferred,
    Running,
    Stopped,
}

/// <summary>
/// Kurvenausgabe (Abschnitt 8): getrennt in Laden (lokal prüfen und vorbereiten), Übertragen (ins Gerät), Starten und Stoppen.
/// Vor dem Übertragen wird die Geräteeignung geprüft; es gibt keine unbemerkte Skalierung oder Abtastratenänderung.
/// </summary>
public interface IWaveformOutputAdapter
{
    OutputCapabilities GetOutputCapabilities(string channel);
    OutputState GetOutputState(string channel);
    string? LoadedCurve(string channel);
    Task LoadAsync(string channel, OutputCurve curve, CancellationToken cancellationToken);
    Task TransferAsync(string channel, CancellationToken cancellationToken);
    Task StartAsync(string channel, CancellationToken cancellationToken);
    Task StopAsync(string channel, CancellationToken cancellationToken);
}

public sealed class OutputSequenceException(string message) : Exception(message);

public static class OutputSuitability
{
    /// <summary>Geräteeignung: Einheit, Kanäle, Abtastrate (exakt), Zeitbasis, Wertebereich, Länge.</summary>
    public static IReadOnlyList<SuitabilityIssue> Check(OutputCapabilities caps, OutputCurve curve)
    {
        var list = new List<SuitabilityIssue>();
        foreach (var channel in curve.Channels)
            CheckChannel(caps, channel, list);
        if (curve.Times is not null)
        {
            if (!caps.ExplicitTimes) list.Add(new(true, "Gerät unterstützt keine expliziten Zeitpunkte – Abtastrate ausdrücklich festlegen (Resampling im Editor)."));
        }
        else if (!caps.SupportsRate(curve.SampleRate))
        {
            list.Add(new(true, $"Andere Abtastrate: {curve.SampleRate:0.###} Hz, Gerät unterstützt {caps.RatesText} Hz. Anpassung ausdrücklich erforderlich – keine automatische Konvertierung."));
        }
        if (curve.Count > caps.MaxSamples) list.Add(new(true, $"{curve.Count} Samples überschreiten den Gerätespeicher ({caps.MaxSamples})."));
        if (curve.Count == 0) list.Add(new(true, "Kurve ist leer."));
        if (list.Count == 0) list.Add(new(false, $"Geräteeignung geprüft: {caps.Unit}, {(curve.Times is null ? $"{curve.SampleRate:0.###} Hz" : "explizite Zeitpunkte")}, {curve.Channels.Count} Kanal/Kanäle."));
        return list;
    }

    private static void CheckChannel(OutputCapabilities caps, OutputChannelData ch, List<SuitabilityIssue> list)
    {
        if (!caps.Channels.Contains(ch.Id)) list.Add(new(true, $"Kanal {ch.Id} gibt es am Gerät {caps.Device} nicht (vorhanden: {string.Join(", ", caps.Channels)})."));
        if (!string.Equals(ch.Unit, caps.Unit, StringComparison.Ordinal))
            list.Add(new(true, $"Kanal {ch.Id}: Einheit {ch.Unit} statt {caps.Unit} – keine automatische Umrechnung."));
        var min = ch.Samples.Length == 0 ? 0 : ch.Samples.Min();
        var max = ch.Samples.Length == 0 ? 0 : ch.Samples.Max();
        if (min < caps.Min || max > caps.Max)
            list.Add(new(true, $"Kanal {ch.Id}: Werte {min:0.###} … {max:0.###} {ch.Unit} außerhalb {caps.Min:0.###} … {caps.Max:0.###} – keine automatische Skalierung."));
    }
}
