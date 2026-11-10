/**
 * @file
 * @brief Interpretiert SCPI-Befehle auf einem gemeinsamen simulierten Geräteprüfstand.
 */

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Testumgebung.Instruments;

/// <summary>
/// Gemeinsamer „Laboraufbau“ der Gerätesimulatoren: AC-Quelle, Netzgerät und Generator erzeugen Signale, Multimeter und
/// Oszilloskop messen sie. Verdrahtung: DMM VAC/IAC/FREQ ← AC-Quelle (Last 54,8 Ω), DMM VDC/IDC ← Netzgerät CH1 (Last 12 Ω),
/// Oszilloskop CHAN1 ← AC-Quelle, CHAN2/CHAN3 ← Generator CH1/CH2, CHAN4 ← Netzgerät CH1.
/// </summary>
public sealed class SimulatedBench
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public const double AcLoadOhm = 54.8;
    public const double DcLoadOhm = 12;

    public double Now => _clock.Elapsed.TotalSeconds;

    // AC-Quelle
    public double AcVolt { get; set; } = 0;
    public double AcFreq { get; set; } = 50;
    public double AcCurrentLimit { get; set; } = 10;
    public double AcRange { get; set; } = 310;
    public bool AcOn { get; set; }

    // Netzgerät
    public double[] DcVolt { get; } = [0, 0, 0];
    public double[] DcCurrentLimit { get; } = [1, 0.1, 0.1];
    public bool[] DcOn { get; } = [false, false, false];

    // Generator
    public sealed class GeneratorChannel
    {
        public string Function = "SIN";
        public double Freq = 1000;
        public double Amplitude = 0.1;
        public double Offset;
        public bool On;
        public bool HighZ;
        public string? Arb;
        public double ArbRate = 40000;
        public Dictionary<string, float[]> ArbData { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double SyncTime;
    }

    public GeneratorChannel[] Generator { get; } = [new(), new()];

    public double AcVoltage(double t)
    {
        lock (_gate) return AcOn ? AcVolt * Math.Sqrt(2) * Math.Sin(2 * Math.PI * AcFreq * t) : 0;
    }

    public double AcRms
    {
        get { lock (_gate) return AcOn ? AcVolt : 0; }
    }

    public double AcCurrent
    {
        get { lock (_gate) return AcOn ? Math.Min(AcVolt / AcLoadOhm, AcCurrentLimit) : 0; }
    }

    public double DcOutput(int ch)
    {
        lock (_gate)
        {
            if (!DcOn[ch]) return 0;
            var value = DcVolt[ch];
            return Math.Abs(value / DcLoadOhm) > DcCurrentLimit[ch] ? Math.Sign(value) * DcCurrentLimit[ch] * DcLoadOhm : value; // Strombegrenzung
        }
    }

    public double DcCurrent(int ch) => DcOutput(ch) / DcLoadOhm;

    public double GeneratorVoltage(int ch, double t)
    {
        lock (_gate)
        {
            var generatorChannel = Generator[ch];
            if (!generatorChannel.On) return 0;
            return generatorChannel.Function switch
            {
                "DC" => generatorChannel.Offset,
                "SIN" => generatorChannel.Offset + generatorChannel.Amplitude / 2 * Math.Sin(2 * Math.PI * generatorChannel.Freq * t),
                "SQU" => generatorChannel.Offset + generatorChannel.Amplitude / 2 * Math.Sign(Math.Sin(2 * Math.PI * generatorChannel.Freq * t)),
                "ARB" when generatorChannel.Arb is { } name && generatorChannel.ArbData.TryGetValue(name, out var samples) && samples.Length > 0
                    => generatorChannel.Offset + generatorChannel.Amplitude / 2 * samples[(int)(Math.Max(0, t - generatorChannel.SyncTime) * generatorChannel.ArbRate) % samples.Length],
                _ => generatorChannel.Offset,
            };
        }
    }

    public void Sync(double t)
    {
        lock (_gate)
            foreach (var generatorChannel in Generator) generatorChannel.SyncTime = t;
    }

    public object Gate => _gate;
}

/// <summary>Ein simuliertes SCPI-Gerät: verarbeitet einzelne Befehle (ohne *IDN?/SYST:ERR? – das macht der Server).</summary>
public abstract class ScpiDeviceSimulator(string model, SimulatedBench bench)
{
    private readonly Queue<string> _errors = new();

