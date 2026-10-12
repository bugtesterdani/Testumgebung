using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Testumgebung.Contracts;

namespace Testumgebung.Instruments;

/// <summary>Angaben eines DAQmx-Geräts (aus dem Treiber gelesen).</summary>
public sealed record DaqDeviceInfo(
    string Name,
    string ProductType,
    bool IsSimulated,
    IReadOnlyList<string> AiChannels,
    IReadOnlyList<string> AoChannels,
    IReadOnlyList<string> DoLines,
    IReadOnlyList<string> DiLines,
    double AiMax,
    double AoMax,
    double AiMaxRate,
    double AoMaxRate,
    bool SimultaneousSampling);

/// <summary>Anschlussart analoger Eingänge (DAQmx-Konstanten).</summary>
public enum AiTerminal
{
    Default = -1,
    Rse = 10083,
    Nrse = 10078,
    Diff = 10106,
    PseudoDiff = 12529,
}

/// <summary>
/// Die verwendeten Funktionen der NI-DAQmx-C-API (nicaiu.dll). Hinter einer Schnittstelle, damit die Treiberlogik
/// (Tasks, Takt, Blöcke, Abbruch) ohne installierten Treiber getestet werden kann.
/// </summary>
public interface IDaqmxApi
{
    bool IsAvailable { get; }
    IReadOnlyList<string> DeviceNames();
    DaqDeviceInfo DeviceInfo(string device);
    nint CreateTask(string name);
    void ClearTask(nint task);
    void StartTask(nint task);
    void StopTask(nint task);
    void CreateAIVoltageChan(nint task, string physical, AiTerminal terminal, double min, double max);
    void CreateAOVoltageChan(nint task, string physical, double min, double max);
    void CreateDOChan(nint task, string lines);
    void CreateDIChan(nint task, string lines);
    void CfgSampClkTiming(nint task, double rate, bool continuous, long samplesPerChannel);
    void CfgDigEdgeStartTrig(nint task, string source, bool rising);
    void SetWriteRegenMode(nint task, bool allow);
    double ReadAnalogScalar(nint task, double timeout);
    /// <summary>Verschachtelt (je Abtastzeitpunkt alle Kanäle). Rückgabe: gelesene Samples je Kanal.</summary>
    int ReadAnalogInterleaved(nint task, int samplesPerChannel, double timeout, double[] buffer);
    void WriteAnalogScalar(nint task, double value, double timeout);
    /// <summary>Kanalweise (erst alle Samples Kanal 0, dann Kanal 1 …).</summary>
    int WriteAnalogByChannel(nint task, int samplesPerChannel, bool autoStart, double timeout, double[] data);
    void WriteDigitalLines(nint task, byte[] states, double timeout);
    byte[] ReadDigitalLines(nint task, int lines, double timeout);
}

/// <summary>P/Invoke auf nicaiu.dll (NI-DAQmx, Windows). Fehler (Status &lt; 0) → <see cref="InstrumentException"/> mit erweitertem Text.</summary>
public sealed class NativeDaqmxApi : IDaqmxApi
{
    private const string Dll = "nicaiu";
    private const int FiniteSamps = 10178;
    private const int ContSamps = 10123;
    private const int Rising = 10280;
    private const int Falling = 10171;
    private const int Volts = 10348;
    private const int AllowRegen = 10097;
    private const int DoNotAllowRegen = 10158;
    private const int ChanForAllLines = 1;
    private const int ChanPerLine = 0;
    private const uint GroupByChannel = 0;
    private const uint GroupByScanNumber = 1;

    public static NativeDaqmxApi Instance { get; } = new();

    public bool IsAvailable => NativeLibrary.TryLoad(Dll, typeof(NativeDaqmxApi).Assembly, null, out _);

    private readonly INativeApi _api;
    public NativeDaqmxApi() : this(new DaqNativeApi()) { }
    internal NativeDaqmxApi(INativeApi api) => _api = api;

    internal interface INativeApi
    {
        int DAQmxCreateTask(string name, out nint task);
        int DAQmxClearTask(nint task);
        int DAQmxStartTask(nint task);
        int DAQmxStopTask(nint task);
        int DAQmxCreateAIVoltageChan(nint task, string physical, string name, int terminal, double min, double max, int units, string? scale);
        int DAQmxCreateAOVoltageChan(nint task, string physical, string name, double min, double max, int units, string? scale);
        int DAQmxCreateDOChan(nint task, string lines, string name, int grouping);
        int DAQmxCreateDIChan(nint task, string lines, string name, int grouping);
        int DAQmxCfgSampClkTiming(nint task, string source, double rate, int edge, int mode, ulong samples);
        int DAQmxCfgDigEdgeStartTrig(nint task, string source, int edge);
        int DAQmxSetWriteRegenMode(nint task, int mode);
        int DAQmxReadAnalogScalarF64(nint task, double timeout, out double value, nint reserved);
        int DAQmxReadAnalogF64(nint task, int samples, double timeout, uint fill, double[] data, uint size, out int read, nint reserved);
        int DAQmxWriteAnalogScalarF64(nint task, uint autoStart, double timeout, double value, nint reserved);
        int DAQmxWriteAnalogF64(nint task, int samples, uint autoStart, double timeout, uint layout, double[] data, out int written, nint reserved);
        int DAQmxWriteDigitalLines(nint task, int samples, uint autoStart, double timeout, uint layout, byte[] data, out int written, nint reserved);
        int DAQmxReadDigitalLines(nint task, int samples, double timeout, uint fill, byte[] data, uint size, out int read, out int bytesPerSamp, nint reserved);
        int DAQmxGetExtendedErrorInfo(byte[] text, uint size);
        int DAQmxGetSysDevNames(byte[] data, uint size);
        int DAQmxGetDevProductType(string device, byte[] data, uint size);
        int DAQmxGetDevIsSimulated(string device, out uint value);
        int DAQmxGetDevAIPhysicalChans(string device, byte[] data, uint size);
        int DAQmxGetDevAOPhysicalChans(string device, byte[] data, uint size);
        int DAQmxGetDevDOLines(string device, byte[] data, uint size);
        int DAQmxGetDevDILines(string device, byte[] data, uint size);
        int DAQmxGetDevAIVoltageRngs(string device, double[] data, uint size);
        int DAQmxGetDevAOVoltageRngs(string device, double[] data, uint size);
        int DAQmxGetDevAIMaxMultiChanRate(string device, out double value);
        int DAQmxGetDevAOMaxRate(string device, out double value);
        int DAQmxGetDevAISimultaneousSamplingSupported(string device, out uint value);
    }

