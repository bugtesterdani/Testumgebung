/**
 * @file
 * @brief Erzeugt reproduzierbare Samples und führt bewusst gewählte Kurvenoperationen aus.
 */

using System.Globalization;

namespace Testumgebung.Domain.Curves;

public enum GeneratorKind
{
    Sine,
    Ramp,
    Step,
    Formula,
}

/// <summary>Einstellungen des Kurvengenerators. Gleiche Einstellungen ergeben bitgleiche Kurven (reproduzierbar).</summary>
public sealed record GeneratorSettings
{
    public GeneratorKind Kind { get; init; } = GeneratorKind.Sine;
    public IReadOnlyList<string> ChannelIds { get; init; } = ["L1", "L2", "L3"];
    public string Unit { get; init; } = "V";
    public double SampleRate { get; init; } = 20000;
    public double Duration { get; init; } = 0.05;

    /// <summary>Sinus: Scheitelwert.</summary>
    public double Amplitude { get; init; } = 325;
    public double Frequency { get; init; } = 50;
    public double Offset { get; init; }

    /// <summary>Sinus: Phasenlage je Kanal in Grad (fehlende = 0).</summary>
    public IReadOnlyList<double> Phases { get; init; } = [0, 120, 240];

    /// <summary>Rampe: Start- und Endwert über die Dauer.</summary>
    public double RampStart { get; init; }
    public double RampEnd { get; init; } = 230;

    /// <summary>Stufen: „Wert:Dauer[s]“ durch Semikolon getrennt, z. B. „0:0.01;230:0.03;0:0.01“.</summary>
    public string Steps { get; init; } = "0:0.01;230:0.03;0:0.01";

    /// <summary>Formel je Kanal (fehlende = erste Formel).</summary>
    public IReadOnlyList<string> Formulas { get; init; } = ["325*sin(2*pi*50*t)"];
}

public sealed class CurveGeneratorException(string message) : Exception(message);

public static class CurveGenerator
{
    public const int MaxSamples = 10_000_000;

    public static CurveDefinition Generate(GeneratorSettings s, string id, string name, string version, string createdBy, DateTimeOffset createdAt)
    {
        if (s.ChannelIds.Count == 0) throw new CurveGeneratorException("Mindestens ein Kanal erforderlich.");
        if (s.ChannelIds.Distinct().Count() != s.ChannelIds.Count) throw new CurveGeneratorException("Kanalnamen müssen eindeutig sein.");
        if (s.SampleRate <= 0) throw new CurveGeneratorException("Abtastrate muss > 0 sein.");
        var duration = s.Kind == GeneratorKind.Step ? ParseSteps(s.Steps).Sum(entry => entry.Duration) : s.Duration;
        if (duration <= 0) throw new CurveGeneratorException("Dauer muss > 0 sein.");
        var count = (int)Math.Round(duration * s.SampleRate);
        if (count < 1 || count > MaxSamples) throw new CurveGeneratorException($"Samplezahl {count} außerhalb 1 … {MaxSamples}.");

        var channels = new List<CurveChannel>();
        for (var columnIndex = 0; columnIndex < s.ChannelIds.Count; columnIndex++)
        {
            Func<double, double> callback = s.Kind switch
            {
                GeneratorKind.Sine => Sine(s, columnIndex),
                GeneratorKind.Ramp => time => s.RampStart + (s.RampEnd - s.RampStart) * Math.Clamp(time / duration, 0, 1),
                GeneratorKind.Step => StepFunction(ParseSteps(s.Steps)),
                _ => Formula(s, columnIndex),
            };
            var data = new double[count];
            for (var index = 0; index < count; index++)
            {
                var value = callback(index / s.SampleRate);
                if (!double.IsFinite(value)) throw new CurveGeneratorException($"Kanal {s.ChannelIds[columnIndex]}: Wert bei t = {index / s.SampleRate:0.######} s ist nicht endlich.");
                data[index] = value;
            }
            channels.Add(new CurveChannel(s.ChannelIds[columnIndex], s.Unit, data));
        }
        return new CurveDefinition
        {
            Id = id,
            Name = name,
            Version = version,
            SampleRate = s.SampleRate,
            Channels = channels,
            Origin = CurveOrigin.Generator,
            SourceText = Describe(s),
            History = [],
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }

    public static string Describe(GeneratorSettings s)
    {
        string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        var common = $"{string.Join("/", s.ChannelIds)} [{s.Unit}], {N(s.SampleRate)} Hz";
        return s.Kind switch
        {
            GeneratorKind.Sine => $"Generator Sinus: {N(s.Amplitude)} {s.Unit} Scheitel, {N(s.Frequency)} Hz, Offset {N(s.Offset)}, Phasen {string.Join("/", s.Phases.Select(N))}°, {N(s.Duration)} s, {common}",
            GeneratorKind.Ramp => $"Generator Rampe: {N(s.RampStart)} → {N(s.RampEnd)} {s.Unit} in {N(s.Duration)} s, {common}",
            GeneratorKind.Step => $"Generator Stufen: {s.Steps}, {common}",
            _ => $"Generator Formel: {string.Join(" | ", s.Formulas)}, {N(s.Duration)} s, {common}",
        };
    }

    private static Func<double, double> Sine(GeneratorSettings generatorSettings, int channelIndex)
    {
        var phase = (channelIndex < generatorSettings.Phases.Count ? generatorSettings.Phases[channelIndex] : 0) * Math.PI / 180;
        return time => generatorSettings.Offset + generatorSettings.Amplitude * Math.Sin(2 * Math.PI * generatorSettings.Frequency * time - phase);
    }

    private static Func<double, double> Formula(GeneratorSettings generatorSettings, int channelIndex)
    {
        if (generatorSettings.Formulas.Count == 0) throw new CurveGeneratorException("Formel fehlt.");
        var text = channelIndex < generatorSettings.Formulas.Count ? generatorSettings.Formulas[channelIndex] : generatorSettings.Formulas[0];
        try { return CurveFormula.Compile(text); }
        catch (CurveFormulaException ex) { throw new CurveGeneratorException($"Kanal {generatorSettings.ChannelIds[channelIndex]}: {ex.Message}"); }
    }

    public static IReadOnlyList<(double Value, double Duration)> ParseSteps(string text)
    {
        var list = new List<(double, double)>();
        foreach (var part in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number <= 0)
                throw new CurveGeneratorException($"Stufe '{part}' ungültig – Format „Wert:Dauer“, z. B. 230:0.03.");
            list.Add((value, number));
        }
        if (list.Count == 0) throw new CurveGeneratorException("Mindestens eine Stufe angeben.");
        return list;
    }

