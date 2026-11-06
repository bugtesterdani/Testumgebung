using System.Runtime.InteropServices;
using System.Text;

namespace Testumgebung.Instruments;

public interface ILjmApi
{
    int Open(string model, string connection, string identifier);
    int DeviceType(int handle);
    double Read(int handle, string channel);
    void Write(int handle, string channel, double value);
    void Close(int handle);
}

/// <summary>One explicitly identified T-series handle; discovery never writes an output.</summary>
public sealed class LjmIoSession : IDaqIoSession
{
    private readonly ILjmApi _api;
    private readonly int _handle;
    private readonly InstrumentOptions _options;
    private readonly DaqModel _model;
    private bool _disposed;
    public LjmIoSession(DaqModel model, string connection, string resource, InstrumentOptions options, ILjmApi api)
    {
        _api = api; _options = options;
        _model = model;
        var type = model.Name.StartsWith("T4", StringComparison.Ordinal) ? 4 : model.Name == "T8" ? 8 : 7;
        _handle = api.Open("T" + type, connection == "Ethernet" ? "ETHERNET" : connection == "WLAN" ? "WIFI" : "USB", resource);
        try
        {
            if (api.DeviceType(_handle) != type) throw new InstrumentException("Angeschlossenes LabJack passt nicht zum gewählten Modell.");
            Identity = $"LabJack {model.Name} / {connection} / {resource}";
            Channels = LabJackChannels.Create(model);
        }
        catch { api.Close(_handle); throw; }
    }
    public string Identity { get; }
    public IReadOnlyList<InstrumentChannel> Channels { get; }
    public double Read(string channel)
    {
        var index = DaqModels.Index(channel, "AIN");
        if (index >= 0 && _options.Text("Bereich") is not null)
        {
            if (_model.Name.StartsWith("T4", StringComparison.Ordinal)) throw new InstrumentException("T4 verwendet feste modellabhängige Eingangsbereiche.");
            var range = _options.Number("Bereich") ?? throw new InstrumentException("Bereich muss eine Zahl sein.");
            var maximum = _model.Name == "T8" ? 11 : 10;
            if (!double.IsFinite(range) || range <= 0 || range > maximum) throw new InstrumentException($"LabJack-Eingangsbereich muss größer 0 und höchstens {maximum} V sein.");
            _api.Write(_handle, channel + "_RANGE", range);
        }
        return _api.Read(_handle, channel);
    }
    public void Write(string channel, double value) => _api.Write(_handle, channel, value);
    public void Dispose() { if (_disposed) return; _disposed = true; _api.Close(_handle); }
}

internal static class LabJackChannels
{
    public static IReadOnlyList<InstrumentChannel> Create(DaqModel model)
    {
        var list = new List<InstrumentChannel>();
        for (var index = 0; index < model.AnalogInputs; index++)
        {
            var limits = InputLimits(model, index);
            list.Add(new("AIN" + index, "V", limits.Min, limits.Max, ChannelAccess.Read, "Analogeingang; modellabhängige Leitungskonfiguration"));
        }
        for (var index = 0; index < model.AnalogOutputs; index++)
            list.Add(new("DAC" + index, "V", model.Name == "T8" ? -0.1 : 0, model.Name == "T8" ? 10 : 5, ChannelAccess.Write, "Analogausgang"));
        var first = model.Name.StartsWith("T4", StringComparison.Ordinal) || model.Name.StartsWith("U3-HV", StringComparison.Ordinal) ? 4 : 0;
        for (var index = first; index < model.DigitalBits; index++) list.Add(new("DIO" + index, "bool", 0, 1, ChannelAccess.ReadWrite, "Digital lesen = Eingang, setzen = Ausgang"));
        return list;
    }
    private static (double Min, double Max) InputLimits(DaqModel model, int index)
    {
        if (model.Name == "T8") return (-11, 11);
        if (model.Name.StartsWith("UE9", StringComparison.Ordinal)) return (-5, 5);
        if (model.Name.StartsWith("T4", StringComparison.Ordinal) && index >= 4) return (0, 2.5);
        if (!model.Name.StartsWith("U3", StringComparison.Ordinal)) return (-10, 10);
        return model.Name.Contains("HV", StringComparison.Ordinal) && index < 4 ? (-10, 10) : (0, 2.44);
    }
}

public sealed class NativeLjmApi : ILjmApi
{
    public static NativeLjmApi Instance { get; } = new();
    public int Open(string model, string connection, string identifier) { Check(Native.Open(model, connection, identifier, out var handle)); return handle; }
    public int DeviceType(int handle) { Check(Native.Info(handle, out var type, out _, out _, out _, out _, out _)); return type; }
    public double Read(int handle, string channel) { Check(Native.Read(handle, channel, out var value)); return value; }
    public void Write(int handle, string channel, double value) => Check(Native.Write(handle, channel, value));
    public void Close(int handle) => Check(Native.Close(handle));
    private static void Check(int error)
    {
        if (error == 0) return;
        var text = new StringBuilder(256); Native.Error(error, text);
        throw new InstrumentException($"LabJack LJM ({error}): {text}");
    }
    private static class Native
    {
        private const string Library = "LabJackM.dll";
        [DllImport(Library, EntryPoint="LJM_OpenS", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Open(string model, string connection, string identifier, out int handle);
        [DllImport(Library, EntryPoint="LJM_GetHandleInfo"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Info(int handle, out int type, out int connection, out int serial, out int ip, out int port, out int maxBytes);
        [DllImport(Library, EntryPoint="LJM_eReadName", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Read(int handle, string channel, out double value);
        [DllImport(Library, EntryPoint="LJM_eWriteName", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Write(int handle, string channel, double value);
        [DllImport(Library, EntryPoint="LJM_Close"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Close(int handle);
        [DllImport(Library, EntryPoint="LJM_ErrorToString", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Error(int error, StringBuilder text);
    }
}
