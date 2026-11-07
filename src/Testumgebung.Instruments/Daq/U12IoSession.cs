using System.Globalization;
using System.Runtime.InteropServices;

namespace Testumgebung.Instruments;

public interface IU12Api
{
    float Firmware(ref int identifier);
    double AnalogIn(ref int identifier, int channel);
    double DigitalIn(ref int identifier, int channel, bool dPort);
    void AnalogOut(ref int identifier, double first, double second);
    void DigitalOut(ref int identifier, int channel, bool dPort, bool state);
}

/// <summary>U12 scalar I/O with explicit paired-DAC state, avoiding the vendor cache default of zero.</summary>
public sealed class U12IoSession : IDaqIoSession
{
    private readonly IU12Api _api;
    private readonly double?[] _outputs = new double?[2];
    private int _identifier;
    public U12IoSession(string resource, InstrumentOptions options, IU12Api api)
    {
        if (!int.TryParse(resource, NumberStyles.None, CultureInfo.InvariantCulture, out _identifier)) throw new InstrumentException("U12 benötigt eine eindeutige numerische Seriennummer oder lokale Geräte-ID.");
        _api = api;
        if (api.Firmware(ref _identifier) <= 0) throw new InstrumentException("U12 wurde nicht gefunden.");
        foreach (var pair in DaqSettings.Map(options, "AO-Paar-Startwerte"))
        {
            var index = DaqModels.Index(pair.Key, "DAC");
            if (index is < 0 or > 1 || !double.TryParse(pair.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value is < 0 or > 5)
                throw new InstrumentException("U12 AO-Paar-Startwerte: DAC0/DAC1 mit Werten zwischen 0 und 5 V angeben.");
            _outputs[index] = value;
        }
        Identity = "LabJack U12 / " + _identifier;
        Channels = LabJackChannels.Create(DaqModels.Get(DaqModels.LabJack, "U12"));
    }
    public string Identity { get; }
    public IReadOnlyList<InstrumentChannel> Channels { get; }
    public double Read(string channel)
    {
        var index = DaqModels.Index(channel, "AIN");
        if (index >= 0) return _api.AnalogIn(ref _identifier, index);
        index = DaqModels.Index(channel, "DIO");
        return _api.DigitalIn(ref _identifier, index < 16 ? index : index - 16, index < 16);
    }
    public void Write(string channel, double value)
    {
        var index = DaqModels.Index(channel, "DAC");
        if (index >= 0)
        {
            var other = _outputs[1 - index] ?? throw new InstrumentException("U12 setzt beide Analogausgänge gemeinsam. Den anderen Ausgang unter AO-Paar-Startwerte ausdrücklich festlegen, bevor ein Ausgang gesetzt wird.");
            _api.AnalogOut(ref _identifier, index == 0 ? value : other, index == 1 ? value : other);
            _outputs[index] = value; return;
        }
        index = DaqModels.Index(channel, "DIO");
        _api.DigitalOut(ref _identifier, index < 16 ? index : index - 16, index < 16, value == 1);
    }
    public void Dispose() { }
}

public sealed class NativeU12Api : IU12Api
{
    public static NativeU12Api Instance { get; } = new();
    public float Firmware(ref int identifier) => Native.Firmware(ref identifier);
    public double AnalogIn(ref int identifier, int channel)
    {
        Check(Native.AnalogIn(ref identifier, 0, channel, 0, out var overvoltage, out var value));
        if (overvoltage != 0) throw new InstrumentException("U12 meldet eine Überspannung am Analogeingang.");
        return value;
    }
    public double DigitalIn(ref int identifier, int channel, bool dPort) { Check(Native.DigitalIn(ref identifier, 0, channel, dPort ? 1 : 0, out var value)); return value; }
    public void AnalogOut(ref int identifier, double first, double second) => Check(Native.AnalogOut(ref identifier, 0, (float)first, (float)second));
    public void DigitalOut(ref int identifier, int channel, bool dPort, bool state) => Check(Native.DigitalOut(ref identifier, 0, channel, dPort ? 1 : 0, state ? 1 : 0));
    private static void Check(int error) { if (error != 0) throw new InstrumentException($"LabJack U12: Treiberfehler {error}."); }
    private static class Native
    {
        private const string Library = "ljackuw.dll";
        [DllImport(Library, EntryPoint="GetFirmwareVersion"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern float Firmware(ref int identifier);
        [DllImport(Library, EntryPoint="EAnalogIn"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int AnalogIn(ref int identifier, int demo, int channel, int gain, out int overvoltage, out float value);
        [DllImport(Library, EntryPoint="EDigitalIn"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int DigitalIn(ref int identifier, int demo, int channel, int dPort, out int value);
        [DllImport(Library, EntryPoint="EAnalogOut"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int AnalogOut(ref int identifier, int demo, float first, float second);
        [DllImport(Library, EntryPoint="EDigitalOut"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int DigitalOut(ref int identifier, int demo, int channel, int dPort, int state);
    }
}
