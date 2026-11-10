/**
 * @file
 * @brief Hält Kurvenversionen und ihre Herkunft; gespeicherte Versionen dienen als Referenzen im Prüfprogramm.
 */

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Testumgebung.Domain.Curves;

/// <summary>Kanal einer Ausgabekurve (z. B. L1/L2/L3) mit Einheit und Samples.</summary>
public sealed record CurveChannel(string Id, string Unit, double[] Samples)
{
    public double Min => Samples.Length == 0 ? 0 : Samples.Min();
    public double Max => Samples.Length == 0 ? 0 : Samples.Max();

    public double Rms()
    {
        if (Samples.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in Samples) sum += sample * sample;
        return Math.Sqrt(sum / Samples.Length);
    }
}

/// <summary>Entstehungsweg einer Kurve (Abschnitt 8): Datei, Messung oder Editor/Generator.</summary>
public enum CurveOrigin
{
    Generator,
    FileImport,
    Measurement,
}

/// <summary>
/// Ausgabekurve in einer Version. Alle Kanäle teilen die Zeitbasis: feste Abtastrate oder explizite Zeitpunkte.
/// Gespeicherte Versionen sind unveränderlich; Bearbeitungen erzeugen eine neue Version mit Historie.
/// </summary>
public sealed class CurveDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }

    /// <summary>Abtastrate in Hz (feste Zeitbasis). Bei expliziten Zeitpunkten informativ (0 = keine).</summary>
    public double SampleRate { get; init; }

    /// <summary>Explizite Zeitpunkte in Sekunden (gemeinsam für alle Kanäle); null = feste Abtastrate.</summary>
    public double[]? Times { get; init; }

    public required IReadOnlyList<CurveChannel> Channels { get; init; }

    public CurveOrigin Origin { get; init; }

    /// <summary>Herkunft, z. B. „Generator Sinus 325 V, 50 Hz“ oder „Datei beispiel.csv (SHA-256 …)“.</summary>
    public string SourceText { get; init; } = "";

    /// <summary>Bearbeitungsschritte in Reihenfolge (reproduzierbar).</summary>
    public IReadOnlyList<string> History { get; init; } = [];

    public string CreatedBy { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Qualitätshinweis (z. B. Warnungen aus Qualitätscodes des Imports); null = gültig.</summary>
    public string? Quality { get; init; }

    public int Count => Channels.Count == 0 ? 0 : Channels[0].Samples.Length;

    public bool HasExplicitTimes => Times is not null;

    public string Key => $"{Id}@{Version}";

    /// <summary>Zeitpunkt eines Samples in Sekunden.</summary>
    public double TimeOf(int index) => Times is { } samples ? samples[index] : index / SampleRate;

    public double Duration => Count == 0 ? 0 : Times is { } samples ? samples[^1] - samples[0] + (samples.Length > 1 ? samples[^1] - samples[^2] : 0) : Count / SampleRate;

    /// <summary>Wert eines Kanals zur Zeit t (lineare Interpolation; außerhalb: Randwert).</summary>
    public double ValueAt(int channel, double t)
    {
        var samples = Channels[channel].Samples;
        if (samples.Length == 0) return 0;
        if (Times is null)
        {
            var xValue = t * SampleRate;
            if (xValue <= 0) return samples[0];
            if (xValue >= samples.Length - 1) return samples[^1];
            var index = (int)xValue;
            return samples[index] + (samples[index + 1] - samples[index]) * (xValue - index);
        }
        var times = Times;
        if (t <= times[0]) return samples[0];
        if (t >= times[^1]) return samples[^1];
        var index2 = Array.BinarySearch(times, t);
        if (index2 >= 0) return samples[index2];
        index2 = ~index2;
        var frequency = (t - times[index2 - 1]) / (times[index2] - times[index2 - 1]);
        return samples[index2 - 1] + (samples[index2] - samples[index2 - 1]) * frequency;
    }

    /// <summary>Inhaltshash (Zeitbasis, Kanäle, Einheiten, Samples) – unabhängig von Name/Beschreibung.</summary>
    public string ContentHash()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text + "\n"); sha.AppendData(bytes); }
        void Doubles(double[] samples)
        {
            var buf = new byte[8];
            foreach (var value in samples)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(buf, value);
                sha.AppendData(buf);
            }
        }
        Text(SampleRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (Times is { } times)
        {
            Text("times");
            Doubles(times);
        }
        foreach (var curveChannel in Channels)
        {
            Text(curveChannel.Id);
            Text(curveChannel.Unit);
            Doubles(curveChannel.Samples);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Neue (noch nicht gespeicherte) Fassung mit geänderten Daten und angehängtem Bearbeitungsschritt.</summary>
    public CurveDefinition Derive(string operation, IReadOnlyList<CurveChannel>? channels = null, double? sampleRate = null, double[]? times = null, bool keepTimes = true)
        => new()
        {
            Id = Id,
            Name = Name,
            Version = Version,
            Description = Description,
            SampleRate = sampleRate ?? SampleRate,
            Times = times ?? (keepTimes ? Times : null),
            Channels = channels ?? Channels,
            Origin = Origin,
            SourceText = SourceText,
            History = [.. History, operation],
            CreatedBy = CreatedBy,
            CreatedAt = CreatedAt,
            Quality = Quality,
        };

    public CurveDefinition WithVersion(string version, string? name = null, string? id = null, string? createdBy = null, DateTimeOffset? createdAt = null, string? description = null)
        => new()
        {
            Id = id ?? Id,
            Name = name ?? Name,
            Version = version,
            Description = description ?? Description,
            SampleRate = SampleRate,
            Times = Times,
            Channels = Channels,
            Origin = Origin,
            SourceText = SourceText,
            History = History,
            CreatedBy = createdBy ?? CreatedBy,
            CreatedAt = createdAt ?? CreatedAt,
            Quality = Quality,
        };

    /// <summary>Für das Ausgabegerät.</summary>
    public Testumgebung.Contracts.OutputCurve ToOutput()
        => new(Key, SampleRate, Times, Channels.Select(curveChannel => new Testumgebung.Contracts.OutputChannelData(curveChannel.Id, curveChannel.Unit, curveChannel.Samples)).ToList());

    public override string ToString() => $"{Name} v{Version} ({Channels.Count} Kanäle, {Count} Samples)";
}

/// <summary>Kurvenbibliothek eines Projekts: alle gespeicherten Versionen (unveränderlich).</summary>
public sealed class CurveLibrary
{
    private readonly List<CurveDefinition> _versions = [];

    public IReadOnlyList<CurveDefinition> Versions => _versions;

    public IEnumerable<string> Ids => _versions.Select(curve => curve.Id).Distinct();

    public CurveDefinition? Find(string id, string? version = null)
        => version is null or "" or "neueste"
            ? _versions.Where(curve => curve.Id == id).OrderBy(curve => curve.Version, VersionComparer.Instance).LastOrDefault()
            : _versions.FirstOrDefault(curve => curve.Id == id && curve.Version == version);

    public IReadOnlyList<CurveDefinition> VersionsOf(string id) => _versions.Where(curve => curve.Id == id).OrderBy(curve => curve.Version, VersionComparer.Instance).ToList();

    /// <summary>Version speichern. Eine bestehende Version wird nie überschrieben.</summary>
    public void Add(CurveDefinition curve)
    {
        if (_versions.Any(curve2 => curve2.Id == curve.Id && curve2.Version == curve.Version))
            throw new InvalidOperationException($"Kurve {curve.Id} v{curve.Version} existiert bereits – Versionen sind unveränderlich.");
        if (string.IsNullOrWhiteSpace(curve.Id) || string.IsNullOrWhiteSpace(curve.Version)) throw new InvalidOperationException("Kurven-Id und Version erforderlich.");
        _versions.Add(curve);
    }

    public string NextVersion(string id)
    {
        var last = Find(id);
        if (last is null) return "0.1";
        var parts = last.Version.Split('.');
        return int.TryParse(parts[^1], out var count) ? string.Join('.', parts[..^1].Append((count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) : last.Version + ".1";
    }
}

/// <summary>Vergleicht Versionen numerisch je Abschnitt (0.10 &gt; 0.9).</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var parts = (x ?? "").Split('.');
        var parts2 = (y ?? "").Split('.');
        for (var index = 0; index < Math.Max(parts.Length, parts2.Length); index++)
        {
            var pa = index < parts.Length ? parts[index] : "0";
            var pb = index < parts2.Length ? parts2[index] : "0";
            var parsedValue = int.TryParse(pa, out var na) && int.TryParse(pb, out var nb) ? na.CompareTo(nb) : string.CompareOrdinal(pa, pb);
            if (parsedValue != 0) return parsedValue;
        }
        return 0;
    }
}
