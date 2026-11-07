using System.Runtime.InteropServices;
using System.Text;

namespace Testumgebung.Instruments;

public interface IUdApi
{
    int Open(int type, int connection, string resource);
    double Get(int handle, int operation, int channel);
    void Put(int handle, int operation, int channel, double value);
    void Release();
}

/// <summary>UD scalar I/O; flexible U3 lines change mode only when that line is accessed.</summary>
public sealed class UdIoSession : IDaqIoSession
{
    private readonly IUdApi _api;
    private readonly int _handle;
    private readonly DaqModel _model;
    private readonly int _range;
    private bool _disposed;
    public UdIoSession(DaqModel model, string connection, string resource, InstrumentOptions options, IUdApi api)
    {
        _api = api; _model = model;
        _range = SelectRange(model, options);
        var type = model.Name.StartsWith("U3", StringComparison.Ordinal) ? 3 : model.Name.StartsWith("UE9", StringComparison.Ordinal) ? 9 : 6;
        _handle = api.Open(type, connection == "USB" ? 1 : 2, resource);
        try
        {
            if (type == 3 && (api.Get(_handle, 1000, 22) != 0) != model.Name.Contains("HV", StringComparison.Ordinal))
                throw new InstrumentException("U3-HV/LV stimmt nicht mit dem angeschlossenen Gerät überein.");
            Identity = $"LabJack {model.Name} / {connection} / {resource}";
            Channels = LabJackChannels.Create(model);
        }
        catch { api.Release(); throw; }
    }
    public string Identity { get; }
    public IReadOnlyList<InstrumentChannel> Channels { get; }
    public double Read(string channel)
    {
        var analog = DaqModels.Index(channel, "AIN");
        if (analog >= 0)
        {
            if (_model.Name.StartsWith("U3", StringComparison.Ordinal))
            {
                if (!(_model.Name.Contains("HV", StringComparison.Ordinal) && analog < 4)) _api.Put(_handle, 2013, analog, 1);
            }
            else _api.Put(_handle, 2000, analog, _range);
            return _api.Get(_handle, 10, analog);
        }
        var bit = DaqModels.Index(channel, "DIO");
        DigitalMode(bit); return _api.Get(_handle, 30, bit);
    }
    public void Write(string channel, double value)
    {
        var analog = DaqModels.Index(channel, "DAC");
        if (analog >= 0)
        {
            if (_model.Name.StartsWith("UE9", StringComparison.Ordinal) || _model.Name.StartsWith("U3", StringComparison.Ordinal) && analog == 1) _api.Put(_handle, 2002, analog, 1);
            _api.Put(_handle, 20, analog, value); return;
        }
        var bit = DaqModels.Index(channel, "DIO");
        DigitalMode(bit); _api.Put(_handle, 40, bit, value);
    }
    private static int SelectRange(DaqModel model, InstrumentOptions options)
    {
        if (model.Name.StartsWith("U3", StringComparison.Ordinal))
        {
            if (options.Text("Bereich") is not null) throw new InstrumentException("U3 verwendet feste modellabhängige Eingangsbereiche.");
            return 0;
        }
        var value = options.Text("Bereich") is null ? (model.Name.StartsWith("UE9", StringComparison.Ordinal) ? 5 : 10)
            : options.Number("Bereich") ?? throw new InstrumentException("Bereich muss eine Zahl sein.");
        if (model.Name.StartsWith("UE9", StringComparison.Ordinal)) return value == 5 ? 3 : throw new InstrumentException("UE9: dieser Adapter verwendet den bipolaren Bereich ±5 V.");
        return value switch { 10 => 2, 1 => 8, 0.1 => 10, 0.01 => 11, _ => throw new InstrumentException("U6: Bereich 10, 1, 0.1 oder 0.01 V wählen.") };
    }
    private void DigitalMode(int bit)
    {
        if (_model.Name.StartsWith("U3", StringComparison.Ordinal) && bit < 16) _api.Put(_handle, 2013, bit, 0);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _api.Release(); }
}

/// <summary>UD Close() closes every UD device. Keep the native library open until the final session releases it.</summary>
public sealed class NativeUdApi : IUdApi
{
    public static NativeUdApi Instance { get; } = new();
    private readonly object _gate = new();
    private int _sessions;
    public int Open(int type, int connection, string resource)
    {
        lock (_gate) { Check(Native.Open(type, connection, resource, 0, out var handle)); _sessions++; return handle; }
    }
    public double Get(int handle, int operation, int channel) { double value = 0; Check(Native.Get(handle, operation, channel, ref value, 0)); return value; }
    public void Put(int handle, int operation, int channel, double value) => Check(Native.Put(handle, operation, channel, value, 0));
    public void Release() { lock (_gate) { if (_sessions > 0 && --_sessions == 0) Native.Close(); } }
    private static void Check(int error)
    {
        if (error == 0) return;
        var text = new StringBuilder(256); Native.Error(error, text);
        throw new InstrumentException($"LabJack UD ({error}): {text}");
    }
    private static class Native
    {
        private const string Library = "LabJackUD.dll";
        [DllImport(Library, EntryPoint="OpenLabJack", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Open(int type, int connection, string address, int firstFound, out int handle);
        [DllImport(Library, EntryPoint="eGet"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Get(int handle, int operation, int channel, ref double value, int extra);
        [DllImport(Library, EntryPoint="ePut"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern int Put(int handle, int operation, int channel, double value, int extra);
        [DllImport(Library, EntryPoint="Close"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern void Close();
        [DllImport(Library, EntryPoint="ErrorToString", CharSet=CharSet.Ansi), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern void Error(int error, StringBuilder text);
    }
}