    public string Model { get; } = model;
    protected SimulatedBench Bench { get; } = bench;
    public virtual string Identity => $"Keysight Technologies,{Model},SIM{Model.GetHashCode() & 0xFFFF:X4},A.01.00-SIM";

    /// <summary>Verzögerung je Antwort (Abbruch- und Zeitlimittests).</summary>
    public TimeSpan ResponseDelay { get; set; }

    /// <summary>Alle empfangenen Befehle (Tests: Reihenfolge, sicherer Zustand).</summary>
    public List<string> Received { get; } = [];

    protected void Error(int code, string text)
    {
        lock (_errors) _errors.Enqueue($"{code},\"{text}\"");
    }

    public string NextError()
    {
        lock (_errors) return _errors.Count > 0 ? _errors.Dequeue() : "+0,\"No error\"";
    }

    /// <summary>Befehl ausführen; Rückgabe = Antwort (Abfragen) oder null.</summary>
    public byte[]? Execute(string command, byte[]? block)
    {
        lock (Received) Received.Add(command);
        var code = command.Trim();
        var upper = code.ToUpperInvariant();
        switch (upper)
        {
            case "*IDN?": return Encoding.ASCII.GetBytes(Identity + "\n");
            case "*OPC?": return "1\n"u8.ToArray();
            case "SYST:ERR?" or "SYSTEM:ERROR?" or ":SYST:ERR?": return Encoding.ASCII.GetBytes(NextError() + "\n");
            case "*RST" or "*CLS": Reset(); return null;
        }
        try
        {
            var reply = Handle(code, upper, block);
            return EncodeReply(reply);
        }
        catch (FormatException)
        {
            Error(-104, "Data type error");
            return upper.Contains('?') ? "0\n"u8.ToArray() : null;
        }
    }

    private static byte[]? EncodeReply(object? reply)
    {
        return reply switch
        {
            null => null,
            byte[] bytes => bytes,
            string text => Encoding.ASCII.GetBytes(text + "\n"),
            double number => Encoding.ASCII.GetBytes(number.ToString("+0.000000000E+00;-0.000000000E+00", CultureInfo.InvariantCulture) + "\n"),
            _ => Encoding.ASCII.GetBytes(reply + "\n"),
        };
    }

    protected virtual void Reset()
    {
    }

    /// <summary>Gerätespezifische Befehle. Unbekannt → Fehler -113.</summary>
    protected abstract object? Handle(string command, string upper, byte[]? block);

    protected object? Unknown(string upper)
    {
        Error(-113, "Undefined header");
        return upper.Contains('?') ? "0" : null;
    }

