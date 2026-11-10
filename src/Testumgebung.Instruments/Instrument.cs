/**
 * @file
 * @brief Definiert Gerätekanäle und gemeinsame Instrumentoperationen für konkrete Treiber.
 */

using System.Globalization;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments;

/// <summary>Zugriff auf einen Gerätekanal.</summary>
[Flags]
public enum ChannelAccess
{
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
    Waveform = 4,
    WaveformOutput = 8,
}

/// <summary>Kanal eines Treibers (für Zuordnung, Prüfung und Anzeige).</summary>
public sealed record InstrumentChannel(string Name, string Unit, double Min, double Max, ChannelAccess Access, string Description);

/// <summary>Beschreibung einer Gerätefamilie für die Konfiguration („Gerät hinzufügen“).</summary>
public sealed record InstrumentFamily(string Adapter, string Title, IReadOnlyList<string> Models, string ResourceExample, string Notes);

/// <summary>Ergebnis von „Gerät testen“.</summary>
public sealed record InstrumentProbe(bool Ok, string Identity, IReadOnlyList<string> Details, IReadOnlyList<string> Log);

/// <summary>Gemeinsame Schnittstelle aller realen Gerätetreiber (zusätzlich zu den Hardwareverträgen).</summary>
public interface IInstrumentDriver : IHardwareAdapter, ISafeStateAdapter, IDisposable
{
    string Adapter { get; }
    string Model { get; }
    string Resource { get; }

    /// <summary>Kanäle des Modells (vor dem Verbinden aus der Modelltabelle).</summary>
    IReadOnlyList<InstrumentChannel> Channels { get; }

    /// <summary>Verbinden, identifizieren, Fehlerwarteschlange lesen, Fähigkeiten melden – ohne Ausgänge zu verändern.</summary>
    Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken);

    IReadOnlyList<string> Log { get; }
}

/// <summary>Basis der SCPI-Treiber: Kanalkatalog, Zahlformat, sicherer Zustand.</summary>
public abstract class ScpiInstrument : IInstrumentDriver
{
    protected ScpiInstrument(string adapter, string model, ScpiSession session, string? name = null)
    {
        Adapter = adapter;
        Model = model;
        Session = session;
        Name = name ?? $"{model} ({session.Resource})";
    }

    public string Adapter { get; }
    public string Model { get; }
    public string Name { get; }
    public string Resource => Session.Resource;
    public ScpiSession Session { get; }
    public IReadOnlyList<string> Log => Session.Log;

    public abstract IReadOnlyList<InstrumentChannel> Channels { get; }

    protected InstrumentChannel Channel(string channel)
        => Channels.FirstOrDefault(channel2 => string.Equals(channel2.Name, channel, StringComparison.OrdinalIgnoreCase))
           ?? throw new InstrumentException($"{Model}: Kanal „{channel}“ gibt es nicht (vorhanden: {string.Join(", ", Channels.Select(channel2 => channel2.Name))}).");

    public ChannelCapabilities GetCapabilities(string channel)
    {
        var channel2 = Channel(channel);
        return new ChannelCapabilities(channel2.Unit, channel2.Min, channel2.Max, channel2.Access.HasFlag(ChannelAccess.Waveform), channel2.Access.HasFlag(ChannelAccess.Waveform));
    }

    public async Task<double> ReadAsync(string channel, CancellationToken cancellationToken)
    {
        var channel2 = Channel(channel);
        if (!channel2.Access.HasFlag(ChannelAccess.Read)) throw new InstrumentException($"{Model}: Kanal {channel2.Name} ist nicht lesbar.");
        return await ReadChannelAsync(channel2, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string channel, double value, CancellationToken cancellationToken)
    {
        var channel2 = Channel(channel);
        if (!channel2.Access.HasFlag(ChannelAccess.Write)) throw new InstrumentException($"{Model}: Kanal {channel2.Name} ist nur lesbar.");
        if (value < channel2.Min || value > channel2.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{F(value)} {channel2.Unit} außerhalb {F(channel2.Min)} … {F(channel2.Max)} {channel2.Unit} von {Model} {channel2.Name} – keine automatische Begrenzung.");
        await WriteChannelAsync(channel2, value, cancellationToken).ConfigureAwait(false);
    }

    protected abstract Task<double> ReadChannelAsync(InstrumentChannel channel, CancellationToken ct);

    protected abstract Task WriteChannelAsync(InstrumentChannel channel, double value, CancellationToken ct);

    /// <summary>Alle Ausgänge aus; darf auch nach einem Abbruch aufgerufen werden (eigene Zeitbegrenzung).</summary>
    public abstract Task SafeStateAsync(CancellationToken cancellationToken);

    public virtual async Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();
        try
        {
            var id = await Session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
            if (!SameModel(Model, id.Model))
                details.Add($"Achtung: Gerät meldet {id.Model}, konfiguriert ist {Model}.");
            var errors = await Session.ReadErrorsAsync(cancellationToken).ConfigureAwait(false);
            details.AddRange(errors.Select(error => "Fehlerwarteschlange: " + error));
            details.AddRange(await DescribeAsync(cancellationToken).ConfigureAwait(false));
            details.Add("Kanäle: " + string.Join(", ", Channels.Select(channel => $"{channel.Name} [{channel.Unit}]")));
            return new InstrumentProbe(true, id.ToString(), details, Log);
        }
        catch (Exception ex) when (ex is InstrumentException or TimeoutException or IOException)
        {
            details.Add(ex.Message);
            return new InstrumentProbe(false, "nicht erreichbar", details, Log);
        }
    }

    /// <summary>Modellspezifische Zusatzangaben beim Test (Bereiche, Optionen) – nur lesend.</summary>
    protected virtual Task<IReadOnlyList<string>> DescribeAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>„DSOX3034T“ ≙ „DSO-X 3034T“, „34461A“ ≙ „34461A“; Agilent/Keysight-Varianten mit gleicher Nummer gelten als gleich.</summary>
    public static bool SameModel(string configured, string reported)
    {
        static string N(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var (code, resource) = (N(configured), N(reported));
        if (code.Length == 0 || resource.Length == 0) return false;
        if (resource.Contains(code, StringComparison.Ordinal) || code.Contains(resource, StringComparison.Ordinal)) return true;
        var digits = new string(code.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && resource.Contains(digits, StringComparison.Ordinal);
    }

    protected static string F(double v) => v.ToString("G10", CultureInfo.InvariantCulture);

    /// <summary>Sicherer Zustand mit eigener Zeitbegrenzung – auch wenn der Prüfablauf bereits abgebrochen ist.</summary>
    protected static CancellationToken SafeToken(CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        return cts.Token;
    }

    public void Dispose()
    {
        Session.Dispose();
        GC.SuppressFinalize(this);
    }
}