    private static Func<double, double> StepFunction(IReadOnlyList<(double Value, double Duration)> steps)
    {
        var ends = new double[steps.Count];
        double acc = 0;
        for (var index = 0; index < steps.Count; index++) ends[index] = acc += steps[index].Duration;
        return time =>
        {
            for (var index2 = 0; index2 < ends.Length; index2++)
                if (time < ends[index2] - 1e-12) return steps[index2].Value;
            return steps[^1].Value;
        };
    }
}

/// <summary>
/// Bearbeitungsschritte (Abschnitt 8). Jede Operation ist ausdrücklich, liefert eine neue Fassung und wird in der
/// Historie vermerkt. Es gibt keine automatische Skalierung oder Änderung der Abtastrate.
/// </summary>
public static class CurveOps
{
    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Verschiebt einen Messwert und blendet die Änderung zeitlinear zu den unveränderten Randpunkten aus.</summary>
    public static CurveDefinition MovePoint(CurveDefinition curve, int channelIndex, int pointIndex,
        int leftBoundary, int rightBoundary, double delta)
    {
        if (!double.IsFinite(delta) || leftBoundary < 0 || leftBoundary > pointIndex
            || rightBoundary < pointIndex || rightBoundary >= curve.Count)
            throw new ArgumentException("Ungültige Punktverschiebung.");

        var channels = curve.Channels.ToList();
        var channel = channels[channelIndex];
        var samples = (double[])channel.Samples.Clone();
        var pointTime = curve.TimeOf(pointIndex);
        var leftTime = curve.TimeOf(leftBoundary);
        var rightTime = curve.TimeOf(rightBoundary);
        for (var sampleIndex = leftBoundary; sampleIndex <= rightBoundary; sampleIndex++)
        {
            var weight = PointMoveWeight(curve.TimeOf(sampleIndex), pointTime, leftTime, rightTime);
            if (weight == 0) continue;
            samples[sampleIndex] += delta * weight;
            if (!double.IsFinite(samples[sampleIndex])) throw new ArgumentException("Punktwert außerhalb des gültigen Bereichs.");
        }
        channels[channelIndex] = channel with { Samples = samples };
        return curve.Derive($"Punkt {channel.Id} bei {N(pointTime * 1000)} ms → {N(samples[pointIndex])} {channel.Unit}", channels);
    }