    protected static double Num(string text)
    {
        var text2 = text.Trim().TrimEnd(',').Trim();
        return text2.ToUpperInvariant() switch
        {
            "ON" => 1,
            "OFF" => 0,
            "INF" => double.PositiveInfinity,
            _ => double.Parse(text2, NumberStyles.Float, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>„(@1)“, „(@1:3)“, „(@1,2)“ → Kanalindizes (0-basiert).</summary>
    protected static List<int> Channels(string text)
    {
        var start = text.IndexOf("(@", StringComparison.Ordinal);
        if (start < 0) return [];
        var inner = text[(start + 2)..text.IndexOf(')', start)];
        var list = new List<int>();
        foreach (var part in inner.Split(','))
        {
            var range = part.Split(':');
            var startChannel = int.Parse(range[0], CultureInfo.InvariantCulture);
            var bit = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : startChannel;
            for (var index = startChannel; index <= bit; index++) list.Add(index - 1);
        }
        return list;
    }

    protected bool InRange(double v, double min, double max)
    {
        if (v >= min && v <= max) return true;
        Error(-222, "Data out of range");
        return false;
    }

    /// <summary>Kleines, reproduzierbares Messrauschen.</summary>
    protected double Noise(double scale) => scale * (Random.Shared.NextDouble() * 2 - 1);
}

public sealed class DmmSimulator(string model, SimulatedBench bench) : ScpiDeviceSimulator(model, bench)
{
    private static readonly HashSet<string> MeasurementFunctions = new(StringComparer.Ordinal)
        { "VOLT:DC", "VOLT:AC", "CURR:DC", "CURR:AC", "RES", "FRES", "FREQ" };

    private string _function = "VOLT:DC";

    protected override void Reset() => _function = "VOLT:DC";

    private double Measure(string function) => function switch
    {
        "VOLT:DC" => Bench.DcOutput(0) + Noise(1e-5),
        "VOLT:AC" => Bench.AcRms + Noise(1e-3),
        "CURR:DC" => Bench.DcCurrent(0) + Noise(1e-6),
        "CURR:AC" => Bench.AcCurrent + Noise(1e-5),
        "RES" or "FRES" => 1000.0 + Noise(0.01),
        "FREQ" => Bench.AcRms > 1 ? Bench.AcFreq + Noise(1e-3) : 0,
        _ => 0,
    };

    private static string Func(string upper) => upper.Replace("VOLTAGE", "VOLT").Replace("CURRENT", "CURR").Replace("RESISTANCE", "RES")
        .Replace("FREQUENCY", "FREQ").Split(' ')[0];

    protected override object? Handle(string command, string upper, byte[]? block)
    {
        if (upper.StartsWith("CONF:", StringComparison.Ordinal) || upper.StartsWith("CONFIGURE:", StringComparison.Ordinal))
        {
            _function = Func(upper[(upper.IndexOf(':') + 1)..]);
            if (!MeasurementFunctions.Contains(_function)) return Unknown(upper);
            if (_function == "FRES" && Model == "34460A") return Unknown(upper);
            return null;
        }
        if (upper.Contains(":NPLC ", StringComparison.Ordinal)) return null;
        if (upper == "READ?") return Measure(_function);
        if (upper.StartsWith("MEAS:", StringComparison.Ordinal)) return Measure(Func(upper[5..].TrimEnd('?')));
        return Unknown(upper);
    }
}

public sealed class SupplySimulator(string model, SimulatedBench bench) : ScpiDeviceSimulator(model, bench)
{
    private readonly double[] _maxV = model == "E36313A" ? [6.18, 25.75, 25.75] : [6.18, 25.75, 25.75];
    private readonly double[] _maxI = model == "E36313A" ? [10.3, 2.06, 2.06] : [5.15, 1.03, 1.03];

    protected override void Reset()
    {
        lock (Bench.Gate)
            for (var index = 0; index < 3; index++)
            {
                Bench.DcOn[index] = false;
                Bench.DcVolt[index] = 0;
            }
    }

    private enum Command { Unknown, Volt, Curr, Outp, OutpQuery, MeasVoltQuery, MeasCurrQuery }

    private static readonly IReadOnlyDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.Ordinal)
    {
        ["VOLT"] = Command.Volt,
        ["VOLTAGE"] = Command.Volt,
        ["SOUR:VOLT"] = Command.Volt,
        ["CURR"] = Command.Curr,
        ["CURRENT"] = Command.Curr,
        ["SOUR:CURR"] = Command.Curr,
        ["OUTP"] = Command.Outp,
        ["OUTPUT"] = Command.Outp,
        ["OUTP?"] = Command.OutpQuery,
        ["OUTPUT?"] = Command.OutpQuery,
        ["MEAS:VOLT?"] = Command.MeasVoltQuery,
        ["MEAS:VOLT:DC?"] = Command.MeasVoltQuery,
        ["MEAS:CURR?"] = Command.MeasCurrQuery,
        ["MEAS:CURR:DC?"] = Command.MeasCurrQuery,
    };

    protected override object? Handle(string command, string upper, byte[]? block)
    {
        var chans = Channels(upper);
        if (chans.Count == 0) chans = [0];
        var head = upper.Split(' ')[0];
        switch (Commands.GetValueOrDefault(head, Command.Unknown))
        {
            case Command.Volt:
                {
                    var value = Num(upper.Split(' ', 2)[1].Split(",(@")[0]);
                    foreach (var chan in chans)
                        if (InRange(value, 0, _maxV[chan])) lock (Bench.Gate) Bench.DcVolt[chan] = value;
                    return null;
                }
            case Command.Curr:
                {
                    var current = Num(upper.Split(' ', 2)[1].Split(",(@")[0]);
                    foreach (var chan2 in chans)
                        if (InRange(current, 0, _maxI[chan2])) lock (Bench.Gate) Bench.DcCurrentLimit[chan2] = current;
                    return null;
                }
            case Command.Outp:
                {
                    var on = Num(upper.Split(' ', 2)[1].Split(",(@")[0]) >= 0.5;
                    lock (Bench.Gate)
                        foreach (var chan3 in chans) Bench.DcOn[chan3] = on;
                    return null;
                }
            case Command.OutpQuery:
                lock (Bench.Gate) return Bench.DcOn[chans[0]] ? "1" : "0";
            case Command.MeasVoltQuery:
                return Bench.DcOutput(chans[0]) + Noise(1e-4);
            case Command.MeasCurrQuery:
                return Bench.DcCurrent(chans[0]) + Noise(1e-5);
        }
        return Unknown(upper);
    }
}

public sealed class AcSourceSimulator(string model, SimulatedBench bench) : ScpiDeviceSimulator(model, bench)
{
    protected override void Reset()
    {
        lock (Bench.Gate)
        {
            Bench.AcOn = false;
            Bench.AcVolt = 0;
        }
    }

    private enum Command { Unknown, Volt, VoltRang, Freq, Curr, Outp, OutpQuery, FreqQuery, VoltQuery, MeasVoltAcQuery, MeasCurrAcQuery, MeasPowAcQuery }

    private static readonly IReadOnlyDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.Ordinal)
    {
        ["VOLT"] = Command.Volt,
        ["SOUR:VOLT"] = Command.Volt,
        ["VOLT:RANG"] = Command.VoltRang,
        ["FREQ"] = Command.Freq,
        ["SOUR:FREQ"] = Command.Freq,
        ["CURR"] = Command.Curr,
        ["OUTP"] = Command.Outp,
        ["OUTPUT"] = Command.Outp,
        ["OUTP?"] = Command.OutpQuery,
        ["FREQ?"] = Command.FreqQuery,
        ["VOLT?"] = Command.VoltQuery,
        ["MEAS:VOLT:AC?"] = Command.MeasVoltAcQuery,
        ["MEAS:VOLT?"] = Command.MeasVoltAcQuery,
        ["MEAS:CURR:AC?"] = Command.MeasCurrAcQuery,
        ["MEAS:CURR?"] = Command.MeasCurrAcQuery,
        ["MEAS:POW:AC?"] = Command.MeasPowAcQuery,
        ["MEAS:POW?"] = Command.MeasPowAcQuery,
    };

    protected override object? Handle(string command, string upper, byte[]? block)
    {
        var parts = upper.Split(' ', 2);
        switch (Commands.GetValueOrDefault(parts[0], Command.Unknown))
        {
            case Command.Volt:
                {
                    var value = Num(parts[1]);
                    if (InRange(value, 0, Bench.AcRange)) lock (Bench.Gate) Bench.AcVolt = value;
                    return null;
                }
            case Command.VoltRang:
                {
                    var ratio = Num(parts[1]);
                    if (Bench.AcOn) { Error(-221, "Settings conflict; output on"); return null; }
                    if (ratio is 155 or 310) lock (Bench.Gate) Bench.AcRange = ratio;
                    else Error(-222, "Data out of range");
                    return null;
                }
            case Command.Freq:
                {
                    var frequency = Num(parts[1]);
                    if (InRange(frequency, 40, 500)) lock (Bench.Gate) Bench.AcFreq = frequency;
                    return null;
                }
            case Command.Curr:
                {
                    var current = Num(parts[1]);
                    if (InRange(current, 0, 40)) lock (Bench.Gate) Bench.AcCurrentLimit = current;
                    return null;
                }
            case Command.Outp:
                lock (Bench.Gate) Bench.AcOn = Num(parts[1]) >= 0.5;
                return null;
            case Command.OutpQuery: return Bench.AcOn ? "1" : "0";
            case Command.FreqQuery: return Bench.AcFreq;
            case Command.VoltQuery: return Bench.AcVolt;
            case Command.MeasVoltAcQuery: return Bench.AcRms + Noise(0.01);
            case Command.MeasCurrAcQuery: return Bench.AcCurrent + Noise(1e-4);
            case Command.MeasPowAcQuery: return Bench.AcRms * Bench.AcCurrent + Noise(0.01);
        }
        return Unknown(upper);
    }
}

public sealed class GeneratorSimulator(string model, SimulatedBench bench) : ScpiDeviceSimulator(model, bench)
{
    private bool _swap;

