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
            foreach (var t in _tasks.Values.Where(t => t.Started && t.Trigger == source && t.StartTime < 0))
                t.StartTime = Now;
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
            Enumerable.Range(0, 16).Select(i => $"Dev1/ai{i}").ToList(), ["Dev1/ao0", "Dev1/ao1"],
            Enumerable.Range(0, 4).Select(i => $"Dev1/port1/line{i}").ToList(), Enumerable.Range(0, 4).Select(i => $"Dev1/port0/line{i}").ToList(),
            10, 10, 250000, 250000, false),
        "cDAQ1Mod1" when _withCdaq => new DaqDeviceInfo("cDAQ1Mod1", "NI 9225", true,
            ["cDAQ1Mod1/ai0", "cDAQ1Mod1/ai1", "cDAQ1Mod1/ai2"], [], [], [], 425, 0, 50000, 0, true),
        _ => throw Err(-200220, $"Device identifier is invalid: {device}"),
    };

    private static InstrumentException Err(int code, string text) => new($"NI-DAQmx simuliert ({code}): {text}");

    private SimTask Task(nint task)
    {
        lock (_gate)
            return _tasks.TryGetValue(task, out var t) ? t : throw Err(-200088, "Task specified is invalid or does not exist.");
    }

    private void Note(string call)
    {
        lock (Calls) Calls.Add(call);
    }

    public nint CreateTask(string name)
    {
        lock (_gate)
        {
            var h = _next++;
            _tasks[h] = new SimTask();
            Note("CreateTask");
            return h;
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
        var t = Task(task);
        lock (_gate)
        {
            t.Started = true;
            t.StartTime = t.Trigger is null ? Now : -1;
            t.Read = 0;
        }
        Note("StartTask");
    }

    public void StopTask(nint task)
    {
        var t = Task(task);
        lock (_gate)
        {
            if (t.AoData is not null && t.Started)
                for (var k = 0; k < t.Channels.Count; k++) _aoStatic[AoIndex(t.Channels[k].Channel)] = Ao(t, k, Now); // letzter Wert bleibt stehen
            t.Started = false;
        }
        Note("StopTask");
    }

    private void Known(string channel)
    {
        var all = DeviceNames().Select(DeviceInfo).SelectMany(i => i.AiChannels.Concat(i.AoChannels).Concat(i.DoLines).Concat(i.DiLines));
        if (!all.Contains(channel, StringComparer.OrdinalIgnoreCase)) throw Err(-200170, $"Physical channel specified does not exist on this device: {channel}");
    }

    private static string[] List(string channels) => channels.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public void CreateAIVoltageChan(nint task, string physical, AiTerminal terminal, double min, double max)
    {
        var t = Task(task);
        foreach (var c in List(physical))
        {
            Known(c);
            var limit = c.StartsWith("cDAQ", StringComparison.OrdinalIgnoreCase) ? 425 : 10;
            if (max > limit || min < -limit) throw Err(-200077, $"Requested value is not a supported value for this property: Range ±{max} V");
            lock (_gate) t.Channels.Add(("AI", c, min, max));
        }
        Note($"CreateAIVoltageChan {physical}");
    }

    public void CreateAOVoltageChan(nint task, string physical, double min, double max)
    {
        var t = Task(task);
        foreach (var c in List(physical))
        {
            Known(c);
            lock (_gate) t.Channels.Add(("AO", c, min, max));
        }
        Note($"CreateAOVoltageChan {physical}");
    }

    public void CreateDOChan(nint task, string lines)
    {
        var t = Task(task);
        foreach (var c in List(lines))
        {
            Known(c);
            lock (_gate) t.Channels.Add(("DO", c, 0, 1));
        }
    }

    public void CreateDIChan(nint task, string lines)
    {
        var t = Task(task);
        foreach (var c in List(lines))
        {
            Known(c);
            lock (_gate) t.Channels.Add(("DI", c, 0, 1));
        }
    }

    public void CfgSampClkTiming(nint task, double rate, bool continuous, long samplesPerChannel)
    {
        var t = Task(task);
        var multiplexed = t.Channels.Any(c => c.Channel.StartsWith("Dev1/ai", StringComparison.Ordinal));
        var max = t.Channels.Any(c => c.Channel.StartsWith("cDAQ", StringComparison.Ordinal)) ? 50000 : 250000;
        var total = multiplexed ? rate * t.Channels.Count : rate;
        if (total > max) throw Err(-200077, $"Requested sample rate {rate} exceeds the maximum ({max} S/s{(multiplexed ? " aggregate" : "")}).");
        lock (_gate)
        {
            t.Rate = rate;
            t.Continuous = continuous;
            t.Samples = samplesPerChannel;
        }
        Note($"CfgSampClkTiming {rate} {(continuous ? "kontinuierlich" : "endlich")} {samplesPerChannel}");
    }

    public void CfgDigEdgeStartTrig(nint task, string source, bool rising)
    {
        var t = Task(task);
        lock (_gate) t.Trigger = source;
        Note($"CfgDigEdgeStartTrig {source}");
    }

    public void SetWriteRegenMode(nint task, bool allow) => Task(task);

    private static int AoIndex(string channel) => channel.EndsWith('1') ? 1 : 0;

    private double Ao(SimTask t, int k, double now)
    {
        if (t.AoData is null || t.Samples == 0) return 0;
        var i = (long)Math.Floor((now - t.StartTime) * t.Rate) % t.Samples;
        return t.AoData[k * t.Samples + Math.Max(0, i)];
    }

    /// <summary>Spannung eines Eingangs zum Zeitpunkt (Sekunden).</summary>
    private double Input(string channel, double time)
    {
        if (channel.StartsWith("cDAQ1Mod1/ai", StringComparison.Ordinal))
        {
            var k = channel[^1] - '0';
            return 230 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 50 * time - k * 2 * Math.PI / 3);
        }
        if (channel is "Dev1/ai0" or "Dev1/ai1")
        {
            var index = channel == "Dev1/ai0" ? 0 : 1;
            lock (_gate)
            {
                foreach (var t in _tasks.Values.Where(t => t.Started && t.AoData is not null))
                    for (var k = 0; k < t.Channels.Count; k++)
                        if (AoIndex(t.Channels[k].Channel) == index && t.Channels[k].Channel.StartsWith("Dev1/ao", StringComparison.Ordinal))
                            return Ao(t, k, time);
                return _aoStatic[index];
            }
        }
        return Math.Sin(2 * Math.PI * 50 * time);
    }

    public double ReadAnalogScalar(nint task, double timeout)
    {
        var t = Task(task);
        if (t.Channels.Count != 1 || t.Channels[0].Kind != "AI") throw Err(-200523, "Read scalar requires exactly one analog input channel.");
        return Input(t.Channels[0].Channel, Now);
    }

    public int ReadAnalogInterleaved(nint task, int samplesPerChannel, double timeout, double[] buffer)
    {
        var t = Task(task);
        if (!t.Started) throw Err(-200473, "Read cannot be performed when the task is not started.");
        var deadline = Now + timeout;
        while (true)
        {
            double start;
            long read, total;
            lock (_gate)
            {
                start = t.StartTime;
                read = t.Read;
                total = t.Samples;
            }
            if (start >= 0)
            {
                var available = Math.Min(total, (long)Math.Floor((Now - start) * t.Rate)) - read;
                var want = Math.Min(samplesPerChannel, total - read);
                if (available >= want)
                {
                    for (var i = 0; i < want; i++)
                        for (var k = 0; k < t.Channels.Count; k++)
                            buffer[i * t.Channels.Count + k] = Input(t.Channels[k].Channel, start + (read + i) / t.Rate);
                    lock (_gate) t.Read += want;
                    return (int)want;
                }
            }
            if (Now > deadline) throw Err(-200284, "Some or all of the samples requested have not yet been acquired.");
            Thread.Sleep(2);
        }
    }

    public void WriteAnalogScalar(nint task, double value, double timeout)
    {
        var t = Task(task);
        var ch = t.Channels.Single();
        if (value < ch.Min || value > ch.Max) throw Err(-200448, $"Attempted to write a sample beyond the final range of the device: {value}");
        lock (_gate) _aoStatic[AoIndex(ch.Channel)] = value;
        Note($"WriteAnalogScalar {ch.Channel}={value}");
    }

    public int WriteAnalogByChannel(nint task, int samplesPerChannel, bool autoStart, double timeout, double[] data)
    {
        var t = Task(task);
        if (data.Length != samplesPerChannel * t.Channels.Count) throw Err(-200524, "Write buffer size mismatch.");
        for (var k = 0; k < t.Channels.Count; k++)
            for (var i = 0; i < samplesPerChannel; i++)
                if (data[k * samplesPerChannel + i] < t.Channels[k].Min || data[k * samplesPerChannel + i] > t.Channels[k].Max)
                    throw Err(-200448, "Attempted to write a sample beyond the final range of the device.");
        lock (_gate)
        {
            t.AoData = data;
            t.Samples = samplesPerChannel;
        }
        Note($"WriteAnalogF64 {samplesPerChannel}×{t.Channels.Count}");
        if (autoStart) StartTask(task);
        return samplesPerChannel;
    }

    public void WriteDigitalLines(nint task, byte[] states, double timeout)
    {
        var t = Task(task);
        lock (_gate)
            for (var k = 0; k < t.Channels.Count; k++) _do[t.Channels[k].Channel[^1] - '0'] = states[k];
        Note($"WriteDigitalLines {string.Join(",", t.Channels.Select(c => c.Channel))}={string.Join("", states)}");
    }

    public byte[] ReadDigitalLines(nint task, int lines, double timeout)
    {
        var t = Task(task);
        lock (_gate) return t.Channels.Select(c => _do[c.Channel[^1] - '0']).ToArray(); // DI ← DO (Verdrahtung)
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