    /// <summary>Ersetzt die Stützpunkte eines Kanals. Andere Kanäle behalten ihren Verlauf auf dem gemeinsamen Raster.</summary>
    public static CurveDefinition ReplacePoints(CurveDefinition curve, int channelIndex, (double Time, double Value)[] points)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channelIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channelIndex, curve.Channels.Count);
        if (points.Length is 0 or > CurveGenerator.MaxSamples)
            throw new ArgumentException("Die Linie muss mindestens einen Punkt enthalten und darf die maximale Punktzahl nicht überschreiten.");
        var ordered = points;
        for (var i = 1; i < points.Length; i++)
        {
            if (points[i].Time >= points[i - 1].Time) continue;
            ordered = points.OrderBy(point => point.Time).ToArray();
            break;
        }
        var sameGrid = ordered.Length == curve.Count;
        for (var i = 0; i < ordered.Length; i++)
        {
            var (time, value) = ordered[i];
            if (!double.IsFinite(time) || !double.IsFinite(value))
                throw new ArgumentException("Zeit und Wert müssen endliche Zahlen sein.");
            if (i > 0 && time <= ordered[i - 1].Time)
                throw new ArgumentException($"Die Zeit {N(time)} s ist mehrfach vorhanden.");
            sameGrid = sameGrid && time == curve.TimeOf(i);
        }

        var channel = curve.Channels[channelIndex];
        var operation = $"Stützpunkte von {channel.Id} bearbeitet ({ordered.Length} Punkte)";
        if (sameGrid)
        {
            var channels = curve.Channels.ToArray();
            channels[channelIndex] = channel with { Samples = ordered.Select(point => point.Value).ToArray() };
            return curve.Derive(operation, channels);
        }

        var originalTimes = curve.Times ?? Enumerable.Range(0, curve.Count).Select(curve.TimeOf).ToArray();
        var pointTimes = ordered.Select(point => point.Time).ToArray();
        var times = curve.Channels.Count == 1 ? pointTimes : originalTimes.Union(pointTimes).Order().ToArray();
        if (times.Length > CurveGenerator.MaxSamples || !double.IsFinite(times[^1] - times[0]))
            throw new ArgumentException("Das gemeinsame Zeitraster liegt außerhalb des gültigen Bereichs.");

        var selected = curve.Derive(operation,
            [channel with { Samples = ordered.Select(point => point.Value).ToArray() }], times: pointTimes);
        var samples = curve.Channels.Select((line, index) => line with
        {
            Samples = times.Select(time =>
            {
                if (index == channelIndex) return selected.ValueAt(0, time);
                var originalIndex = Array.BinarySearch(originalTimes, time);
                return originalIndex >= 0 ? line.Samples[originalIndex] : curve.ValueAt(index, time);
            }).ToArray()
        }).ToArray();
        var result = curve.Derive(operation, samples, times: times);
        if (!double.IsFinite(result.Duration) || samples.Any(line => line.Samples.Any(value => !double.IsFinite(value))))
            throw new ArgumentException("Das Zeitraster oder interpolierte Werte liegen außerhalb des gültigen Bereichs.");
        return result;
    }

    /// <summary>Gewichtung für Vorschau und gespeicherte Werte, unabhängig von der Achsenskalierung.</summary>
    public static double PointMoveWeight(double position, double point, double left, double right)
    {
        if (position == point) return 1;
        if (position <= left || position >= right) return 0;
        return position < point ? (position - left) / (point - left) : (right - position) / (right - point);
    }

    /// <summary>Zuschnitt auf [von, bis) in Sekunden; die Zeitbasis beginnt danach wieder bei 0.</summary>
    public static CurveDefinition Trim(CurveDefinition c, double from, double to)
    {
        if (to <= from) throw new ArgumentException("Zuschnitt: Ende muss nach dem Anfang liegen.");
        var idx = Enumerable.Range(0, c.Count).Where(index => c.TimeOf(index) >= from - 1e-12 && c.TimeOf(index) < to - 1e-12).ToArray();
        if (idx.Length == 0) throw new ArgumentException("Zuschnitt enthält keine Samples.");
        var channels = c.Channels.Select(ch => ch with { Samples = idx.Select(idx2 => ch.Samples[idx2]).ToArray() }).ToList();
        double[]? times = c.Times is null ? null : idx.Select(idx2 => c.Times[idx2] - c.Times[idx[0]]).ToArray();
        return c.Derive($"Zuschnitt {N(from * 1000)}–{N(to * 1000)} ms", channels, times: times, keepTimes: false);
    }

    /// <summary>Werte mit Faktor multiplizieren (alle Kanäle oder einer).</summary>
    public static CurveDefinition Scale(CurveDefinition c, double factor, string? channel = null)
    {
        if (!double.IsFinite(factor)) throw new ArgumentException("Skalierung: Faktor ungültig.");
        if (channel is not null && c.Channels.All(ch => ch.Id != channel)) throw new ArgumentException($"Kanal {channel} unbekannt.");
        var channels = c.Channels.Select(ch => channel is null || ch.Id == channel ? ch with { Samples = ch.Samples.Select(sample => sample * factor).ToArray() } : ch).ToList();
        return c.Derive($"Skalierung ×{N(factor)}{(channel is null ? "" : $" ({channel})")}", channels);
    }

    /// <summary>Zeitverschiebung: dt &gt; 0 verzögert (Anfangswert wird vorangestellt), dt &lt; 0 entfernt den Anfang.</summary>
    public static CurveDefinition Shift(CurveDefinition c, double dt)
    {
        if (c.Times is { } times)
        {
            var shifted = times.Select(time => time + dt).ToArray();
            var keep = Enumerable.Range(0, shifted.Length).Where(index => shifted[index] >= 0).ToArray();
            if (keep.Length == 0) throw new ArgumentException("Zeitverschiebung entfernt alle Samples.");
            return c.Derive($"Zeitverschiebung {N(dt * 1000)} ms",
                c.Channels.Select(ch => ch with { Samples = keep.Select(keep2 => ch.Samples[keep2]).ToArray() }).ToList(),
                times: keep.Select(keep2 => shifted[keep2]).ToArray());
        }
        var count = (int)Math.Round(dt * c.SampleRate);
        if (count == 0) return c;
        var channels = c.Channels.Select(ch =>
        {
            if (count > 0) return ch with { Samples = Enumerable.Repeat(ch.Samples.Length == 0 ? 0 : ch.Samples[0], count).Concat(ch.Samples).ToArray() };
            if (-count >= ch.Samples.Length) throw new ArgumentException("Zeitverschiebung entfernt alle Samples.");
            return ch with { Samples = ch.Samples[-count..] };
        }).ToList();
        return c.Derive($"Zeitverschiebung {N(count / c.SampleRate * 1000)} ms ({count} Samples)", channels);
    }

    /// <summary>Ausdrückliche Änderung der Abtastrate (lineare Interpolation); explizite Zeitpunkte werden zu fester Zeitbasis.</summary>
    public static CurveDefinition Resample(CurveDefinition c, double newRate)
    {
        if (newRate <= 0) throw new ArgumentException("Abtastrate muss > 0 sein.");
        var start = c.Times?[0] ?? 0;
        var end = c.Times is { } samples ? samples[^1] : (c.Count - 1) / c.SampleRate;
        var count = (int)Math.Floor((end - start) * newRate + 1e-9) + 1;
        if (count > CurveGenerator.MaxSamples) throw new ArgumentException("Zu viele Samples.");
        var channels = c.Channels.Select((ch, ci) => ch with
        {
            Samples = Enumerable.Range(0, count).Select(index => c.ValueAt(ci, start + index / newRate)).ToArray(),
        }).ToList();
        return c.Derive($"Abtastrate ausdrücklich geändert: {N(c.SampleRate)} → {N(newRate)} Hz (linear interpoliert)", channels, newRate, keepTimes: false);
    }

    /// <summary>Kanäle umbenennen bzw. auswählen (Kanalzuordnung): Quelle → Ziel.</summary>
    public static CurveDefinition MapChannels(CurveDefinition c, IReadOnlyList<(string Source, string Target)> mapping)
    {
        var channels = mapping.Select(entry => (c.Channels.FirstOrDefault(ch => ch.Id == entry.Source) ?? throw new ArgumentException($"Kanal {entry.Source} unbekannt.")) with { Id = entry.Target }).ToList();
        if (channels.Select(ch => ch.Id).Distinct().Count() != channels.Count) throw new ArgumentException("Zielkanäle müssen eindeutig sein.");
        return c.Derive("Kanalzuordnung " + string.Join(", ", mapping.Select(entry => $"{entry.Source}→{entry.Target}")), channels);
    }

    /// <summary>Formale Prüfung: gleiche Längen, endliche Werte, steigende Zeitpunkte.</summary>
    public static IReadOnlyList<string> Validate(CurveDefinition c)
    {
        var list = new List<string>();
        if (c.Channels.Count == 0) list.Add("Keine Kanäle.");
        if (c.Channels.Select(ch => ch.Samples.Length).Distinct().Count() > 1) list.Add("Kanäle haben unterschiedliche Längen.");
        if (c.Channels.Select(ch => ch.Id).Distinct().Count() != c.Channels.Count) list.Add("Kanal-IDs nicht eindeutig.");
        foreach (var ch in c.Channels.Where(ch => ch.Samples.Any(sample => !double.IsFinite(sample)))) list.Add($"Kanal {ch.Id}: nicht endliche Werte.");
        if (c.Times is { } samples)
        {
            if (samples.Length != c.Count) list.Add("Anzahl Zeitpunkte passt nicht zu den Samples.");
            for (var index = 1; index < samples.Length; index++)
                if (samples[index] <= samples[index - 1]) { list.Add($"Zeitpunkte nicht streng steigend (Zeile {index + 1})."); break; }
        }
        else if (c.SampleRate <= 0) list.Add("Abtastrate fehlt.");
        return list;
    }
}
