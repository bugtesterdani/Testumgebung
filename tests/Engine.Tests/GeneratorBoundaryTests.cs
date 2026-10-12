using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class GeneratorBoundaryTests
{
    private static string Query(GeneratorSimulator simulator, string command) => Encoding.ASCII.GetString(simulator.Execute(command, null)!).Trim();
    private static double Number(GeneratorSimulator simulator, string command) => double.Parse(Query(simulator, command), CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Amplitude_offset_limits_and_channel_isolation(int channel, bool highZ)
    {
        var bench = new SimulatedBench(); var simulator = new GeneratorSimulator("33500", bench);
        var prefix = $"SOUR{channel}:";
        simulator.Execute($"OUTP{channel}:LOAD {(highZ ? "INF" : "50")}", null);
        simulator.Execute(prefix + "FREQ 200", null); Assert.Equal(200, Number(simulator, prefix + "FREQ?"));
        simulator.Execute(prefix + "FUNC SIN", null); Assert.Equal("SIN", Query(simulator, prefix + "FUNC?"));
        var limit = highZ ? 10 : 5;
        simulator.Execute(prefix + $"VOLT {2 * limit}", null); Assert.Equal(2 * limit, Number(simulator, prefix + "VOLT?"));
        simulator.Execute(prefix + "VOLT:OFFS 1", null); Assert.StartsWith("-222", simulator.NextError()); Assert.Equal(0, Number(simulator, prefix + "VOLT:OFFS?"));
        simulator.Execute(prefix + $"VOLT {2 * limit + 1}", null); Assert.StartsWith("-222", simulator.NextError()); Assert.Equal(2 * limit, Number(simulator, prefix + "VOLT?"));
        simulator.Execute(prefix + "FUNC DC", null); simulator.Execute(prefix + $"VOLT:OFFS {limit}", null);
        Assert.Equal(limit, Number(simulator, prefix + "VOLT:OFFS?"));
        simulator.Execute($"OUTP{channel} 1", null); Assert.Equal("1", Query(simulator, $"OUTP{channel}?"));
        Assert.Equal(limit, bench.GeneratorVoltage(channel - 1, 0));
        Assert.Equal(0, bench.GeneratorVoltage(2 - channel, 0));
        simulator.Execute($"OUTP{channel} 0", null); Assert.Equal("0", Query(simulator, $"OUTP{channel}?"));
        Assert.Equal(0, bench.GeneratorVoltage(channel - 1, 0));
        simulator.Execute("SOUR9:FREQ?", null); Assert.StartsWith("-113", simulator.NextError());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Arbitrary_waveforms_obey_byte_order_range_and_clear(bool swap)
    {
        var bench = new SimulatedBench(); var simulator = new GeneratorSimulator("33500", bench);
        simulator.Execute("FORM:BORD " + (swap ? "SWAP" : "NORM"), null);
        simulator.Execute("FUNC:ARB missing", null); Assert.StartsWith("-224", simulator.NextError());
        simulator.Execute("DATA:ARB signal,", null); Assert.StartsWith("-104", simulator.NextError());
        byte[] Bytes(params float[] values)
        {
            var bytes = new byte[4 * values.Length];
            for (var i = 0; i < values.Length; i++)
                if (swap) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4 * i), values[i]);
                else BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(4 * i), values[i]);
            return bytes;
        }
        foreach (var invalid in new[] { -1.01f, 1.01f })
        {
            simulator.Execute("DATA:ARB bad,", Bytes(invalid)); Assert.StartsWith("-222", simulator.NextError());
            Assert.False(bench.Generator[0].ArbData.ContainsKey("bad"));
        }
        simulator.Execute("DATA:ARB signal,", Bytes(-1, 0, 1));
        simulator.Execute("FUNC:ARB signal", null); simulator.Execute("FUNC:ARB:SRAT 100", null);
        simulator.Execute("VOLT 2", null); simulator.Execute("FUNC ARB", null); simulator.Execute("OUTP 1", null);
        bench.Sync(0);
        Assert.Equal(-1, bench.GeneratorVoltage(0, 0)); Assert.Equal(0, bench.GeneratorVoltage(0, .01)); Assert.Equal(1, bench.GeneratorVoltage(0, .02));
        simulator.Execute("FUNC:ARB:SYNC", null); Assert.True(bench.Generator[0].SyncTime >= 0);
        simulator.Execute("DATA:VOL:CLE", null); Assert.Empty(bench.Generator[0].ArbData); Assert.Null(bench.Generator[0].Arb);
        Assert.Equal(0, bench.GeneratorVoltage(0, 0));
        simulator.Execute("UNKNOWN", null); Assert.StartsWith("-113", simulator.NextError());
    }
}