    private sealed class DaqNativeApi : INativeApi
    {
        public int DAQmxCreateTask(string name, out nint task) => Native.DAQmxCreateTask(name, out task);
        public int DAQmxClearTask(nint task) => Native.DAQmxClearTask(task);
        public int DAQmxStartTask(nint task) => Native.DAQmxStartTask(task);
        public int DAQmxStopTask(nint task) => Native.DAQmxStopTask(task);
        public int DAQmxCreateAIVoltageChan(nint task, string physical, string name, int terminal, double min, double max, int units, string? scale) => Native.DAQmxCreateAIVoltageChan(task, physical, name, terminal, min, max, units, scale);
        public int DAQmxCreateAOVoltageChan(nint task, string physical, string name, double min, double max, int units, string? scale) => Native.DAQmxCreateAOVoltageChan(task, physical, name, min, max, units, scale);
        public int DAQmxCreateDOChan(nint task, string lines, string name, int grouping) => Native.DAQmxCreateDOChan(task, lines, name, grouping);
        public int DAQmxCreateDIChan(nint task, string lines, string name, int grouping) => Native.DAQmxCreateDIChan(task, lines, name, grouping);
        public int DAQmxCfgSampClkTiming(nint task, string source, double rate, int edge, int mode, ulong samples) => Native.DAQmxCfgSampClkTiming(task, source, rate, edge, mode, samples);
        public int DAQmxCfgDigEdgeStartTrig(nint task, string source, int edge) => Native.DAQmxCfgDigEdgeStartTrig(task, source, edge);
        public int DAQmxSetWriteRegenMode(nint task, int mode) => Native.DAQmxSetWriteRegenMode(task, mode);
        public int DAQmxReadAnalogScalarF64(nint task, double timeout, out double value, nint reserved) => Native.DAQmxReadAnalogScalarF64(task, timeout, out value, reserved);
        public int DAQmxReadAnalogF64(nint task, int samples, double timeout, uint fill, double[] data, uint size, out int read, nint reserved) => Native.DAQmxReadAnalogF64(task, samples, timeout, fill, data, size, out read, reserved);
        public int DAQmxWriteAnalogScalarF64(nint task, uint autoStart, double timeout, double value, nint reserved) => Native.DAQmxWriteAnalogScalarF64(task, autoStart, timeout, value, reserved);
        public int DAQmxWriteAnalogF64(nint task, int samples, uint autoStart, double timeout, uint layout, double[] data, out int written, nint reserved) => Native.DAQmxWriteAnalogF64(task, samples, autoStart, timeout, layout, data, out written, reserved);
        public int DAQmxWriteDigitalLines(nint task, int samples, uint autoStart, double timeout, uint layout, byte[] data, out int written, nint reserved) => Native.DAQmxWriteDigitalLines(task, samples, autoStart, timeout, layout, data, out written, reserved);
        public int DAQmxReadDigitalLines(nint task, int samples, double timeout, uint fill, byte[] data, uint size, out int read, out int bytesPerSamp, nint reserved) => Native.DAQmxReadDigitalLines(task, samples, timeout, fill, data, size, out read, out bytesPerSamp, reserved);
        public int DAQmxGetExtendedErrorInfo(byte[] text, uint size) => Native.DAQmxGetExtendedErrorInfo(text, size);
        public int DAQmxGetSysDevNames(byte[] data, uint size) => Native.DAQmxGetSysDevNames(data, size);
        public int DAQmxGetDevProductType(string device, byte[] data, uint size) => Native.DAQmxGetDevProductType(device, data, size);
        public int DAQmxGetDevIsSimulated(string device, out uint value) => Native.DAQmxGetDevIsSimulated(device, out value);
        public int DAQmxGetDevAIPhysicalChans(string device, byte[] data, uint size) => Native.DAQmxGetDevAIPhysicalChans(device, data, size);
        public int DAQmxGetDevAOPhysicalChans(string device, byte[] data, uint size) => Native.DAQmxGetDevAOPhysicalChans(device, data, size);
        public int DAQmxGetDevDOLines(string device, byte[] data, uint size) => Native.DAQmxGetDevDOLines(device, data, size);
        public int DAQmxGetDevDILines(string device, byte[] data, uint size) => Native.DAQmxGetDevDILines(device, data, size);
        public int DAQmxGetDevAIVoltageRngs(string device, double[] data, uint size) => Native.DAQmxGetDevAIVoltageRngs(device, data, size);
        public int DAQmxGetDevAOVoltageRngs(string device, double[] data, uint size) => Native.DAQmxGetDevAOVoltageRngs(device, data, size);
        public int DAQmxGetDevAIMaxMultiChanRate(string device, out double value) => Native.DAQmxGetDevAIMaxMultiChanRate(device, out value);
        public int DAQmxGetDevAOMaxRate(string device, out double value) => Native.DAQmxGetDevAOMaxRate(device, out value);
        public int DAQmxGetDevAISimultaneousSamplingSupported(string device, out uint value) => Native.DAQmxGetDevAISimultaneousSamplingSupported(device, out value);
    }

