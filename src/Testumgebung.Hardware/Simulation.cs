using System.Collections.Concurrent;
using Testumgebung.Contracts;

namespace Testumgebung.Hardware;

/// <summary>
/// Simulationsadapter – erster verbindlicher Entwicklungsweg (Abschnitt 7).
/// Eingänge liefern vorgegebene Werte (optional mit Generator), Ausgänge werden protokolliert.
/// </summary>
public sealed class SimulationAdapter(string name = "Simulation") : IHardwareAdapter, IWaveformAdapter, IWaveformOutputAdapter,
    IByteStreamAdapter, IModbusAdapter, ICanAdapter, ILinAdapter, IOpcUaDevice
{
    IOpcUaClient IOpcUaDevice.OpcUa(string channel)
        => _comm.TryGetValue(channel, out var e) && e is IOpcUaClient c ? c
            : throw new CommunicationException($"Kanal '{channel}' der Simulation ist kein OPC-UA-Server.");

    // ------------------------------------------------------------------ Kommunikation (simulierter Prüfling je Kanal)

    private readonly ConcurrentDictionary<string, object> _comm = new(StringComparer.Ordinal);

    /// <summary>Simulierte Kommunikationsschnittstelle an einem Kanal (<see cref="CommSimulation"/>).</summary>
    public SimulationAdapter ConfigureCommunication(string channel, object endpoint)
    {
        _comm[channel] = endpoint;
        return this;
    }

    public object? CommunicationEndpoint(string channel) => _comm.TryGetValue(channel, out var e) ? e : null;

    private T Comm<T>(string channel) where T : class
        => _comm.TryGetValue(channel, out var e) && e is T t ? t
            : throw new CommunicationException($"Kanal '{channel}' der Simulation ist keine passende Kommunikationsschnittstelle ({typeof(T).Name[1..].Replace("Adapter", "")}).");

    Task IByteStreamAdapter.SendAsync(string channel, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => Comm<IByteStreamAdapter>(channel).SendAsync(channel, data, cancellationToken);

    Task<byte[]> IByteStreamAdapter.ReceiveAsync(string channel, ReceiveRule rule, CancellationToken cancellationToken)
        => Comm<IByteStreamAdapter>(channel).ReceiveAsync(channel, rule, cancellationToken);

    void IByteStreamAdapter.DiscardInput(string channel) => Comm<IByteStreamAdapter>(channel).DiscardInput(channel);

    Task<ushort[]> IModbusAdapter.ReadRegistersAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken)
        => Comm<IModbusAdapter>(channel).ReadRegistersAsync(channel, area, address, count, cancellationToken);

    Task<bool[]> IModbusAdapter.ReadBitsAsync(string channel, ModbusArea area, int address, int count, CancellationToken cancellationToken)
        => Comm<IModbusAdapter>(channel).ReadBitsAsync(channel, area, address, count, cancellationToken);

    Task IModbusAdapter.WriteRegistersAsync(string channel, int address, IReadOnlyList<ushort> values, CancellationToken cancellationToken)
        => Comm<IModbusAdapter>(channel).WriteRegistersAsync(channel, address, values, cancellationToken);

    Task IModbusAdapter.WriteBitsAsync(string channel, int address, IReadOnlyList<bool> values, CancellationToken cancellationToken)
        => Comm<IModbusAdapter>(channel).WriteBitsAsync(channel, address, values, cancellationToken);

    Task ICanAdapter.SendAsync(string channel, CanFrame frame, CancellationToken cancellationToken)
        => Comm<ICanAdapter>(channel).SendAsync(channel, frame, cancellationToken);

    Task<CanFrame> ICanAdapter.ReceiveAsync(string channel, CanFilter filter, TimeSpan timeout, CancellationToken cancellationToken)
        => Comm<ICanAdapter>(channel).ReceiveAsync(channel, filter, timeout, cancellationToken);

    Task<CanFrame> ICanAdapter.ReceiveAnyAsync(string channel, IReadOnlyList<CanFilter> filters, TimeSpan timeout, CancellationToken cancellationToken)
        => Comm<ICanAdapter>(channel).ReceiveAnyAsync(channel, filters, timeout, cancellationToken);

    void ICanAdapter.DiscardInput(string channel) => Comm<ICanAdapter>(channel).DiscardInput(channel);

    Task<byte[]> ILinAdapter.RequestAsync(string channel, byte frameId, int length, LinChecksum checksum, TimeSpan timeout, CancellationToken cancellationToken)
        => Comm<ILinAdapter>(channel).RequestAsync(channel, frameId, length, checksum, timeout, cancellationToken);

    Task ILinAdapter.SendAsync(string channel, byte frameId, byte[] data, LinChecksum checksum, CancellationToken cancellationToken)
        => Comm<ILinAdapter>(channel).SendAsync(channel, frameId, data, checksum, cancellationToken);

    private sealed class OutputSlot(OutputCapabilities caps, IReadOnlyDictionary<string, string> feedback)
    {
        public OutputCapabilities Caps { get; } = caps;
        public IReadOnlyDictionary<string, string> Feedback { get; } = feedback;
        public OutputState State { get; set; } = OutputState.Idle;
        public OutputCurve? Loaded { get; set; }
        public OutputCurve? InDevice { get; set; }
        public Dictionary<string, Func<double, double>?> Saved { get; } = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, OutputSlot> _outputs2 = new(StringComparer.Ordinal);

    /// <summary>Protokoll der Ausgabeschritte (Tests, Anzeige).</summary>
    public ConcurrentQueue<string> OutputLog { get; } = new();

    /// <summary>
    /// Simulierte Kurvenquelle: Fähigkeiten und Rückwirkung (Kurvenkanal → Messkanal dieser Simulation),
    /// damit eine laufende Ausgabe an den Messkanälen messbar ist.
    /// </summary>
    public SimulationAdapter ConfigureOutput(string channel, OutputCapabilities capabilities, IReadOnlyDictionary<string, string>? feedback = null)
    {
        _outputs2[channel] = new OutputSlot(capabilities, feedback ?? new Dictionary<string, string>());
        return this;
    }

    private OutputSlot Slot(string channel)
        => _outputs2.TryGetValue(channel, out var s) ? s : throw new InvalidOperationException($"Kanal '{channel}' ist kein Kurvenausgang.");

    public OutputCapabilities GetOutputCapabilities(string channel) => Slot(channel).Caps;

    public OutputState GetOutputState(string channel) => Slot(channel).State;

    public string? LoadedCurve(string channel) => Slot(channel).InDevice?.Key ?? Slot(channel).Loaded?.Key;

    public Task LoadAsync(string channel, OutputCurve curve, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = Slot(channel);
        lock (slot)
        {
            if (slot.State == OutputState.Running) throw new OutputSequenceException("Ausgabe läuft – erst stoppen, dann neue Kurve laden.");
            var errors = OutputSuitability.Check(slot.Caps, curve).Where(i => i.IsError).ToList();
            if (errors.Count > 0) throw new OutputSequenceException("Gerät ungeeignet: " + string.Join(" ", errors.Select(e => e.Text)));
            slot.Loaded = curve;
            slot.State = OutputState.Loaded;
        }
        OutputLog.Enqueue($"laden {curve.Key}");
        return Task.CompletedTask;
    }

    public Task TransferAsync(string channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = Slot(channel);
        lock (slot)
        {
            if (slot.State != OutputState.Loaded || slot.Loaded is null) throw new OutputSequenceException("Übertragen erst nach dem Laden einer Kurve.");
            slot.InDevice = slot.Loaded;
            slot.State = OutputState.Transferred;
        }
        OutputLog.Enqueue($"übertragen {slot.InDevice.Key}");
        return Task.CompletedTask;
    }

    public Task StartAsync(string channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = Slot(channel);
        lock (slot)
        {
            if (slot.State is not (OutputState.Transferred or OutputState.Stopped) || slot.InDevice is not { } curve)
                throw new OutputSequenceException("Starten erst nach dem Übertragen einer Kurve.");
            for (var i = 0; i < curve.Channels.Count; i++)
            {
                if (!slot.Feedback.TryGetValue(curve.Channels[i].Id, out var input)) continue;
                if (!slot.Saved.ContainsKey(input)) slot.Saved[input] = _waveforms.TryGetValue(input, out var old) ? old : null;
                var index = i;
                _waveforms[input] = t => curve.ValueAt(index, t);
            }
            slot.State = OutputState.Running;
        }
        OutputLog.Enqueue($"starten {slot.InDevice!.Key}");
        return Task.CompletedTask;
    }

    public Task StopAsync(string channel, CancellationToken cancellationToken)
    {
        var slot = Slot(channel);
        lock (slot)
        {
            if (slot.State != OutputState.Running) throw new OutputSequenceException("Ausgabe läuft nicht.");
            foreach (var (input, old) in slot.Saved)
            {
                if (old is null) _waveforms.TryRemove(input, out _);
                else _waveforms[input] = old;
            }
            slot.Saved.Clear();
            slot.State = OutputState.Stopped;
        }
        OutputLog.Enqueue("stoppen");
        return Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, Func<double, double>> _waveforms = new(StringComparer.Ordinal);
    private long _captureCounter;

    /// <summary>Zeitfunktion eines Kanals (t in Sekunden) für Kurvenaufnahmen.</summary>
    public void SetWaveform(string channel, Func<double, double> signal) => _waveforms[channel] = signal;

    /// <summary>Sinus mit Effektivwert, Frequenz und Phasenlage (Grad).</summary>
    public void SetSine(string channel, double rms, double frequency = 50, double phaseDegrees = 0)
        => SetWaveform(channel, t => rms * Math.Sqrt(2) * Math.Sin(2 * Math.PI * frequency * t - phaseDegrees * Math.PI / 180));

    public IClock? Clock { get; set; }

    public Task<WaveformSet> CaptureAsync(IReadOnlyList<string> channels, double sampleRate, int samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Abtastrate muss > 0 sein.");
        if (samples <= 0) throw new ArgumentOutOfRangeException(nameof(samples), "Anzahl Samples muss > 0 sein.");
        var start = (Clock ?? SystemClock.Instance).Now;
        var sync = $"{Name}-{Interlocked.Increment(ref _captureCounter)}";
        var list = new List<Waveform>();
        foreach (var channel in channels)
        {
            if (!_waveforms.TryGetValue(channel, out var f))
                throw new InvalidOperationException($"Kanal '{channel}' liefert keine Kurven.");
            using var writer = new SampleBufferWriter(samples);
            for (var offset = 0; offset < samples; offset += SampleBuffer.BlockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new double[Math.Min(SampleBuffer.BlockSize, samples - offset)];
                for (var i = 0; i < data.Length; i++) data[i] = f((offset + i) / sampleRate);
                writer.Append(data);
            }
            list.Add(new Waveform(channel, GetCapabilities(channel).Unit, sampleRate, start, writer.Complete(), Quality: "simuliert", SyncGroup: sync));
        }
        return Task.FromResult(new WaveformSet(list));
    }

    private readonly ConcurrentDictionary<string, Func<double>> _inputs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _outputs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChannelCapabilities> _caps = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Channel, double Value)> _writeLog = new();

    public string Name { get; } = name;

    public IReadOnlyCollection<(string Channel, double Value)> WriteLog => _writeLog.ToArray();

    public SimulationAdapter Channel(string channel, ChannelCapabilities capabilities)
    {
        _caps[channel] = capabilities;
        return this;
    }

    public void SetInput(string channel, double value) => _inputs[channel] = () => value;

    public void SetInput(string channel, Func<double> generator) => _inputs[channel] = generator;

    public double? GetOutput(string channel) => _outputs.TryGetValue(channel, out var v) ? v : null;

    public ChannelCapabilities GetCapabilities(string channel)
        => _caps.TryGetValue(channel, out var c) ? c : new ChannelCapabilities("", double.MinValue, double.MaxValue);

    public Task<double> ReadAsync(string channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_inputs.TryGetValue(channel, out var gen)) return Task.FromResult(gen());
        if (_outputs.TryGetValue(channel, out var o)) return Task.FromResult(o);
        return Task.FromResult(0.0);
    }

    public Task WriteAsync(string channel, double value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var caps = GetCapabilities(channel);
        if (value < caps.Min || value > caps.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} {caps.Unit} außerhalb des Bereichs {caps.Min}–{caps.Max} von Kanal {channel}.");
        _outputs[channel] = value;
        _writeLog.Enqueue((channel, value));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Prüfplatzkonfiguration: abstrakte Namen → Adapter/Kanal. Tests verwenden nur abstrakte Namen.
/// </summary>
public sealed class HardwareRegistry
{
    private readonly Dictionary<string, ResourceBinding> _bindings = new(StringComparer.Ordinal);

    /// <param name="lockKey">Sperrschlüssel des Geräts/Kanals (Prüfplatzkonfiguration); ohne Angabe der Name.</param>
    public HardwareRegistry Bind(string name, IHardwareAdapter adapter, string channel, bool requiresLock = false, string? lockKey = null)
    {
        _bindings[name] = new ResourceBinding(name, adapter, channel, requiresLock, lockKey);
        return this;
    }

    public ResourceBinding Resolve(string name)
        => _bindings.TryGetValue(name, out var b)
            ? b
            : throw new KeyNotFoundException($"Ressource '{name}' ist in der Prüfplatzkonfiguration nicht zugeordnet.");

    public bool TryResolve(string name, out ResourceBinding binding) => _bindings.TryGetValue(name, out binding!);

    /// <summary>Sperrschlüssel eines abstrakten Namens (nicht zugeordnete Namen sperren unter ihrem Namen).</summary>
    public string LockKeyOf(string name) => _bindings.TryGetValue(name, out var b) ? b.Key : name;

    /// <summary>Anzeigename eines Sperrschlüssels (abstrakte Namen, die ihn verwenden).</summary>
    public string DisplayOf(string key)
    {
        var names = _bindings.Values.Where(b => b.Key == key).Select(b => b.Name).ToList();
        return names.Count == 0 ? key : string.Join("/", names);
    }

    public IReadOnlyCollection<ResourceBinding> Bindings => _bindings.Values;
}
