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
        foreach (var s in Samples) sum += s * s;
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
    public double TimeOf(int index) => Times is { } t ? t[index] : index / SampleRate;

    public double Duration => Count == 0 ? 0 : Times is { } t ? t[^1] - t[0] + (t.Length > 1 ? t[^1] - t[^2] : 0) : Count / SampleRate;

    /// <summary>Wert eines Kanals zur Zeit t (lineare Interpolation; außerhalb: Randwert).</summary>
    public double ValueAt(int channel, double t)
    {
        var s = Channels[channel].Samples;
        if (s.Length == 0) return 0;
        if (Times is null)
        {
            var x = t * SampleRate;
            if (x <= 0) return s[0];
            if (x >= s.Length - 1) return s[^1];
            var i = (int)x;
            return s[i] + (s[i + 1] - s[i]) * (x - i);
        }
        var times = Times;
        if (t <= times[0]) return s[0];
        if (t >= times[^1]) return s[^1];
        var k = Array.BinarySearch(times, t);
        if (k >= 0) return s[k];
        k = ~k;
        var f = (t - times[k - 1]) / (times[k] - times[k - 1]);
        return s[k - 1] + (s[k] - s[k - 1]) * f;
    }

    /// <summary>Inhaltshash (Zeitbasis, Kanäle, Einheiten, Samples) – unabhängig von Name/Beschreibung.</summary>
    public string ContentHash()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string s) { var b = Encoding.UTF8.GetBytes(s + "\n"); sha.AppendData(b); }
        void Doubles(double[] d)
        {
            var buf = new byte[8];
            foreach (var v in d)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(buf, v);
                sha.AppendData(buf);
            }
        }
        Text(SampleRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        if (Times is { } times)
        {
            Text("times");
            Doubles(times);
        }
        foreach (var c in Channels)
        {
            Text(c.Id);
            Text(c.Unit);
            Doubles(c.Samples);
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
        => new(Key, SampleRate, Times, Channels.Select(c => new Testumgebung.Contracts.OutputChannelData(c.Id, c.Unit, c.Samples)).ToList());

    public override string ToString() => $"{Name} v{Version} ({Channels.Count} Kanäle, {Count} Samples)";
}

/// <summary>Kurvenbibliothek eines Projekts: alle gespeicherten Versionen (unveränderlich).</summary>
public sealed class CurveLibrary
{
    private readonly List<CurveDefinition> _versions = [];

    public IReadOnlyList<CurveDefinition> Versions => _versions;

    public IEnumerable<string> Ids => _versions.Select(v => v.Id).Distinct();

    public CurveDefinition? Find(string id, string? version = null)
        => version is null or "" or "neueste"
            ? _versions.Where(v => v.Id == id).OrderBy(v => v.Version, VersionComparer.Instance).LastOrDefault()
            : _versions.FirstOrDefault(v => v.Id == id && v.Version == version);

    public IReadOnlyList<CurveDefinition> VersionsOf(string id) => _versions.Where(v => v.Id == id).OrderBy(v => v.Version, VersionComparer.Instance).ToList();

    /// <summary>Version speichern. Eine bestehende Version wird nie überschrieben.</summary>
    public void Add(CurveDefinition curve)
    {
        if (_versions.Any(v => v.Id == curve.Id && v.Version == curve.Version))
            throw new InvalidOperationException($"Kurve {curve.Id} v{curve.Version} existiert bereits – Versionen sind unveränderlich.");
        if (string.IsNullOrWhiteSpace(curve.Id) || string.IsNullOrWhiteSpace(curve.Version)) throw new InvalidOperationException("Kurven-Id und Version erforderlich.");
        _versions.Add(curve);
    }

    public string NextVersion(string id)
    {
        var last = Find(id);
        if (last is null) return "0.1";
        var parts = last.Version.Split('.');
        return int.TryParse(parts[^1], out var n) ? string.Join('.', parts[..^1].Append((n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) : last.Version + ".1";
    }
}

/// <summary>Vergleicht Versionen numerisch je Abschnitt (0.10 &gt; 0.9).</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = (x ?? "").Split('.');
        var b = (y ?? "").Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var pa = i < a.Length ? a[i] : "0";
            var pb = i < b.Length ? b[i] : "0";
            var c = int.TryParse(pa, out var na) && int.TryParse(pb, out var nb) ? na.CompareTo(nb) : string.CompareOrdinal(pa, pb);
            if (c != 0) return c;
        }
        return 0;
    }
}