    private static class Native
    {
        [DllImport(Dll)] public static extern int DAQmxCreateTask([MarshalAs(UnmanagedType.LPStr)] string name, out nint task);
        [DllImport(Dll)] public static extern int DAQmxClearTask(nint task);
        [DllImport(Dll)] public static extern int DAQmxStartTask(nint task);
        [DllImport(Dll)] public static extern int DAQmxStopTask(nint task);
        [DllImport(Dll)] public static extern int DAQmxCreateAIVoltageChan(nint task, [MarshalAs(UnmanagedType.LPStr)] string physical, [MarshalAs(UnmanagedType.LPStr)] string name, int terminal, double min, double max, int units, [MarshalAs(UnmanagedType.LPStr)] string? scale);
        [DllImport(Dll)] public static extern int DAQmxCreateAOVoltageChan(nint task, [MarshalAs(UnmanagedType.LPStr)] string physical, [MarshalAs(UnmanagedType.LPStr)] string name, double min, double max, int units, [MarshalAs(UnmanagedType.LPStr)] string? scale);
        [DllImport(Dll)] public static extern int DAQmxCreateDOChan(nint task, [MarshalAs(UnmanagedType.LPStr)] string lines, [MarshalAs(UnmanagedType.LPStr)] string name, int grouping);
        [DllImport(Dll)] public static extern int DAQmxCreateDIChan(nint task, [MarshalAs(UnmanagedType.LPStr)] string lines, [MarshalAs(UnmanagedType.LPStr)] string name, int grouping);
        [DllImport(Dll)] public static extern int DAQmxCfgSampClkTiming(nint task, [MarshalAs(UnmanagedType.LPStr)] string source, double rate, int edge, int mode, ulong samples);
        [DllImport(Dll)] public static extern int DAQmxCfgDigEdgeStartTrig(nint task, [MarshalAs(UnmanagedType.LPStr)] string source, int edge);
        [DllImport(Dll)] public static extern int DAQmxSetWriteRegenMode(nint task, int mode);
        [DllImport(Dll)] public static extern int DAQmxReadAnalogScalarF64(nint task, double timeout, out double value, nint reserved);
        [DllImport(Dll)] public static extern int DAQmxReadAnalogF64(nint task, int samples, double timeout, uint fill, [Out] double[] data, uint size, out int read, nint reserved);
        [DllImport(Dll)] public static extern int DAQmxWriteAnalogScalarF64(nint task, uint autoStart, double timeout, double value, nint reserved);
        [DllImport(Dll)] public static extern int DAQmxWriteAnalogF64(nint task, int samples, uint autoStart, double timeout, uint layout, double[] data, out int written, nint reserved);
        [DllImport(Dll)] public static extern int DAQmxWriteDigitalLines(nint task, int samples, uint autoStart, double timeout, uint layout, byte[] data, out int written, nint reserved);
        [DllImport(Dll)] public static extern int DAQmxReadDigitalLines(nint task, int samples, double timeout, uint fill, [Out] byte[] data, uint size, out int read, out int bytesPerSamp, nint reserved);
        [DllImport(Dll)] public static extern int DAQmxGetExtendedErrorInfo([Out] byte[] text, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetSysDevNames([Out] byte[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevProductType([MarshalAs(UnmanagedType.LPStr)] string device, [Out] byte[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevIsSimulated([MarshalAs(UnmanagedType.LPStr)] string device, out uint value);
        [DllImport(Dll)] public static extern int DAQmxGetDevAIPhysicalChans([MarshalAs(UnmanagedType.LPStr)] string device, [Out] byte[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevAOPhysicalChans([MarshalAs(UnmanagedType.LPStr)] string device, [Out] byte[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevDOLines([MarshalAs(UnmanagedType.LPStr)] string device, [Out] byte[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevDILines([MarshalAs(UnmanagedType.LPStr)] string device, [Out] byte[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevAIVoltageRngs([MarshalAs(UnmanagedType.LPStr)] string device, [Out] double[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevAOVoltageRngs([MarshalAs(UnmanagedType.LPStr)] string device, [Out] double[] data, uint size);
        [DllImport(Dll)] public static extern int DAQmxGetDevAIMaxMultiChanRate([MarshalAs(UnmanagedType.LPStr)] string device, out double value);
        [DllImport(Dll)] public static extern int DAQmxGetDevAOMaxRate([MarshalAs(UnmanagedType.LPStr)] string device, out double value);
        [DllImport(Dll)] public static extern int DAQmxGetDevAISimultaneousSamplingSupported([MarshalAs(UnmanagedType.LPStr)] string device, out uint value);

    }

    private void Check(int status, string call)
    {
        if (status >= 0) return;
        var buffer = new byte[2048];
        try
        {
            _api.DAQmxGetExtendedErrorInfo(buffer, (uint)buffer.Length);
        }
        catch (DllNotFoundException)
        {
        }
        var text = Encoding.ASCII.GetString(buffer).TrimEnd('\0').Trim();
        throw new InstrumentException($"NI-DAQmx {call} ({status}): {(text.Length > 0 ? text : "Fehler")}");
    }

    private static T Call<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (DllNotFoundException ex)
        {
            throw new InstrumentException("NI-DAQmx ist nicht installiert (nicaiu.dll fehlt). Ohne Hardware: in NI MAX ein simuliertes Gerät anlegen.", ex);
        }
    }

    private static void Call(Action f) => Call(() => { f(); return 0; });

    private string Text(Func<byte[], uint, int> f, string call)
    {
        var buffer = new byte[8192];
        Check(f(buffer, (uint)buffer.Length), call);
        return Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }

    private static List<string> List(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    public IReadOnlyList<string> DeviceNames() => Call(() => List(Text(_api.DAQmxGetSysDevNames, "DAQmxGetSysDevNames")));

    public DaqDeviceInfo DeviceInfo(string device) => Call(() =>
    {
        double MaxRange(Func<string, double[], uint, int> f)
        {
            var r = new double[64];
            return f(device, r, (uint)r.Length) < 0 ? 10 : r.Select(Math.Abs).DefaultIfEmpty(10).Max() is var m && m > 0 ? m : 10;
        }
        _api.DAQmxGetDevIsSimulated(device, out var sim);
        var aiRate = _api.DAQmxGetDevAIMaxMultiChanRate(device, out var air) < 0 ? 0 : air;
        var aoRate = _api.DAQmxGetDevAOMaxRate(device, out var aor) < 0 ? 0 : aor;
        var simultaneous = _api.DAQmxGetDevAISimultaneousSamplingSupported(device, out var simu) >= 0 && simu != 0;
        string Opt(Func<string, byte[], uint, int> f)
        {
            var b = new byte[8192];
            return f(device, b, (uint)b.Length) < 0 ? "" : Encoding.ASCII.GetString(b).TrimEnd('\0');
        }
        return new DaqDeviceInfo(device, Text((b, n) => _api.DAQmxGetDevProductType(device, b, n), "DAQmxGetDevProductType"), sim != 0,
            List(Opt(_api.DAQmxGetDevAIPhysicalChans)), List(Opt(_api.DAQmxGetDevAOPhysicalChans)), List(Opt(_api.DAQmxGetDevDOLines)), List(Opt(_api.DAQmxGetDevDILines)),
            MaxRange(_api.DAQmxGetDevAIVoltageRngs), MaxRange(_api.DAQmxGetDevAOVoltageRngs), aiRate, aoRate, simultaneous);
    });

    public nint CreateTask(string name) => Call(() =>
    {
        Check(_api.DAQmxCreateTask("", out var t), "DAQmxCreateTask");
        return t;
    });

    public void ClearTask(nint task) => Call(() => Check(_api.DAQmxClearTask(task), "DAQmxClearTask"));
    public void StartTask(nint task) => Call(() => Check(_api.DAQmxStartTask(task), "DAQmxStartTask"));
    public void StopTask(nint task) => Call(() => Check(_api.DAQmxStopTask(task), "DAQmxStopTask"));

    public void CreateAIVoltageChan(nint task, string physical, AiTerminal terminal, double min, double max)
        => Call(() => Check(_api.DAQmxCreateAIVoltageChan(task, physical, "", (int)terminal, min, max, Volts, null), "DAQmxCreateAIVoltageChan"));

    public void CreateAOVoltageChan(nint task, string physical, double min, double max)
        => Call(() => Check(_api.DAQmxCreateAOVoltageChan(task, physical, "", min, max, Volts, null), "DAQmxCreateAOVoltageChan"));

    public void CreateDOChan(nint task, string lines) => Call(() => Check(_api.DAQmxCreateDOChan(task, lines, "", ChanPerLine), "DAQmxCreateDOChan"));
    public void CreateDIChan(nint task, string lines) => Call(() => Check(_api.DAQmxCreateDIChan(task, lines, "", ChanPerLine), "DAQmxCreateDIChan"));

    public void CfgSampClkTiming(nint task, double rate, bool continuous, long samplesPerChannel)
        => Call(() => Check(_api.DAQmxCfgSampClkTiming(task, "", rate, Rising, continuous ? ContSamps : FiniteSamps, (ulong)samplesPerChannel), "DAQmxCfgSampClkTiming"));

    public void CfgDigEdgeStartTrig(nint task, string source, bool rising)
        => Call(() => Check(_api.DAQmxCfgDigEdgeStartTrig(task, source, rising ? Rising : Falling), "DAQmxCfgDigEdgeStartTrig"));

    public void SetWriteRegenMode(nint task, bool allow) => Call(() => Check(_api.DAQmxSetWriteRegenMode(task, allow ? AllowRegen : DoNotAllowRegen), "DAQmxSetWriteRegenMode"));

    public double ReadAnalogScalar(nint task, double timeout) => Call(() =>
    {
        Check(_api.DAQmxReadAnalogScalarF64(task, timeout, out var v, 0), "DAQmxReadAnalogScalarF64");
        return v;
    });

    public int ReadAnalogInterleaved(nint task, int samplesPerChannel, double timeout, double[] buffer) => Call(() =>
    {
        Check(_api.DAQmxReadAnalogF64(task, samplesPerChannel, timeout, GroupByScanNumber, buffer, (uint)buffer.Length, out var read, 0), "DAQmxReadAnalogF64");
        return read;
    });

    public void WriteAnalogScalar(nint task, double value, double timeout) => Call(() => Check(_api.DAQmxWriteAnalogScalarF64(task, 1, timeout, value, 0), "DAQmxWriteAnalogScalarF64"));

    public int WriteAnalogByChannel(nint task, int samplesPerChannel, bool autoStart, double timeout, double[] data) => Call(() =>
    {
        Check(_api.DAQmxWriteAnalogF64(task, samplesPerChannel, autoStart ? 1u : 0u, timeout, GroupByChannel, data, out var written, 0), "DAQmxWriteAnalogF64");
        return written;
    });

    public void WriteDigitalLines(nint task, byte[] states, double timeout)
        => Call(() => Check(_api.DAQmxWriteDigitalLines(task, 1, 1, timeout, GroupByChannel, states, out _, 0), "DAQmxWriteDigitalLines"));

    public byte[] ReadDigitalLines(nint task, int lines, double timeout) => Call(() =>
    {
        var data = new byte[Math.Max(1, lines)];
        Check(_api.DAQmxReadDigitalLines(task, 1, timeout, GroupByChannel, data, (uint)data.Length, out _, out _, 0), "DAQmxReadDigitalLines");
        return data;
    });
}

/// <summary>
/// NI-DAQmx-Gerät (USB-6001…6363, PCIe-63xx, CompactDAQ-Module wie NI 9215/9225/9227/9263/9401):
/// AI lesen (Einzelwert), AO setzen, DO/DI-Leitungen, hardwaregetaktete Mehrkanal-Aufnahme mit einem Abtasttakt
/// (gemeinsame Zeitbasis; optional Starttrigger z. B. „/Dev1/PFI0“), Kurvenausgabe auf AO („AO“, Laden/Übertragen/
/// Starten/Stoppen mit Regeneration). Ressource: Gerätename(n) aus NI MAX, z. B. „Dev1“ oder „cDAQ1Mod1,cDAQ1Mod2“.
/// Optionen: „Anschluss“ (RSE/NRSE/DIFF/Standard), „Bereich“ (±V), „Starttrigger“, „Kurvenkanäle“ (L1=Dev1/ao0;…),
/// „Sichere Ausgänge“ (Dev1/port0/line0=0;…).
/// </summary>
public sealed class DaqmxDevice : IInstrumentDriver, IWaveformAdapter, IWaveformOutputAdapter
{
    public const string AdapterName = "NI-DAQmx";
    public const string OutputChannel = "AO";
    public static readonly string[] Models = ["USB-6001", "USB-6002", "USB-6003", "USB-6211", "USB-6212", "USB-6343", "USB-6363", "PCIe-6321", "cDAQ-9174 + NI 9215/9225/9227/9263/9401"];

    private readonly IDaqmxApi _api;
    private readonly InstrumentOptions _options;
    private readonly object _gate = new();
    private readonly List<string> _log = [];
    private readonly IReadOnlyList<string> _devices;
    private IReadOnlyList<DaqDeviceInfo>? _info;
    private OutputState _state = OutputState.Idle;
    private OutputCurve? _loaded;
    private OutputCurve? _inDevice;
    private nint _aoTask;
    private long _captures;

    public DaqmxDevice(string resource, IDaqmxApi? api = null, InstrumentOptions? options = null, string model = "")
    {
        _api = api ?? NativeDaqmxApi.Instance;
        _options = options ?? new InstrumentOptions();
        Resource = resource;
        _devices = resource.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Model = model;
    }

    public string Adapter => AdapterName;
    public string Model { get; private set; }
    public string Resource { get; }
    public string Name => $"NI-DAQmx {Resource}";
    public IReadOnlyList<string> Log
    {
        get { lock (_log) return _log.ToList(); }
    }

    private void Note(string text)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss.fff} {text}");
            if (_log.Count > 300) _log.RemoveAt(0);
        }
    }

    private IReadOnlyList<DaqDeviceInfo> Info()
    {
        if (_info is not null) return _info;
        _info = _devices.Select(_api.DeviceInfo).ToList();
        if (Model.Length == 0) Model = string.Join(" + ", _info.Select(i => i.ProductType));
        return _info;
    }

    /// <summary>Messbereich eines Eingangs: Option „Bereich“ (±V), höchstens der Gerätebereich.</summary>
    private double AiRange(InstrumentChannel c) => Math.Min(_options.Number("Bereich") ?? c.Max, c.Max);
    private double AoRange => _options.Number("AO-Bereich") ?? Info().Select(i => i.AoMax).DefaultIfEmpty(10).Max();

    private AiTerminal Terminal => (_options.Text("Anschluss") ?? "").ToUpperInvariant() switch
    {
        "RSE" => AiTerminal.Rse,
        "NRSE" => AiTerminal.Nrse,
        "DIFF" => AiTerminal.Diff,
        "PSEUDODIFF" => AiTerminal.PseudoDiff,
        _ => AiTerminal.Default,
    };

    public IReadOnlyList<InstrumentChannel> Channels
    {
        get
        {
            var list = new List<InstrumentChannel>();
            foreach (var d in Info())
            {
                list.AddRange(d.AiChannels.Select(c => new InstrumentChannel(c, "V", -d.AiMax, d.AiMax, ChannelAccess.Read | ChannelAccess.Waveform, $"{d.ProductType} Analogeingang")));
                list.AddRange(d.AoChannels.Select(c => new InstrumentChannel(c, "V", -d.AoMax, d.AoMax, ChannelAccess.Write, $"{d.ProductType} Analogausgang")));
                list.AddRange(d.DoLines.Select(c => new InstrumentChannel(c, "bool", 0, 1, ChannelAccess.Write, $"{d.ProductType} Digitalausgang")));
                list.AddRange(d.DiLines.Select(c => new InstrumentChannel(c, "bool", 0, 1, ChannelAccess.Read, $"{d.ProductType} Digitaleingang")));
            }
            if (list.Any(c => c.Access == ChannelAccess.Write && c.Unit == "V"))
                list.Add(new(OutputChannel, "V", -AoRange, AoRange, ChannelAccess.WaveformOutput, "Kurvenausgabe auf Analogausgänge"));
            return list;
        }
    }

    private InstrumentChannel Channel(string name)
        => Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
           ?? throw new InstrumentException($"NI-DAQmx {Resource}: Kanal „{name}“ gibt es nicht.");

    public ChannelCapabilities GetCapabilities(string channel)
    {
        var c = Channel(channel);
        var simultaneous = Info().Any(i => i.SimultaneousSampling);
        return new ChannelCapabilities(c.Unit, c.Min, c.Max, c.Access.HasFlag(ChannelAccess.Waveform), simultaneous);
    }

    /// <summary>Kurzlebiger Task: anlegen, benutzen, immer freigeben (auch bei Fehler/Abbruch).</summary>
    private T WithTask<T>(string what, Func<nint, T> body)
    {
        var task = _api.CreateTask("");
        try
        {
            return body(task);
        }
        finally
        {
            try { _api.ClearTask(task); } catch (InstrumentException) { /* Task bereits ungültig */ }
            Note(what);
        }
    }

    public Task<double> ReadAsync(string channel, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var c = Channel(channel);
        cancellationToken.ThrowIfCancellationRequested();
        if (c.Unit == "bool")
            return WithTask($"DI lesen {c.Name}", t =>
            {
                _api.CreateDIChan(t, c.Name);
                return _api.ReadDigitalLines(t, 1, 5)[0] != 0 ? 1.0 : 0.0;
            });
        if (!c.Access.HasFlag(ChannelAccess.Read)) throw new InstrumentException($"NI-DAQmx: Kanal {c.Name} ist nicht lesbar.");
        return WithTask($"AI lesen {c.Name}", t =>
        {
            _api.CreateAIVoltageChan(t, c.Name, Terminal, -AiRange(c), AiRange(c));
            return _api.ReadAnalogScalar(t, 5);
        });
    }, cancellationToken);

    public Task WriteAsync(string channel, double value, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var c = Channel(channel);
        cancellationToken.ThrowIfCancellationRequested();
        if (!c.Access.HasFlag(ChannelAccess.Write) || c.Name == OutputChannel) throw new InstrumentException($"NI-DAQmx: Kanal {c.Name} ist nicht schreibbar.");
        if (value < c.Min || value > c.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} {c.Unit} außerhalb {c.Min} … {c.Max} von {c.Name}.");
        if (c.Unit == "bool")
            WithTask($"DO setzen {c.Name}={value}", t =>
            {
                _api.CreateDOChan(t, c.Name);
                _api.WriteDigitalLines(t, [value >= 0.5 ? (byte)1 : (byte)0], 5);
                return 0;
            });
        else
            WithTask($"AO setzen {c.Name}={value}", t =>
            {
                _api.CreateAOVoltageChan(t, c.Name, c.Min, c.Max);
                _api.WriteAnalogScalar(t, value, 5);
                return 0;
            });
    }, cancellationToken);

    /// <summary>
    /// Hardwaregetaktete Aufnahme: ein Task, ein Abtasttakt für alle Kanäle (gemeinsame Zeitbasis), endliche Samples.
    /// Gelesen wird in Blöcken (~0,2 s), damit ein Abbruch schnell wirkt; der Task wird immer freigegeben.
    /// </summary>
    public Task<WaveformSet> CaptureAsync(IReadOnlyList<string> channels, double sampleRate, int samples, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var chans = channels.Select(Channel).ToList();
        if (chans.Count == 0 || chans.Any(c => !c.Access.HasFlag(ChannelAccess.Waveform)))
            throw new InstrumentException("NI-DAQmx: Kurvenaufnahme nur auf Analogeingängen.");
        var involved = Info().Where(i => chans.Any(c => c.Name.StartsWith(i.Name + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        var maxRate = involved.Select(i => i.AiMaxRate).Where(r => r > 0).DefaultIfEmpty(double.MaxValue).Min();
        var simultaneous = involved.Count > 0 && involved.All(i => i.SimultaneousSampling);
        if (sampleRate * (simultaneous ? 1 : chans.Count) > maxRate)
            throw new InstrumentException(string.Create(CultureInfo.InvariantCulture, $"NI-DAQmx: {sampleRate:0.###} Hz × {chans.Count} Kanäle übersteigt die Gerätegrenze {maxRate:0.###} S/s."));
        var start = DateTimeOffset.Now;
        var data = chans.Select(_ => new SampleBufferWriter(samples)).ToList();
        try
        {
            WithTask($"Aufnahme {string.Join(",", chans.Select(c => c.Name))} {samples} @ {sampleRate} Hz", t =>
            {
                foreach (var c in chans) _api.CreateAIVoltageChan(t, c.Name, Terminal, -AiRange(c), AiRange(c));
                _api.CfgSampClkTiming(t, sampleRate, false, samples);
                var trigger = _options.Text("Starttrigger");
                if (trigger is not null) _api.CfgDigEdgeStartTrig(t, trigger, true);
                _api.StartTask(t);
                var block = (int)Math.Clamp(sampleRate * 0.2, 1, Math.Min(samples, SampleBuffer.BlockSize));
                var buffer = new double[block * chans.Count];
                var done = 0;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(samples / sampleRate + (trigger is null ? 10 : _options.Number("Triggerzeitlimit") ?? 30));
                while (done < samples)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _api.StopTask(t);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("NI-DAQmx: Aufnahme nicht rechtzeitig abgeschlossen (Trigger?).");
                    var want = Math.Min(block, samples - done);
                    int read;
                    try
                    {
                        read = _api.ReadAnalogInterleaved(t, want, 0.5, buffer);
                    }
                    catch (InstrumentException ex) when (ex.Message.Contains("(-200284)", StringComparison.Ordinal))
                    {
                        continue; // Zeitlimit eines Blocks (noch kein Trigger) – weiter warten bzw. Abbruch prüfen
                    }
                    for (var k = 0; k < chans.Count; k++)
                    {
                        var values = new double[read];
                        for (var i = 0; i < read; i++) values[i] = buffer[i * chans.Count + k];
                        data[k].Append(values);
                    }
                    done += read;
                }
                _api.StopTask(t);
                return 0;
            });
            var sync = $"daqmx-{Resource}-{Interlocked.Increment(ref _captures)}";
            var quality = simultaneous ? "NI-DAQmx, simultan abgetastet" : "NI-DAQmx, gemultiplext (Kanalversatz ≤ 1/Gesamtrate)";
            if (involved.Any(i => i.IsSimulated)) quality += ", simuliertes Gerät";
            return new WaveformSet(chans.Select((c, k) => new Waveform(c.Name, "V", sampleRate, start, data[k].Complete(), Quality: quality, SyncGroup: sync)).ToList());
        }
        finally { foreach (var writer in data) writer.Dispose(); }
    }, CancellationToken.None);

    // ------------------------------------------------------------------ Kurvenausgabe (AO)

    private Dictionary<string, string> Map()
    {
        var map = _options.Map("Kurvenkanäle");
        if (map.Count > 0) return map;
        var ao = Info().SelectMany(i => i.AoChannels).ToList();
        string[] ids = ["L1", "L2", "L3", "L4"];
        return ao.Take(ids.Length).Select((c, i) => (ids[i], c)).ToDictionary(x => x.Item1, x => x.c, StringComparer.Ordinal);
    }

    public OutputCapabilities GetOutputCapabilities(string channel)
    {
        Check(channel);
        var maxRate = Info().Select(i => i.AoMaxRate).Where(r => r > 0).DefaultIfEmpty(1000).Min();
        return new OutputCapabilities($"NI-DAQmx {Resource} ({Model})", "V", -AoRange, AoRange, Map().Keys.ToList(), [], false,
            (int)(_options.Number("Speicher") ?? 8_000_000), 1, maxRate);
    }

    private static void Check(string channel)
    {
        if (!string.Equals(channel, OutputChannel, StringComparison.OrdinalIgnoreCase))
            throw new InstrumentException($"NI-DAQmx: Kurvenausgabe über Kanal „{OutputChannel}“.");
    }

    public OutputState GetOutputState(string channel)
    {
        lock (_gate) return _state;
    }

    public string? LoadedCurve(string channel)
    {
        lock (_gate) return (_inDevice ?? _loaded)?.Key;
    }

    public Task LoadAsync(string channel, OutputCurve curve, CancellationToken cancellationToken)
    {
        Check(channel);
        lock (_gate)
        {
            if (_state == OutputState.Running) throw new OutputSequenceException("Ausgabe läuft – erst stoppen, dann neue Kurve laden.");
            var errors = OutputSuitability.Check(GetOutputCapabilities(channel), curve).Where(i => i.IsError).ToList();
            if (errors.Count > 0) throw new OutputSequenceException("Gerät ungeeignet: " + string.Join(" ", errors.Select(e => e.Text)));
            _loaded = curve;
            _state = OutputState.Loaded;
        }
        return Task.CompletedTask;
    }

    /// <summary>AO-Task anlegen, Takt (kontinuierlich, Regeneration), Daten in den Puffer – noch ohne Start.</summary>
    public Task TransferAsync(string channel, CancellationToken cancellationToken) => Task.Run(() =>
    {
        Check(channel);
        OutputCurve curve;
        lock (_gate)
        {
            if (_state != OutputState.Loaded || _loaded is null) throw new OutputSequenceException("Übertragen erst nach dem Laden einer Kurve.");
            curve = _loaded;
        }
        ReleaseAoTask();
        var map = Map();
        var task = _api.CreateTask("");
        try
        {
            foreach (var ch in curve.Channels) _api.CreateAOVoltageChan(task, map[ch.Id], -AoRange, AoRange);
            _api.CfgSampClkTiming(task, curve.SampleRate, true, curve.Count);
            _api.SetWriteRegenMode(task, true);
            var data = curve.Channels.SelectMany(c => c.Samples).ToArray();
            var written = _api.WriteAnalogByChannel(task, curve.Count, false, 10, data);
            if (written != curve.Count) throw new InstrumentException($"NI-DAQmx: nur {written} von {curve.Count} Samples übertragen.");
        }
        catch
        {
            _api.ClearTask(task);
            throw;
        }
        Note($"AO übertragen {curve.Key}: {curve.Count} Samples @ {curve.SampleRate} Hz auf {string.Join(",", curve.Channels.Select(c => map[c.Id]))}");
        lock (_gate)
        {
            _aoTask = task;
            _inDevice = curve;
            _state = OutputState.Transferred;
        }
    }, CancellationToken.None);

    public Task StartAsync(string channel, CancellationToken cancellationToken) => Task.Run(() =>
    {
        Check(channel);
        nint task;
        lock (_gate)
        {
            if (_state is not (OutputState.Transferred or OutputState.Stopped) || _aoTask == 0)
                throw new OutputSequenceException("Starten erst nach dem Übertragen einer Kurve.");
            task = _aoTask;
        }
        _api.StartTask(task);
        Note("AO gestartet");
        lock (_gate) _state = OutputState.Running;
    }, CancellationToken.None);

    public Task StopAsync(string channel, CancellationToken cancellationToken) => Task.Run(() =>
    {
        Check(channel);
        nint task;
        lock (_gate)
        {
            if (_state != OutputState.Running) throw new OutputSequenceException("Ausgabe läuft nicht.");
            task = _aoTask;
        }
        _api.StopTask(task);
        Note("AO gestoppt");
        lock (_gate) _state = OutputState.Stopped;
    }, CancellationToken.None);

    private void ReleaseAoTask()
    {
        nint task;
        lock (_gate)
        {
            task = _aoTask;
            _aoTask = 0;
        }
        if (task == 0) return;
        try { _api.StopTask(task); } catch (InstrumentException) { }
        try { _api.ClearTask(task); } catch (InstrumentException) { }
    }

    /// <summary>Kurvenausgabe beenden, alle Analogausgänge 0 V, konfigurierte Digitalausgänge auf sicheren Wert.</summary>
    public Task SafeStateAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        ReleaseAoTask();
        lock (_gate)
        {
            if (_state == OutputState.Running) _state = OutputState.Stopped;
            if (_state == OutputState.Transferred) _state = OutputState.Loaded;
        }
        var ao = Info().SelectMany(i => i.AoChannels).ToList();
        foreach (var c in ao)
            WithTask($"Sicherer Zustand {c}=0 V", t =>
            {
                _api.CreateAOVoltageChan(t, c, -AoRange, AoRange);
                _api.WriteAnalogScalar(t, 0, 2);
                return 0;
            });
        foreach (var (line, value) in _options.Map("Sichere Ausgänge"))
            WithTask($"Sicherer Zustand {line}={value}", t =>
            {
                _api.CreateDOChan(t, line);
                _api.WriteDigitalLines(t, [value == "1" ? (byte)1 : (byte)0], 2);
                return 0;
            });
    }, CancellationToken.None);

    public Task<InstrumentProbe> ProbeAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            if (!_api.IsAvailable) return new InstrumentProbe(false, "NI-DAQmx nicht installiert", ["nicaiu.dll fehlt – NI-DAQmx installieren (ohne Hardware: simuliertes Gerät in NI MAX)."], Log);
            _info = null;
            var known = _api.DeviceNames();
            var missing = _devices.Where(d => !known.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                return new InstrumentProbe(false, "Gerät nicht gefunden", [$"Nicht vorhanden: {string.Join(", ", missing)}. Gefunden: {string.Join(", ", known)}."], Log);
            var details = Info().Select(i => string.Create(CultureInfo.InvariantCulture,
                $"{i.Name}: {i.ProductType}{(i.IsSimulated ? " (simuliert)" : "")}, AI {i.AiChannels.Count} (±{i.AiMax} V, {i.AiMaxRate:0} S/s{(i.SimultaneousSampling ? ", simultan" : "")}), AO {i.AoChannels.Count} (±{i.AoMax} V, {i.AoMaxRate:0} S/s), DO {i.DoLines.Count}, DI {i.DiLines.Count}")).ToList();
            return new InstrumentProbe(true, Model, details, Log);
        }
        catch (InstrumentException ex)
        {
            return new InstrumentProbe(false, "Fehler", [ex.Message], Log);
        }
    }, cancellationToken);

    public void Dispose() => ReleaseAoTask();
}
