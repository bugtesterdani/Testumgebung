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
        var duration = s.Kind == GeneratorKind.Step ? ParseSteps(s.Steps).Sum(x => x.Duration) : s.Duration;
        if (duration <= 0) throw new CurveGeneratorException("Dauer muss > 0 sein.");
        var count = (int)Math.Round(duration * s.SampleRate);
        if (count < 1 || count > MaxSamples) throw new CurveGeneratorException($"Samplezahl {count} außerhalb 1 … {MaxSamples}.");

        var channels = new List<CurveChannel>();
        for (var c = 0; c < s.ChannelIds.Count; c++)
        {
            Func<double, double> f = s.Kind switch
            {
                GeneratorKind.Sine => Sine(s, c),
                GeneratorKind.Ramp => t => s.RampStart + (s.RampEnd - s.RampStart) * Math.Clamp(t / duration, 0, 1),
                GeneratorKind.Step => StepFunction(ParseSteps(s.Steps)),
                _ => Formula(s, c),
            };
            var data = new double[count];
            for (var i = 0; i < count; i++)
            {
                var v = f(i / s.SampleRate);
                if (!double.IsFinite(v)) throw new CurveGeneratorException($"Kanal {s.ChannelIds[c]}: Wert bei t = {i / s.SampleRate:0.######} s ist nicht endlich.");
                data[i] = v;
            }
            channels.Add(new CurveChannel(s.ChannelIds[c], s.Unit, data));
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
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var common = $"{string.Join("/", s.ChannelIds)} [{s.Unit}], {N(s.SampleRate)} Hz";
        return s.Kind switch
        {
            GeneratorKind.Sine => $"Generator Sinus: {N(s.Amplitude)} {s.Unit} Scheitel, {N(s.Frequency)} Hz, Offset {N(s.Offset)}, Phasen {string.Join("/", s.Phases.Select(N))}°, {N(s.Duration)} s, {common}",
            GeneratorKind.Ramp => $"Generator Rampe: {N(s.RampStart)} → {N(s.RampEnd)} {s.Unit} in {N(s.Duration)} s, {common}",
            GeneratorKind.Step => $"Generator Stufen: {s.Steps}, {common}",
            _ => $"Generator Formel: {string.Join(" | ", s.Formulas)}, {N(s.Duration)} s, {common}",
        };
    }

    private static Func<double, double> Sine(GeneratorSettings s, int c)
    {
        var phase = (c < s.Phases.Count ? s.Phases[c] : 0) * Math.PI / 180;
        return t => s.Offset + s.Amplitude * Math.Sin(2 * Math.PI * s.Frequency * t - phase);
    }

    private static Func<double, double> Formula(GeneratorSettings s, int c)
    {
        if (s.Formulas.Count == 0) throw new CurveGeneratorException("Formel fehlt.");
        var text = c < s.Formulas.Count ? s.Formulas[c] : s.Formulas[0];
        try { return CurveFormula.Compile(text); }
        catch (CurveFormulaException ex) { throw new CurveGeneratorException($"Kanal {s.ChannelIds[c]}: {ex.Message}"); }
    }

    public static IReadOnlyList<(double Value, double Duration)> ParseSteps(string text)
    {
        var list = new List<(double, double)>();
        foreach (var part in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                || !double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0)
                throw new CurveGeneratorException($"Stufe '{part}' ungültig – Format „Wert:Dauer“, z. B. 230:0.03.");
            list.Add((v, d));
        }
        if (list.Count == 0) throw new CurveGeneratorException("Mindestens eine Stufe angeben.");
        return list;
    }

    private static Func<double, double> StepFunction(IReadOnlyList<(double Value, double Duration)> steps)
    {
        var ends = new double[steps.Count];
        double acc = 0;
        for (var i = 0; i < steps.Count; i++) ends[i] = acc += steps[i].Duration;
        return t =>
        {
            for (var i = 0; i < ends.Length; i++)
                if (t < ends[i] - 1e-12) return steps[i].Value;
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
    private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Zuschnitt auf [von, bis) in Sekunden; die Zeitbasis beginnt danach wieder bei 0.</summary>
    public static CurveDefinition Trim(CurveDefinition c, double from, double to)
    {
        if (to <= from) throw new ArgumentException("Zuschnitt: Ende muss nach dem Anfang liegen.");
        var idx = Enumerable.Range(0, c.Count).Where(i => c.TimeOf(i) >= from - 1e-12 && c.TimeOf(i) < to - 1e-12).ToArray();
        if (idx.Length == 0) throw new ArgumentException("Zuschnitt enthält keine Samples.");
        var channels = c.Channels.Select(ch => ch with { Samples = idx.Select(i => ch.Samples[i]).ToArray() }).ToList();
        double[]? times = c.Times is null ? null : idx.Select(i => c.Times[i] - c.Times[idx[0]]).ToArray();
        return c.Derive($"Zuschnitt {N(from * 1000)}–{N(to * 1000)} ms", channels, times: times, keepTimes: false);
    }

    /// <summary>Werte mit Faktor multiplizieren (alle Kanäle oder einer).</summary>
    public static CurveDefinition Scale(CurveDefinition c, double factor, string? channel = null)
    {
        if (!double.IsFinite(factor)) throw new ArgumentException("Skalierung: Faktor ungültig.");
        if (channel is not null && c.Channels.All(ch => ch.Id != channel)) throw new ArgumentException($"Kanal {channel} unbekannt.");
        var channels = c.Channels.Select(ch => channel is null || ch.Id == channel ? ch with { Samples = ch.Samples.Select(v => v * factor).ToArray() } : ch).ToList();
        return c.Derive($"Skalierung ×{N(factor)}{(channel is null ? "" : $" ({channel})")}", channels);
    }

    /// <summary>Zeitverschiebung: dt &gt; 0 verzögert (Anfangswert wird vorangestellt), dt &lt; 0 entfernt den Anfang.</summary>
    public static CurveDefinition Shift(CurveDefinition c, double dt)
    {
        if (c.Times is { } times)
        {
            var shifted = times.Select(t => t + dt).ToArray();
            var keep = Enumerable.Range(0, shifted.Length).Where(i => shifted[i] >= 0).ToArray();
            if (keep.Length == 0) throw new ArgumentException("Zeitverschiebung entfernt alle Samples.");
            return c.Derive($"Zeitverschiebung {N(dt * 1000)} ms",
                c.Channels.Select(ch => ch with { Samples = keep.Select(i => ch.Samples[i]).ToArray() }).ToList(),
                times: keep.Select(i => shifted[i]).ToArray());
        }
        var n = (int)Math.Round(dt * c.SampleRate);
        if (n == 0) return c;
        var channels = c.Channels.Select(ch =>
        {
            if (n > 0) return ch with { Samples = Enumerable.Repeat(ch.Samples.Length == 0 ? 0 : ch.Samples[0], n).Concat(ch.Samples).ToArray() };
            if (-n >= ch.Samples.Length) throw new ArgumentException("Zeitverschiebung entfernt alle Samples.");
            return ch with { Samples = ch.Samples[-n..] };
        }).ToList();
        return c.Derive($"Zeitverschiebung {N(n / c.SampleRate * 1000)} ms ({n} Samples)", channels);
    }

    /// <summary>Ausdrückliche Änderung der Abtastrate (lineare Interpolation); explizite Zeitpunkte werden zu fester Zeitbasis.</summary>
    public static CurveDefinition Resample(CurveDefinition c, double newRate)
    {
        if (newRate <= 0) throw new ArgumentException("Abtastrate muss > 0 sein.");
        var start = c.Times?[0] ?? 0;
        var end = c.Times is { } t ? t[^1] : (c.Count - 1) / c.SampleRate;
        var count = (int)Math.Floor((end - start) * newRate + 1e-9) + 1;
        if (count > CurveGenerator.MaxSamples) throw new ArgumentException("Zu viele Samples.");
        var channels = c.Channels.Select((ch, ci) => ch with
        {
            Samples = Enumerable.Range(0, count).Select(i => c.ValueAt(ci, start + i / newRate)).ToArray(),
        }).ToList();
        return c.Derive($"Abtastrate ausdrücklich geändert: {N(c.SampleRate)} → {N(newRate)} Hz (linear interpoliert)", channels, newRate, keepTimes: false);
    }

    /// <summary>Kanäle umbenennen bzw. auswählen (Kanalzuordnung): Quelle → Ziel.</summary>
    public static CurveDefinition MapChannels(CurveDefinition c, IReadOnlyList<(string Source, string Target)> mapping)
    {
        var channels = mapping.Select(m => (c.Channels.FirstOrDefault(ch => ch.Id == m.Source) ?? throw new ArgumentException($"Kanal {m.Source} unbekannt.")) with { Id = m.Target }).ToList();
        if (channels.Select(ch => ch.Id).Distinct().Count() != channels.Count) throw new ArgumentException("Zielkanäle müssen eindeutig sein.");
        return c.Derive("Kanalzuordnung " + string.Join(", ", mapping.Select(m => $"{m.Source}→{m.Target}")), channels);
    }

    /// <summary>Formale Prüfung: gleiche Längen, endliche Werte, steigende Zeitpunkte.</summary>
    public static IReadOnlyList<string> Validate(CurveDefinition c)
    {
        var list = new List<string>();
        if (c.Channels.Count == 0) list.Add("Keine Kanäle.");
        if (c.Channels.Select(ch => ch.Samples.Length).Distinct().Count() > 1) list.Add("Kanäle haben unterschiedliche Längen.");
        if (c.Channels.Select(ch => ch.Id).Distinct().Count() != c.Channels.Count) list.Add("Kanal-IDs nicht eindeutig.");
        foreach (var ch in c.Channels.Where(ch => ch.Samples.Any(v => !double.IsFinite(v)))) list.Add($"Kanal {ch.Id}: nicht endliche Werte.");
        if (c.Times is { } t)
        {
            if (t.Length != c.Count) list.Add("Anzahl Zeitpunkte passt nicht zu den Samples.");
            for (var i = 1; i < t.Length; i++)
                if (t[i] <= t[i - 1]) { list.Add($"Zeitpunkte nicht streng steigend (Zeile {i + 1})."); break; }
        }
        else if (c.SampleRate <= 0) list.Add("Abtastrate fehlt.");
        return list;
    }
}
