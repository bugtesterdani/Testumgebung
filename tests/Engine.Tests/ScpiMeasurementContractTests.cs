using System.Globalization;
using System.Text;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class ScpiMeasurementContractTests
{
    private static string Query(ScpiDeviceSimulator device, string command) => Encoding.ASCII.GetString(device.Execute(command, null)!).Trim();
    private static double Number(ScpiDeviceSimulator device, string command) => double.Parse(Query(device, command), CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("DSOX1102G")]
    [InlineData("DSOX3034T")]
    [InlineData("OTHER")]
    public void Scope_captures_selected_channels_and_returns_consistent_binary_preamble(string model)
    {
        var bench = new SimulatedBench { AcOn = true, AcVolt = 230, AcFreq = 50 };
        bench.DcOn[0] = true; bench.DcVolt[0] = 12; bench.DcCurrentLimit[0] = 10;
        bench.Generator[0].On = true; bench.Generator[0].Function = "DC"; bench.Generator[0].Offset = 2;
        bench.Generator[1].On = true; bench.Generator[1].Function = "SIN"; bench.Generator[1].Amplitude = 4;
        var scope = new ScopeSimulator(model, bench);
        Assert.Contains(model.StartsWith("DSOX") ? "DSO-X " + model[4..] : model, Query(scope, "*IDN?"));
        scope.Execute("TIM:SCAL 0.002", null); Assert.Equal(0.002, Number(scope, "TIM:SCAL?"));
        foreach (var channel in Enumerable.Range(1, 4))
        {
            scope.Execute($"CHAN{channel}:SCAL 2", null); scope.Execute($"CHAN{channel}:OFFS -1", null);
            Assert.Equal(2, Number(scope, $"CHAN{channel}:SCAL?")); Assert.Equal(-1, Number(scope, $"CHAN{channel}:OFFS?"));
        }
        Assert.InRange(Number(scope, "MEAS:VRMS? CHAN1"), 229.999, 230.001);
        Assert.InRange(Number(scope, "MEAS:VPP? CHAN1"), 650.4, 650.6);
        Assert.InRange(Number(scope, "MEAS:VAV? CHAN1"), -0.001, 0.001);
        Assert.Equal(50, Number(scope, "MEAS:FREQ? CHAN1"));
        Assert.Equal(2, Number(scope, "MEAS:VAV? CHAN2"));
        Assert.InRange(Number(scope, "MEAS:VRMS? CHAN3"), 1.413, 1.415);
        Assert.Equal(12, Number(scope, "MEAS:VAV? CHAN4"));
        Assert.Equal(1000, Number(scope, "MEAS:FREQ? CHAN2")); Assert.Equal(1000, Number(scope, "MEAS:FREQ? CHAN3"));
        Assert.Equal(9.9e37, Number(scope, "MEAS:FREQ? CHAN4"));
        bench.AcOn = false; Assert.Equal(9.9e37, Number(scope, "MEAS:FREQ? CHAN1"));
        scope.Execute("WAV:POIN 7", null); scope.Execute("DIG CHAN1,CHAN2,CHAN3,CHAN4,INVALID,CHAN9", null);
        Assert.StartsWith("-224", scope.NextError()); Assert.StartsWith("-224", scope.NextError());
        foreach (var source in new[] { "CHAN1", "CHAN2", "CHAN3", "CHAN4", "UNKNOWN" })
        {
            scope.Execute("WAV:SOUR " + source, null);
            Assert.Equal("+7", Query(scope, "WAV:PRE?").Split(',')[2]);
            var bytes = scope.Execute("WAV:DATA?", null)!;
            Assert.Equal("#214", Encoding.ASCII.GetString(bytes[..4])); Assert.Equal(19, bytes.Length); Assert.Equal((byte)10, bytes[^1]);
        }
        foreach (var command in new[] { "TIM:REF LEFT", "WAV:POIN:MODE RAW", "WAV:FORM WORD", "WAV:BYT LSB", "WAV:UNS 0", "TRIG:SWE AUTO", "TRIG:EDGE:SOUR CHAN1", "TRIG:EDGE:LEV 0", "RUN", "STOP" })
            Assert.Null(scope.Execute(command, null));
        scope.Execute("UNKNOWN", null); Assert.StartsWith("-113", scope.NextError());
    }

    [Theory]
    [InlineData("VOLT:DC", 12.0, 0.0001)]
    [InlineData("VOLT:AC", 230.0, 0.01)]
    [InlineData("CURR:DC", 1.0, 0.00001)]
    [InlineData("CURR:AC", 230.0 / 54.8, 0.0001)]
    [InlineData("RES", 1000.0, 0.1)]
    [InlineData("FRES", 1000.0, 0.1)]
    [InlineData("FREQ", 50.0, 0.01)]
    public void Dmm_configuration_and_direct_measurement_agree(string function, double expected, double tolerance)
    {
        var bench = new SimulatedBench { AcOn = true, AcVolt = 230, AcFreq = 50 };
        bench.DcOn[0] = true; bench.DcVolt[0] = 12;
        var dmm = new DmmSimulator("34465A", bench);
        Assert.Null(dmm.Execute("CONFIGURE:" + function, null));
        Assert.InRange(Number(dmm, "READ?"), expected - tolerance, expected + tolerance);
        Assert.InRange(Number(dmm, "MEAS:" + function + "?"), expected - tolerance, expected + tolerance);
        Assert.Null(dmm.Execute(function + ":NPLC 1", null));
        dmm.Execute("CONF:INVALID", null); Assert.StartsWith("-113", dmm.NextError());
        dmm.Execute("*RST", null); Assert.InRange(Number(dmm, "READ?"), 11.9999, 12.0001);
        var limited = new DmmSimulator("34460A", bench); limited.Execute("CONF:FRES", null);
        Assert.StartsWith("-113", limited.NextError());
        bench.AcOn = false; Assert.Equal(0, Number(dmm, "MEAS:FREQ?"));
    }
}
