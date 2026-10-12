using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

internal class DaqNativeApiStub : NativeDaqmxApi.INativeApi
{
    public string? Fail;
    public string? MissingLibrary;
    public string? LastCall;
    public Dictionary<string, object?> Arguments = new();
    public Dictionary<string, object> Outputs = new();
    public Dictionary<string, Array> Buffers = new();
    private T Value<T>(string key) => Outputs.TryGetValue(key, out var value) ? (T)value : default!;
    protected int Status(string name)
    {
        if (name == MissingLibrary) throw new DllNotFoundException("fixture missing library");
        if (Buffers.TryGetValue(name, out var source))
            foreach (var destination in Arguments.Values.OfType<Array>())
                Array.Copy(source, destination, Math.Min(source.Length, destination.Length));
        return name == Fail ? -1 : 0;
    }
    public int DAQmxCreateTask(string name, out nint task) { task = Value<nint>("DAQmxCreateTask.task"); LastCall = "DAQmxCreateTask"; Arguments = new() { ["name"] = name }; return Status("DAQmxCreateTask"); }
    public int DAQmxClearTask(nint task) { LastCall = "DAQmxClearTask"; Arguments = new() { ["task"] = task }; return Status("DAQmxClearTask"); }
    public int DAQmxStartTask(nint task) { LastCall = "DAQmxStartTask"; Arguments = new() { ["task"] = task }; return Status("DAQmxStartTask"); }
    public int DAQmxStopTask(nint task) { LastCall = "DAQmxStopTask"; Arguments = new() { ["task"] = task }; return Status("DAQmxStopTask"); }
    public int DAQmxCreateAIVoltageChan(nint task, string physical, string name, int terminal, double min, double max, int units, string? scale) { LastCall = "DAQmxCreateAIVoltageChan"; Arguments = new() { ["task"] = task, ["physical"] = physical, ["name"] = name, ["terminal"] = terminal, ["min"] = min, ["max"] = max, ["units"] = units, ["scale"] = scale }; return Status("DAQmxCreateAIVoltageChan"); }
    public int DAQmxCreateAOVoltageChan(nint task, string physical, string name, double min, double max, int units, string? scale) { LastCall = "DAQmxCreateAOVoltageChan"; Arguments = new() { ["task"] = task, ["physical"] = physical, ["name"] = name, ["min"] = min, ["max"] = max, ["units"] = units, ["scale"] = scale }; return Status("DAQmxCreateAOVoltageChan"); }
    public int DAQmxCreateDOChan(nint task, string lines, string name, int grouping) { LastCall = "DAQmxCreateDOChan"; Arguments = new() { ["task"] = task, ["lines"] = lines, ["name"] = name, ["grouping"] = grouping }; return Status("DAQmxCreateDOChan"); }
    public int DAQmxCreateDIChan(nint task, string lines, string name, int grouping) { LastCall = "DAQmxCreateDIChan"; Arguments = new() { ["task"] = task, ["lines"] = lines, ["name"] = name, ["grouping"] = grouping }; return Status("DAQmxCreateDIChan"); }
    public int DAQmxCfgSampClkTiming(nint task, string source, double rate, int edge, int mode, ulong samples) { LastCall = "DAQmxCfgSampClkTiming"; Arguments = new() { ["task"] = task, ["source"] = source, ["rate"] = rate, ["edge"] = edge, ["mode"] = mode, ["samples"] = samples }; return Status("DAQmxCfgSampClkTiming"); }
    public int DAQmxCfgDigEdgeStartTrig(nint task, string source, int edge) { LastCall = "DAQmxCfgDigEdgeStartTrig"; Arguments = new() { ["task"] = task, ["source"] = source, ["edge"] = edge }; return Status("DAQmxCfgDigEdgeStartTrig"); }
    public int DAQmxSetWriteRegenMode(nint task, int mode) { LastCall = "DAQmxSetWriteRegenMode"; Arguments = new() { ["task"] = task, ["mode"] = mode }; return Status("DAQmxSetWriteRegenMode"); }
    public int DAQmxReadAnalogScalarF64(nint task, double timeout, out double value, nint reserved) { value = Value<double>("DAQmxReadAnalogScalarF64.value"); LastCall = "DAQmxReadAnalogScalarF64"; Arguments = new() { ["task"] = task, ["timeout"] = timeout, ["reserved"] = reserved }; return Status("DAQmxReadAnalogScalarF64"); }
    public int DAQmxReadAnalogF64(nint task, int samples, double timeout, uint fill, double[] data, uint size, out int read, nint reserved) { read = Value<int>("DAQmxReadAnalogF64.read"); LastCall = "DAQmxReadAnalogF64"; Arguments = new() { ["task"] = task, ["samples"] = samples, ["timeout"] = timeout, ["fill"] = fill, ["data"] = data, ["size"] = size, ["reserved"] = reserved }; return Status("DAQmxReadAnalogF64"); }
    public int DAQmxWriteAnalogScalarF64(nint task, uint autoStart, double timeout, double value, nint reserved) { LastCall = "DAQmxWriteAnalogScalarF64"; Arguments = new() { ["task"] = task, ["autoStart"] = autoStart, ["timeout"] = timeout, ["value"] = value, ["reserved"] = reserved }; return Status("DAQmxWriteAnalogScalarF64"); }
    public int DAQmxWriteAnalogF64(nint task, int samples, uint autoStart, double timeout, uint layout, double[] data, out int written, nint reserved) { written = Value<int>("DAQmxWriteAnalogF64.written"); LastCall = "DAQmxWriteAnalogF64"; Arguments = new() { ["task"] = task, ["samples"] = samples, ["autoStart"] = autoStart, ["timeout"] = timeout, ["layout"] = layout, ["data"] = data, ["reserved"] = reserved }; return Status("DAQmxWriteAnalogF64"); }
    public int DAQmxWriteDigitalLines(nint task, int samples, uint autoStart, double timeout, uint layout, byte[] data, out int written, nint reserved) { written = Value<int>("DAQmxWriteDigitalLines.written"); LastCall = "DAQmxWriteDigitalLines"; Arguments = new() { ["task"] = task, ["samples"] = samples, ["autoStart"] = autoStart, ["timeout"] = timeout, ["layout"] = layout, ["data"] = data, ["reserved"] = reserved }; return Status("DAQmxWriteDigitalLines"); }
    public int DAQmxReadDigitalLines(nint task, int samples, double timeout, uint fill, byte[] data, uint size, out int read, out int bytesPerSamp, nint reserved) { read = Value<int>("DAQmxReadDigitalLines.read"); bytesPerSamp = Value<int>("DAQmxReadDigitalLines.bytesPerSamp"); LastCall = "DAQmxReadDigitalLines"; Arguments = new() { ["task"] = task, ["samples"] = samples, ["timeout"] = timeout, ["fill"] = fill, ["data"] = data, ["size"] = size, ["reserved"] = reserved }; return Status("DAQmxReadDigitalLines"); }
    public int DAQmxGetExtendedErrorInfo(byte[] text, uint size) { LastCall = "DAQmxGetExtendedErrorInfo"; Arguments = new() { ["text"] = text, ["size"] = size }; return Status("DAQmxGetExtendedErrorInfo"); }
    public int DAQmxGetSysDevNames(byte[] data, uint size) { LastCall = "DAQmxGetSysDevNames"; Arguments = new() { ["data"] = data, ["size"] = size }; return Status("DAQmxGetSysDevNames"); }
    public int DAQmxGetDevProductType(string device, byte[] data, uint size) { LastCall = "DAQmxGetDevProductType"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevProductType"); }
    public int DAQmxGetDevIsSimulated(string device, out uint value) { value = Value<uint>("DAQmxGetDevIsSimulated.value"); LastCall = "DAQmxGetDevIsSimulated"; Arguments = new() { ["device"] = device }; return Status("DAQmxGetDevIsSimulated"); }
    public int DAQmxGetDevAIPhysicalChans(string device, byte[] data, uint size) { LastCall = "DAQmxGetDevAIPhysicalChans"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevAIPhysicalChans"); }
    public int DAQmxGetDevAOPhysicalChans(string device, byte[] data, uint size) { LastCall = "DAQmxGetDevAOPhysicalChans"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevAOPhysicalChans"); }
    public int DAQmxGetDevDOLines(string device, byte[] data, uint size) { LastCall = "DAQmxGetDevDOLines"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevDOLines"); }
    public int DAQmxGetDevDILines(string device, byte[] data, uint size) { LastCall = "DAQmxGetDevDILines"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevDILines"); }
    public int DAQmxGetDevAIVoltageRngs(string device, double[] data, uint size) { LastCall = "DAQmxGetDevAIVoltageRngs"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevAIVoltageRngs"); }
    public int DAQmxGetDevAOVoltageRngs(string device, double[] data, uint size) { LastCall = "DAQmxGetDevAOVoltageRngs"; Arguments = new() { ["device"] = device, ["data"] = data, ["size"] = size }; return Status("DAQmxGetDevAOVoltageRngs"); }
    public int DAQmxGetDevAIMaxMultiChanRate(string device, out double value) { value = Value<double>("DAQmxGetDevAIMaxMultiChanRate.value"); LastCall = "DAQmxGetDevAIMaxMultiChanRate"; Arguments = new() { ["device"] = device }; return Status("DAQmxGetDevAIMaxMultiChanRate"); }
    public int DAQmxGetDevAOMaxRate(string device, out double value) { value = Value<double>("DAQmxGetDevAOMaxRate.value"); LastCall = "DAQmxGetDevAOMaxRate"; Arguments = new() { ["device"] = device }; return Status("DAQmxGetDevAOMaxRate"); }
    public int DAQmxGetDevAISimultaneousSamplingSupported(string device, out uint value) { value = Value<uint>("DAQmxGetDevAISimultaneousSamplingSupported.value"); LastCall = "DAQmxGetDevAISimultaneousSamplingSupported"; Arguments = new() { ["device"] = device }; return Status("DAQmxGetDevAISimultaneousSamplingSupported"); }
}
