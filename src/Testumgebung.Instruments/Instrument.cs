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
        => Channels.FirstOrDefault(c => string.Equals(c.Name, channel, StringComparison.OrdinalIgnoreCase))
           ?? throw new InstrumentException($"{Model}: Kanal „{channel}“ gibt es nicht (vorhanden: {string.Join(", ", Channels.Select(c => c.Name))}).");

    public ChannelCapabilities GetCapabilities(string channel)
    {
        var c = Channel(channel);
        return new ChannelCapabilities(c.Unit, c.Min, c.Max, c.Access.HasFlag(ChannelAccess.Waveform), c.Access.HasFlag(ChannelAccess.Waveform));
    }

    public async Task<double> ReadAsync(string channel, CancellationToken cancellationToken)
    {
        var c = Channel(channel);
        if (!c.Access.HasFlag(ChannelAccess.Read)) throw new InstrumentException($"{Model}: Kanal {c.Name} ist nicht lesbar.");
        return await ReadChannelAsync(c, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string channel, double value, CancellationToken cancellationToken)
    {
        var c = Channel(channel);
        if (!c.Access.HasFlag(ChannelAccess.Write)) throw new InstrumentException($"{Model}: Kanal {c.Name} ist nur lesbar.");
        if (value < c.Min || value > c.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{F(value)} {c.Unit} außerhalb {F(c.Min)} … {F(c.Max)} {c.Unit} von {Model} {c.Name} – keine automatische Begrenzung.");
        await WriteChannelAsync(c, value, cancellationToken).ConfigureAwait(false);
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
            details.AddRange(errors.Select(e => "Fehlerwarteschlange: " + e));
            details.AddRange(await DescribeAsync(cancellationToken).ConfigureAwait(false));
            details.Add("Kanäle: " + string.Join(", ", Channels.Select(c => $"{c.Name} [{c.Unit}]")));
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
        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var (c, r) = (N(configured), N(reported));
        if (c.Length == 0 || r.Length == 0) return false;
        if (r.Contains(c, StringComparison.Ordinal) || c.Contains(r, StringComparison.Ordinal)) return true;
        var digits = new string(c.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 && r.Contains(digits, StringComparison.Ordinal);
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
