using System.Globalization;

namespace Testumgebung.Instruments;

/// <summary>Stateful I/O double using the same channel contract as real DAQ devices.</summary>
public sealed class SimulatedDaqIoSession : IDaqIoSession
{
    private readonly Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _feedback;
    public SimulatedDaqIoSession(DaqModel model, InstrumentOptions options)
    {
        Identity = model.Name + " (simuliert, keine Hardware)";
        Channels = model.Sdk == DaqSdk.UniversalLibrary ? UlChannels(options) : LabJackChannels.Create(model);
        foreach (var pair in DaqSettings.Map(options, "Eingaenge"))
        {
            if (!Channels.Any(channel => channel.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)) || !double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InstrumentException("DAQ-Simulation: ungültiger Eingangskanal oder Wert.");
            _values[pair.Key] = value;
        }
        _feedback = DaqSettings.Map(options, "Rueckkopplung");
        foreach (var pair in _feedback)
        {
            var input = Channels.FirstOrDefault(channel => channel.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            var output = Channels.FirstOrDefault(channel => channel.Name.Equals(pair.Value, StringComparison.OrdinalIgnoreCase));
            if (input is null || output is null || !input.Access.HasFlag(ChannelAccess.Read) || !output.Access.HasFlag(ChannelAccess.Write) || input.Unit != output.Unit)
                throw new InstrumentException("DAQ-Simulation: Rückkopplung benötigt lesbaren Eingang und passenden Ausgang mit gleicher Einheit.");
        }
    }
    public string Identity { get; }
    public IReadOnlyList<InstrumentChannel> Channels { get; }
    public double Read(string channel) => _values.GetValueOrDefault(_feedback.GetValueOrDefault(channel) ?? channel);
    public void Write(string channel, double value) => _values[channel] = value;
    public void Dispose() { }
    private static IReadOnlyList<InstrumentChannel> UlChannels(InstrumentOptions options)
    {
        var channels = new List<InstrumentChannel>();
        Add("AI", "V", ChannelAccess.Read); Add("AO", "V", ChannelAccess.Write); Add("DIO", "bool", ChannelAccess.ReadWrite);
        return channels;
        void Add(string prefix, string unit, ChannelAccess access)
        {
            var number = options.Number(prefix + "-Kanaele") ?? 0;
            if (!double.IsFinite(number) || number is < 0 or > 256 || number != Math.Truncate(number)) throw new InstrumentException("DAQ-Simulation: Kanalzahl muss eine ganze Zahl von 0 bis 256 sein.");
            for (var index = 0; index < (int)number; index++) channels.Add(new(prefix + index, unit, unit == "bool" ? 0 : -10, unit == "bool" ? 1 : 10, access, "Explizit konfigurierter simulierter Kanal"));
        }
    }
}
