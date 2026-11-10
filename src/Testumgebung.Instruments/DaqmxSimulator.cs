/**
 * @file
 * @brief Stellt DAQ-Verhalten über eine ersetzbare API bereit, ohne den realen NI-Treiber vorauszusetzen.
 */

using System.Diagnostics;

namespace Testumgebung.Instruments;

/// <summary>
/// DAQmx-Nachbildung ohne NI-Treiber (Tests und Vorführung): „Dev1“ wie USB-6211 (16 AI ±10 V gemultiplext 250 kS/s,
/// 2 AO ±10 V 250 kS/s, 4 DO, 4 DI) und optional „cDAQ1Mod1“ wie NI 9225 (3 AI ±425 V simultan 50 kS/s).
/// Verdrahtung: ai0/ai1 ← ao0/ao1 (auch laufende Kurvenausgabe), DI ← DO, ai2…: 1 V Sinus 50 Hz, NI 9225: 230 V L1–L3.
/// Wie der echte Treiber: Tasks, Takt, endliche Aufnahme in Echtzeit (Fehler -200284 bei Zeitlimit), ungültige Tasks
/// (-200088), unbekannte Kanäle (-200170), zu hohe Rate (-200077).
/// </summary>
public sealed class SimulatedDaqmxApi : IDaqmxApi
{
    private sealed class SimTask
    {
        public List<(string Kind, string Channel, double Min, double Max)> Channels { get; } = [];
        public double Rate;
        public long Samples;
        public bool Continuous;
        public bool Started;
        public double StartTime;
        public long Read;
        public string? Trigger;
        public double[]? AoData;
    }

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<nint, SimTask> _tasks = [];
    private readonly double[] _aoStatic = new double[2];
    private readonly byte[] _do = new byte[4];
    private nint _next = 1;
    private readonly bool _withCdaq;

    public SimulatedDaqmxApi(bool withCdaq = true) => _withCdaq = withCdaq;

    /// <summary>Starttrigger auslösen (z. B. vom Generator); ohne Aufruf wartet eine getriggerte Aufnahme.</summary>
    public void FireTrigger(string source)
    {
        lock (_gate)
            foreach (var task in _tasks.Values.Where(task2 => task2.Started && task2.Trigger == source && task2.StartTime < 0))
                task.StartTime = Now;
    }

    /// <summary>Anzahl offener Tasks (Tests: Tasks werden immer freigegeben).</summary>
    public int OpenTasks
    {
        get { lock (_gate) return _tasks.Count; }
    }

    public List<string> Calls { get; } = [];

    private double Now => _clock.Elapsed.TotalSeconds;

    public bool IsAvailable => true;

    public IReadOnlyList<string> DeviceNames() => _withCdaq ? ["Dev1", "cDAQ1Mod1"] : ["Dev1"];

    public DaqDeviceInfo DeviceInfo(string device) => device switch
    {
        "Dev1" => new DaqDeviceInfo("Dev1", "USB-6211", true,
            Enumerable.Range(0, 16).Select(index => $"Dev1/ai{index}").ToList(), ["Dev1/ao0", "Dev1/ao1"],
            Enumerable.Range(0, 4).Select(index => $"Dev1/port1/line{index}").ToList(), Enumerable.Range(0, 4).Select(index => $"Dev1/port0/line{index}").ToList(),
            10, 10, 250000, 250000, false),
        "cDAQ1Mod1" when _withCdaq => new DaqDeviceInfo("cDAQ1Mod1", "NI 9225", true,
            ["cDAQ1Mod1/ai0", "cDAQ1Mod1/ai1", "cDAQ1Mod1/ai2"], [], [], [], 425, 0, 50000, 0, true),
        _ => throw Err(-200220, $"Device identifier is invalid: {device}"),
    };

    private static InstrumentException Err(int code, string text) => new($"NI-DAQmx simuliert ({code}): {text}");

    private SimTask Task(nint task)
    {
        lock (_gate)
            return _tasks.TryGetValue(task, out var task2) ? task2 : throw Err(-200088, "Task specified is invalid or does not exist.");
    }

    private void Note(string call)
    {
        lock (Calls) Calls.Add(call);
    }

    public nint CreateTask(string name)
    {
        lock (_gate)
        {
            var handle = _next++;
            _tasks[handle] = new SimTask();
            Note("CreateTask");
            return handle;
        }
    }

    public void ClearTask(nint task)
    {
        lock (_gate)
        {
            if (!_tasks.Remove(task)) throw Err(-200088, "Task specified is invalid or does not exist.");
        }
        Note("ClearTask");
    }