    private static (int Ch, string Tail) Source(string upper)
    {
        if (upper.StartsWith("SOUR", StringComparison.Ordinal) && upper.Length > 4 && char.IsDigit(upper[4]))
            return (upper[4] - '1', upper[(upper.IndexOf(':') + 1)..]);
        if (upper.StartsWith("OUTP", StringComparison.Ordinal) && upper.Length > 4 && char.IsDigit(upper[4]))
            return (upper[4] - '1', "OUTP" + upper[5..]);
        return (0, upper);
    }

    private bool Check(SimulatedBench.GeneratorChannel generatorChannel, double amplitude, double offset)
    {
        var limit = generatorChannel.HighZ ? 10 : 5;
        if (Math.Abs(offset) + amplitude / 2 <= limit + 1e-9) return true;
        Error(-222, "Data out of range; amplitude + offset");
        return false;
    }

    private enum Command { Unknown, Freq, FreqQuery, Volt, VoltQuery, VoltOffs, VoltOffsQuery, Func, FuncQuery, FuncArb, FuncArbSrat, DataVolCle, DataArb, Outp, OutpQuery, OutpLoad }

    private static readonly IReadOnlyDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.Ordinal)
    {
        ["FREQ"] = Command.Freq,
        ["FREQ?"] = Command.FreqQuery,
        ["VOLT"] = Command.Volt,
        ["VOLT?"] = Command.VoltQuery,
        ["VOLT:OFFS"] = Command.VoltOffs,
        ["VOLT:OFFS?"] = Command.VoltOffsQuery,
        ["FUNC"] = Command.Func,
        ["FUNC?"] = Command.FuncQuery,
        ["FUNC:ARB"] = Command.FuncArb,
        ["FUNC:ARB:SRAT"] = Command.FuncArbSrat,
        ["DATA:VOL:CLE"] = Command.DataVolCle,
        ["DATA:ARB"] = Command.DataArb,
        ["OUTP"] = Command.Outp,
        ["OUTP?"] = Command.OutpQuery,
        ["OUTP:LOAD"] = Command.OutpLoad,
    };

