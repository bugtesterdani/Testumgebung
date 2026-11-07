using Testumgebung.Contracts;

namespace Testumgebung.Instruments;

/// <summary>A session owns exactly one physical device. Native APIs are replaceable for hardware-free contract tests.</summary>
public interface IDaqIoSession : IDisposable
{
    string Identity { get; }
    IReadOnlyList<InstrumentChannel> Channels { get; }
    double Read(string channel);
    void Write(string channel, double value);
}

/// <summary>Shared scalar driver contract, serialized per device identity, with lazy native loading and explicit safe outputs.</summary>
public sealed class DaqIoDevice : IInstrumentDriver
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private readonly Func<IDaqIoSession> _open;
    private readonly InstrumentOptions _options;
    private readonly List<string> _log = [];
    private IDaqIoSession? _session;
    private bool _disposed;
    public DaqIoDevice(string adapter, string model, string resource, InstrumentOptions options, Func<IDaqIoSession>? open = null)
    {
        var profile = DaqModels.Get(adapter, model);
        var connection = adapter == DaqModels.Simulation ? "Simulation" : DaqModels.Connection(profile, options);
        if (string.IsNullOrWhiteSpace(resource) || resource.Equals("ANY", StringComparison.OrdinalIgnoreCase))
            throw new InstrumentException("DAQ-Gerät eindeutig über Seriennummer, Geräte-ID oder Netzwerkadresse auswählen; ANY ist für Prüfplätze nicht erlaubt.");
        Adapter = adapter; Model = profile.Name; Resource = resource; _options = options;
        _gate = Gates.GetOrAdd(adapter + "|" + resource.Trim(), _ => new());
        _open = open ?? (() => adapter == DaqModels.Simulation ? new SimulatedDaqIoSession(profile, options) : DaqSessionFactory.Open(profile, connection, resource, options));
    }
    public string Adapter { get; }
    public string Model { get; }
    public string Resource { get; }
    public string Name => $"{Model} ({Resource})";
    public IReadOnlyList<string> Log { get { lock (_gate) return _log.ToArray(); } }
    public IReadOnlyList<InstrumentChannel> Channels { get { lock (_gate) return Session().Channels; } }
    private IDaqIoSession Session()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session ??= _open();
    }
    private InstrumentChannel Channel(string name, ChannelAccess? access = null)
    {
        var channel = Session().Channels.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InstrumentException($"{Model}: Kanal „{name}“ nicht vorhanden.");
        if (access.HasValue && !channel.Access.HasFlag(access.Value)) throw new InstrumentException($"{Model}: Zugriff {access} auf „{name}“ nicht erlaubt.");
        return channel;
    }
    public ChannelCapabilities GetCapabilities(string channel)
    {
        lock (_gate) { var info = Channel(channel); return new(info.Unit, info.Min, info.Max); }
    }
    public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => Run(() =>
    {
        var info = Channel(channel, ChannelAccess.Read);
        var value = Session().Read(info.Name);
        if (!double.IsFinite(value)) throw new InstrumentException($"{Model}: ungültiger Messwert auf {info.Name}.");
        _log.Add($"Lesen {info.Name}: {Scpi.N(value)} {info.Unit}"); return value;
    }, cancellationToken);
    public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => Run(() =>
    {
        var info = Channel(channel, ChannelAccess.Write);
        if (!double.IsFinite(value) || value < info.Min || value > info.Max || info.Unit == "bool" && value is not (0 or 1))
            throw new InstrumentException($"{Model}: Ausgangswert für {info.Name} außerhalb des zulässigen Bereichs {info.Min}..{info.Max} {info.Unit}.");
        Session().Write(info.Name, value); _log.Add($"Setzen {info.Name}: {Scpi.N(value)} {info.Unit}"); return true;
    }, cancellationToken);
    public Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken) => Run(() =>
    {
        var session = Session();
        return new InstrumentProbe(true, session.Identity, session.Channels.Select(channel => $"{channel.Name}: {channel.Access}, {channel.Min}..{channel.Max} {channel.Unit}").ToArray(), _log.ToArray());
    }, cancellationToken);
    public async Task SafeStateAsync(CancellationToken cancellationToken)
    {
        // Opening or probing a device must not set outputs. Only explicitly configured safe outputs are changed.
        var errors = new List<Exception>();
        foreach (var pair in DaqSettings.Map(_options, "Sichere Ausgänge"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!double.TryParse(pair.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new InstrumentException($"Ungültiger sicherer Ausgangswert für {pair.Key}.");
                await WriteAsync(pair.Key, value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new InstrumentException("Nicht alle sicheren DAQ-Ausgänge konnten gesetzt werden: " + string.Join("; ", errors.Select(error => error.Message)), new AggregateException(errors));
    }
    private Task<T> Run<T>(Func<T> operation, CancellationToken token) => Task.Run(() =>
    {
        lock (_gate) { token.ThrowIfCancellationRequested(); var value = operation(); token.ThrowIfCancellationRequested(); return value; }
    }, token);
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _session?.Dispose(); _session = null; }
    }
}

internal static class DaqSessionFactory
{
    public static IDaqIoSession Open(DaqModel model, string connection, string resource, InstrumentOptions options)
    {
        try
        {
            return model.Sdk switch
            {
                DaqSdk.Ljm => new LjmIoSession(model, connection, resource, options, NativeLjmApi.Instance),
                DaqSdk.Ud => new UdIoSession(model, connection, resource, options, NativeUdApi.Instance),
                DaqSdk.U12 => new U12IoSession(resource, options, NativeU12Api.Instance),
                _ => new UlIoSession(model, connection, resource, options, NativeUlApi.Instance)
            };
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { throw new InstrumentException($"{model.Name}: {model.Sdk}-Herstellertreiber fehlt, hat die falsche Architektur oder ist inkompatibel. Windows-x64-Laufzeit des Herstellers installieren; siehe Geräteanleitung.", error); }
    }
}