    public void StartTask(nint task)
    {
        var task2 = Task(task);
        lock (_gate)
        {
            task2.Started = true;
            task2.StartTime = task2.Trigger is null ? Now : -1;
            task2.Read = 0;
        }
        Note("StartTask");
    }

    public void StopTask(nint task)
    {
        var task2 = Task(task);
        lock (_gate)
        {
            if (task2.AoData is not null && task2.Started)
                for (var previousIndex = 0; previousIndex < task2.Channels.Count; previousIndex++) _aoStatic[AoIndex(task2.Channels[previousIndex].Channel)] = Ao(task2, previousIndex, Now); // letzter Wert bleibt stehen
            task2.Started = false;
        }
        Note("StopTask");
    }

    private void Known(string channel)
    {
        var all = DeviceNames().Select(DeviceInfo).SelectMany(device => device.AiChannels.Concat(device.AoChannels).Concat(device.DoLines).Concat(device.DiLines));
        if (!all.Contains(channel, StringComparer.OrdinalIgnoreCase)) throw Err(-200170, $"Physical channel specified does not exist on this device: {channel}");
    }

    private static string[] List(string channels) => channels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public void CreateAIVoltageChan(nint task, string physical, AiTerminal terminal, double min, double max)
    {
        var task2 = Task(task);
        foreach (var list in List(physical))
        {
            Known(list);
            var limit = list.StartsWith("cDAQ", StringComparison.OrdinalIgnoreCase) ? 425 : 10;
            if (max > limit || min < -limit) throw Err(-200077, $"Requested value is not a supported value for this property: Range ±{max} V");
            lock (_gate) task2.Channels.Add(("AI", list, min, max));
        }
        Note($"CreateAIVoltageChan {physical}");
    }

    public void CreateAOVoltageChan(nint task, string physical, double min, double max)
    {
        var task2 = Task(task);
        foreach (var list in List(physical))
        {
            Known(list);
            lock (_gate) task2.Channels.Add(("AO", list, min, max));
        }
        Note($"CreateAOVoltageChan {physical}");
    }

    public void CreateDOChan(nint task, string lines)
    {
        var task2 = Task(task);
        foreach (var list in List(lines))
        {
            Known(list);
            lock (_gate) task2.Channels.Add(("DO", list, 0, 1));
        }
    }

    public void CreateDIChan(nint task, string lines)
    {
        var task2 = Task(task);
        foreach (var list in List(lines))
        {
            Known(list);
            lock (_gate) task2.Channels.Add(("DI", list, 0, 1));
        }
    }

    public void CfgSampClkTiming(nint task, double rate, bool continuous, long samplesPerChannel)
    {
        var task2 = Task(task);
        var multiplexed = task2.Channels.Any(channelEntry => channelEntry.Channel.StartsWith("Dev1/ai", StringComparison.Ordinal));
        var max = task2.Channels.Any(channelEntry => channelEntry.Channel.StartsWith("cDAQ", StringComparison.Ordinal)) ? 50000 : 250000;
        var total = multiplexed ? rate * task2.Channels.Count : rate;
        if (total > max) throw Err(-200077, $"Requested sample rate {rate} exceeds the maximum ({max} S/s{(multiplexed ? " aggregate" : "")}).");
        lock (_gate)
        {
            task2.Rate = rate;
            task2.Continuous = continuous;
            task2.Samples = samplesPerChannel;
        }
        Note($"CfgSampClkTiming {rate} {(continuous ? "kontinuierlich" : "endlich")} {samplesPerChannel}");
    }

    public void CfgDigEdgeStartTrig(nint task, string source, bool rising)
    {
        var task2 = Task(task);
        lock (_gate) task2.Trigger = source;
        Note($"CfgDigEdgeStartTrig {source}");
    }

    public void SetWriteRegenMode(nint task, bool allow) => Task(task);

    private static int AoIndex(string channel) => channel.EndsWith('1') ? 1 : 0;

    private double Ao(SimTask task, int index2, double now)
    {
        if (task.AoData is null || task.Samples == 0) return 0;
        var index = (long)Math.Floor((now - task.StartTime) * task.Rate) % task.Samples;
        return task.AoData[index2 * task.Samples + Math.Max(0, index)];
    }