    protected override object? Handle(string command, string upper, byte[]? block)
    {
        if (HandleFormattingAndSync(upper)) return null;
        var (ch, rest) = Source(upper);
        if (ch is < 0 or > 1) return Unknown(upper);
        var generatorChannel = Bench.Generator[ch];
        var parts = rest.Split(' ', 2);
        object? StoreArbitraryWaveform()
        {
            var name = parts[1].Split(',')[0].Trim();
            if (block is null) { Error(-104, "Data type error; block expected"); return null; }
            var values = new float[block.Length / 4];
            for (var index = 0; index < values.Length; index++)
                values[index] = _swap ? BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(index * 4)) : BinaryPrimitives.ReadSingleBigEndian(block.AsSpan(index * 4));
            if (values.Any(value => value is < -1 or > 1)) { Error(-222, "Data out of range; arb values"); return null; }
            generatorChannel.ArbData[name] = values;
            return null;
        }

        lock (Bench.Gate)
        {
            switch (Commands.GetValueOrDefault(parts[0], Command.Unknown))
            {
                case Command.Freq: generatorChannel.Freq = Num(parts[1]); return null;
                case Command.FreqQuery: return generatorChannel.Freq;
                case Command.Volt:
                    {
                        SetAmplitude(generatorChannel, parts[1]);
                        return null;
                    }
                case Command.VoltQuery: return generatorChannel.Amplitude;
                case Command.VoltOffs:
                    {
                        SetOffset(generatorChannel, parts[1]);
                        return null;
                    }
                case Command.VoltOffsQuery: return generatorChannel.Offset;
                case Command.Func: generatorChannel.Function = parts[1].Trim(); return null;
                case Command.FuncQuery: return generatorChannel.Function;
                case Command.FuncArb:
                    SelectArbitraryWaveform(generatorChannel, parts[1]);
                    return null;
                case Command.FuncArbSrat: generatorChannel.ArbRate = Num(parts[1]); return null;
                case Command.DataVolCle: generatorChannel.ArbData.Clear(); generatorChannel.Arb = null; return null;
                case Command.DataArb:
                    {
                        return StoreArbitraryWaveform();
                    }
                case Command.Outp:
                case Command.OutpQuery:
                case Command.OutpLoad:
                    return HandleOutput(Commands.GetValueOrDefault(parts[0], Command.Unknown), parts, generatorChannel);
            }
        }
        return Unknown(upper);
    }
    private bool HandleFormattingAndSync(string upper)
    {
        if (upper is "FORM:BORD SWAP" or "FORM:BORD NORM")
        {
            _swap = upper.EndsWith("SWAP", StringComparison.Ordinal);
            return true;
        }
        if (upper == "FUNC:ARB:SYNC")
        {
            Bench.Sync(Bench.Now);
            return true;
        }
        return false;
    }

    private void SelectArbitraryWaveform(SimulatedBench.GeneratorChannel generatorChannel, string text)
    {
        if (!generatorChannel.ArbData.ContainsKey(text.Trim())) { Error(-224, "Illegal parameter value; arb not loaded"); return; }
        generatorChannel.Arb = text.Trim();
    }

    private void SetAmplitude(SimulatedBench.GeneratorChannel generatorChannel, string text)
    {
        var current = Num(text);
        if (Check(generatorChannel, current, generatorChannel.Offset)) generatorChannel.Amplitude = current;
    }

    private void SetOffset(SimulatedBench.GeneratorChannel generatorChannel, string text)
    {
        var number = Num(text);
        if (Check(generatorChannel, generatorChannel.Function == "DC" ? 0 : generatorChannel.Amplitude, number)) generatorChannel.Offset = number;
    }

    private static object? HandleOutput(Command command, string[] parts, SimulatedBench.GeneratorChannel generatorChannel)
    {
        switch (command)
        {
            case Command.Outp: generatorChannel.On = Num(parts[1]) >= 0.5; return null;
            case Command.OutpQuery: return generatorChannel.On ? "1" : "0";
            case Command.OutpLoad: generatorChannel.HighZ = parts[1].Trim() == "INF"; return null;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

}

public sealed class ScopeSimulator(string model, SimulatedBench bench) : ScpiDeviceSimulator(model, bench)
{
    private double _timebase = 1e-3;
    private string _source = "CHAN1";
    private int _points = 1000;
    private readonly Dictionary<string, double[]> _captured = new(StringComparer.Ordinal);
    private double _capturedXinc = 1e-5;
    private readonly double[] _scale = [100, 1, 1, 1];
    private readonly double[] _offset = [0, 0, 0, 0];

    public override string Identity => $"KEYSIGHT TECHNOLOGIES,{(Model.StartsWith("DSOX", StringComparison.Ordinal) ? "DSO-X " + Model[4..] : Model)},SIM0001,07.50.SIM";

    private int MaxPoints => Model.StartsWith("DSOX1", StringComparison.Ordinal) ? 62500 : 4_000_000;

    private double Signal(int ch, double time) => ch switch
    {
        0 => Bench.AcVoltage(time),
        1 => Bench.GeneratorVoltage(0, time),
        2 => Bench.GeneratorVoltage(1, time),
        _ => Bench.DcOutput(0),
    };

    private static int Ch(string src) => src.StartsWith("CHAN", StringComparison.Ordinal) && src.Length >= 5 ? src[4] - '1' : -1;

    private enum Command { Unknown, TimRef, TimScal, TimScalQuery, WavSour, WavPoin, Dig, WavPreQuery, WavDataQuery, MeasVrmsQuery, MeasVppQuery, MeasVavQuery, MeasFreqQuery }

    private static readonly IReadOnlyDictionary<string, Command> Commands = new Dictionary<string, Command>(StringComparer.Ordinal)
    {
        ["TIM:REF"] = Command.TimRef,
        ["WAV:POIN:MODE"] = Command.TimRef,
        ["WAV:FORM"] = Command.TimRef,
        ["WAV:BYT"] = Command.TimRef,
        ["WAV:UNS"] = Command.TimRef,
        ["TRIG:SWE"] = Command.TimRef,
        ["TRIG:EDGE:SOUR"] = Command.TimRef,
        ["TRIG:EDGE:LEV"] = Command.TimRef,
        ["RUN"] = Command.TimRef,
        ["STOP"] = Command.TimRef,
        ["TIM:SCAL"] = Command.TimScal,
        ["TIM:SCAL?"] = Command.TimScalQuery,
        ["WAV:SOUR"] = Command.WavSour,
        ["WAV:POIN"] = Command.WavPoin,
        ["DIG"] = Command.Dig,
        ["WAV:PRE?"] = Command.WavPreQuery,
        ["WAV:DATA?"] = Command.WavDataQuery,
        ["MEAS:VRMS?"] = Command.MeasVrmsQuery,
        ["MEAS:VPP?"] = Command.MeasVppQuery,
        ["MEAS:VAV?"] = Command.MeasVavQuery,
        ["MEAS:FREQ?"] = Command.MeasFreqQuery,
    };

    protected override object? Handle(string command, string upper, byte[]? block)
    {
        var unit = upper.TrimStart(':');
        var parts = unit.Split(' ', 2);
        switch (Commands.GetValueOrDefault(parts[0], Command.Unknown))
        {
            case Command.TimRef:
                return null;
            case Command.TimScal: _timebase = Num(parts[1]); return null;
            case Command.TimScalQuery: return _timebase;
            case Command.WavSour: _source = parts[1].Trim(); return null;
            case Command.WavPoin: _points = (int)Num(parts[1]); return null;
            case Command.Dig:
                {
                    Capture(parts[1]);
                    return null;
                }
            case Command.WavPreQuery:
                {
                    var data = Data();
                    var yinc = Math.Max(1e-9, data.Select(Math.Abs).DefaultIfEmpty(1).Max() / 30000);
                    _yinc = yinc;
                    var count = Math.Min(_points, data.Length);
                    return string.Create(CultureInfo.InvariantCulture, $"+1,+0,+{count},+1,{_capturedXinc * data.Length / count:E6},0.0E+00,+0,{yinc:E6},0.0E+00,+0");
                }
            case Command.WavDataQuery:
                {
                    var data = Data();
                    var count = Math.Min(_points, data.Length);
                    var step = data.Length / (double)count;
                    var bytes = new byte[count * 2];
                    for (var index = 0; index < count; index++)
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), (short)Math.Clamp(Math.Round(data[(int)(index * step)] / _yinc), short.MinValue, short.MaxValue));
                    var len = bytes.Length.ToString(CultureInfo.InvariantCulture);
                    return (byte[])[.. Encoding.ASCII.GetBytes($"#{len.Length}{len}"), .. bytes, (byte)10];
                }
            case Command.MeasVrmsQuery:
                {
                    var ch = Ch(parts[1].Split(',')[^1]);
                    return Rms(ch);
                }
            case Command.MeasVppQuery:
                {
                    var ch = Ch(parts[1].Trim());
                    var samples = Sample(ch);
                    return samples.Max() - samples.Min();
                }
            case Command.MeasVavQuery:
                return Sample(Ch(parts[1].Split(',')[^1])).Average();
            case Command.MeasFreqQuery:
                return Ch(parts[1].Trim()) == 0 && Bench.AcRms > 0 ? Bench.AcFreq : Ch(parts[1].Trim()) is 1 or 2 ? Bench.Generator[Ch(parts[1].Trim()) - 1].Freq : 9.9e37;
        }
        return HandleChannel(unit, upper);
    }

    private object? HandleChannel(string command, string upper)
    {
        if (command.StartsWith("CHAN", StringComparison.Ordinal) && command.Length > 5)
        {
            var ch = command[4] - '1';
            var what = command[6..].Split(' ');
            switch (what[0])
            {
                case "SCAL": _scale[ch] = Num(what[1]); return null;
                case "SCAL?": return _scale[ch];
                case "OFFS": _offset[ch] = Num(what[1]); return null;
                case "OFFS?": return _offset[ch];
            }
        }
        return Unknown(upper);
    }

    private void Capture(string sourceList)
    {
        var sources = sourceList.Split(',', StringSplitOptions.TrimEntries);
        var span = _timebase * 10;
        var index = Math.Min(MaxPoints, Math.Max(_points, 62500));
        _capturedXinc = span / index;
        var t0 = Bench.Now;
        _captured.Clear();
        foreach (var source in sources)
        {
            var ch = Ch(source);
            if (ch < 0 || ch > 3) { Error(-224, "Illegal parameter value"); continue; }
            var data = new double[index];
            for (var index2 = 0; index2 < index; index2++) data[index2] = Signal(ch, t0 + index2 * _capturedXinc);
            _captured[source] = data;
        }
        if (span > 0.05) Thread.Sleep(TimeSpan.FromSeconds(Math.Min(span, 2))); // Erfassungsdauer
    }

    private double _yinc = 1;

    private double[] Data() => _captured.TryGetValue(_source, out var samples) ? samples : new double[Math.Max(1, _points)];

    private double[] Sample(int ch)
    {
        var count = 2000;
        var t0 = Bench.Now;
        var dt = _timebase * 10 / count;
        return Enumerable.Range(0, count).Select(index => Signal(ch, t0 + index * dt)).ToArray();
    }

    private double Rms(int ch)
    {
        var samples = Sample(ch);
        var mean = samples.Average();
        return Math.Sqrt(samples.Select(xValue => (xValue - mean) * (xValue - mean)).Average());
    }
}

/// <summary>
/// TCP-Server eines simulierten Geräts (wie „SOCKET“ auf Port 5025 echter Geräte): Zeilen mit LF, mehrere Befehle je Zeile
/// mit „;“, Binärblöcke (#…) in Befehlen. Mehrere Verbindungen werden nacheinander bedient.
/// </summary>
public sealed class ScpiSimulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public ScpiSimulatorServer(ScpiDeviceSimulator device, int port = 0)
    {
        Device = device;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
    }

    public ScpiDeviceSimulator Device { get; }
    public int Port { get; }
    public string Resource => $"TCPIP::127.0.0.1::{Port}::SOCKET";

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var pending = new List<byte>();
        var buffer = new byte[65536];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (count == 0) return;
                pending.AddRange(buffer.AsSpan(0, count).ToArray());
                while (TryTakeMessage(pending, out var text, out var block))
                {
                    foreach (var command in Split(text))
                    {
                        var reply = Device.Execute(command, block);
                        if (reply is null) continue;
                        if (Device.ResponseDelay > TimeSpan.Zero) await Task.Delay(Device.ResponseDelay, _cts.Token).ConfigureAwait(false);
                        await stream.WriteAsync(reply, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Nachricht bis LF; ein #-Block wird anhand seiner Länge übersprungen (darf LF enthalten).</summary>
    private static bool TryTakeMessage(List<byte> data, out string text, out byte[]? block)
    {
        text = "";
        block = null;
        var index = 0;
        while (index < data.Count)
        {
            if (data[index] == '#')
            {
                if (index + 2 > data.Count) return false;
                var digits = data[index + 1] - '0';
                if (digits is < 1 or > 9 || index + 2 + digits > data.Count) return false;
                var len = int.Parse(Encoding.ASCII.GetString([.. data.GetRange(index + 2, digits)]), CultureInfo.InvariantCulture);
                var end = index + 2 + digits + len;
                if (end > data.Count) return false;
                block = [.. data.GetRange(index + 2 + digits, len)];
                text = Encoding.ASCII.GetString([.. data.GetRange(0, index)]);
                var after = end < data.Count && data[end] == '\n' ? end + 1 : end;
                data.RemoveRange(0, after);
                return true;
            }
            if (data[index] == '\n')
            {
                text = Encoding.ASCII.GetString([.. data.GetRange(0, index)]).TrimEnd('\r');
                data.RemoveRange(0, index + 1);
                return true;
            }
            index++;
        }
        return false;
    }

    private static IEnumerable<string> Split(string line)
        => line.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _listener.Stop();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}

/// <summary>Kompletter simulierter Laboraufbau: je ein Multimeter, Netzgerät, AC-Quelle, Generator, Oszilloskop.</summary>
public sealed class SimulatedLab : IAsyncDisposable
{
    public SimulatedLab(int basePort = 0)
    {
        Bench = new SimulatedBench();
        int Port(int index) => basePort == 0 ? 0 : basePort + index;
        Dmm = new ScpiSimulatorServer(new DmmSimulator("34465A", Bench), Port(1));
        Supply = new ScpiSimulatorServer(new SupplySimulator("E36312A", Bench), Port(2));
        AcSource = new ScpiSimulatorServer(new AcSourceSimulator("AC6802B", Bench), Port(3));
        Generator = new ScpiSimulatorServer(new GeneratorSimulator("33522B", Bench), Port(4));
        Scope = new ScpiSimulatorServer(new ScopeSimulator("DSOX3034T", Bench), Port(5));
    }

    public SimulatedBench Bench { get; }
    public ScpiSimulatorServer Dmm { get; }
    public ScpiSimulatorServer Supply { get; }
    public ScpiSimulatorServer AcSource { get; }
    public ScpiSimulatorServer Generator { get; }
    public ScpiSimulatorServer Scope { get; }

    public IEnumerable<ScpiSimulatorServer> All => [Dmm, Supply, AcSource, Generator, Scope];

    public async ValueTask DisposeAsync()
    {
        foreach (var scpiSimulatorServer in All) await scpiSimulatorServer.DisposeAsync().ConfigureAwait(false);
    }
}