    /// <summary>Spannung eines Eingangs zum Zeitpunkt (Sekunden).</summary>
    private double Input(string channel, double time)
    {
        if (channel.StartsWith("cDAQ1Mod1/ai", StringComparison.Ordinal))
        {
            var index2 = channel[^1] - '0';
            return 230 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 50 * time - index2 * 2 * Math.PI / 3);
        }
        if (channel is "Dev1/ai0" or "Dev1/ai1")
        {
            var index = channel == "Dev1/ai0" ? 0 : 1;
            lock (_gate)
            {
                foreach (var task in _tasks.Values.Where(task2 => task2.Started && task2.AoData is not null))
                    for (var previousIndex = 0; previousIndex < task.Channels.Count; previousIndex++)
                        if (AoIndex(task.Channels[previousIndex].Channel) == index && task.Channels[previousIndex].Channel.StartsWith("Dev1/ao", StringComparison.Ordinal))
                            return Ao(task, previousIndex, time);
                return _aoStatic[index];
            }
        }
        return Math.Sin(2 * Math.PI * 50 * time);
    }

    public double ReadAnalogScalar(nint task, double timeout)
    {
        var task2 = Task(task);
        if (task2.Channels.Count != 1 || task2.Channels[0].Kind != "AI") throw Err(-200523, "Read scalar requires exactly one analog input channel.");
        return Input(task2.Channels[0].Channel, Now);
    }

    public int ReadAnalogInterleaved(nint task, int samplesPerChannel, double timeout, double[] buffer)
    {
        var task2 = Task(task);
        if (!task2.Started) throw Err(-200473, "Read cannot be performed when the task is not started.");
        var deadline = Now + timeout;
        while (true)
        {
            double start;
            long read, total;
            lock (_gate)
            {
                start = task2.StartTime;
                read = task2.Read;
                total = task2.Samples;
            }
            if (start >= 0)
            {
                var available = Math.Min(total, (long)Math.Floor((Now - start) * task2.Rate)) - read;
                var want = Math.Min(samplesPerChannel, total - read);
                if (available >= want)
                {
                    for (var index = 0; index < want; index++)
                        for (var previousIndex = 0; previousIndex < task2.Channels.Count; previousIndex++)
                            buffer[index * task2.Channels.Count + previousIndex] = Input(task2.Channels[previousIndex].Channel, start + (read + index) / task2.Rate);
                    lock (_gate) task2.Read += want;
                    return (int)want;
                }
            }
            if (Now > deadline) throw Err(-200284, "Some or all of the samples requested have not yet been acquired.");
            Thread.Sleep(2);
        }
    }

    public void WriteAnalogScalar(nint task, double value, double timeout)
    {
        var task2 = Task(task);
        var ch = task2.Channels.Single();
        if (value < ch.Min || value > ch.Max) throw Err(-200448, $"Attempted to write a sample beyond the final range of the device: {value}");
        lock (_gate) _aoStatic[AoIndex(ch.Channel)] = value;
        Note($"WriteAnalogScalar {ch.Channel}={value}");
    }

    public int WriteAnalogByChannel(nint task, int samplesPerChannel, bool autoStart, double timeout, double[] data)
    {
        var task2 = Task(task);
        if (data.Length != samplesPerChannel * task2.Channels.Count) throw Err(-200524, "Write buffer size mismatch.");
        for (var previousIndex = 0; previousIndex < task2.Channels.Count; previousIndex++)
            for (var index = 0; index < samplesPerChannel; index++)
                if (data[previousIndex * samplesPerChannel + index] < task2.Channels[previousIndex].Min || data[previousIndex * samplesPerChannel + index] > task2.Channels[previousIndex].Max)
                    throw Err(-200448, "Attempted to write a sample beyond the final range of the device.");
        lock (_gate)
        {
            task2.AoData = data;
            task2.Samples = samplesPerChannel;
        }
        Note($"WriteAnalogF64 {samplesPerChannel}×{task2.Channels.Count}");
        if (autoStart) StartTask(task);
        return samplesPerChannel;
    }

    public void WriteDigitalLines(nint task, byte[] states, double timeout)
    {
        var task2 = Task(task);
        lock (_gate)
            for (var previousIndex = 0; previousIndex < task2.Channels.Count; previousIndex++) _do[task2.Channels[previousIndex].Channel[^1] - '0'] = states[previousIndex];
        Note($"WriteDigitalLines {string.Join(",", task2.Channels.Select(channelEntry => channelEntry.Channel))}={string.Join("", states)}");
    }

    public byte[] ReadDigitalLines(nint task, int lines, double timeout)
    {
        var task2 = Task(task);
        lock (_gate) return task2.Channels.Select(channelEntry => _do[channelEntry.Channel[^1] - '0']).ToArray(); // DI ← DO (Verdrahtung)
    }

    /// <summary>Aktueller statischer Wert eines Analogausgangs (Tests: sicherer Zustand).</summary>
    public double AoValue(int index)
    {
        lock (_gate) return _aoStatic[index];
    }

    public byte DoValue(int line)
    {
        lock (_gate) return _do[line];
    }
}
