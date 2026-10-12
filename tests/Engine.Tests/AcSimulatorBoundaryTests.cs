using System.Globalization;
using System.Text;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class AcSimulatorBoundaryTests
{
    [Theory]
    [InlineData("VOLT", 0, true)]
    [InlineData("SOUR:VOLT", 310, true)]
    [InlineData("VOLT", -1, false)]
    [InlineData("VOLT", 311, false)]
    [InlineData("FREQ", 40, true)]
    [InlineData("SOUR:FREQ", 500, true)]
    [InlineData("FREQ", 39, false)]
    [InlineData("FREQ", 501, false)]
    [InlineData("CURR", 0, true)]
    [InlineData("CURR", 40, true)]
    [InlineData("CURR", -1, false)]
    [InlineData("CURR", 41, false)]
    public void Ac_commands_enforce_voltage_frequency_and_current_limits(string command, double value, bool accepted)
    {
        var bench = new SimulatedBench(); var simulator = new AcSourceSimulator("AC6800", bench);
        var before = command.Contains("VOLT") ? bench.AcVolt : command.Contains("FREQ") ? bench.AcFreq : bench.AcCurrentLimit;
        Assert.Null(simulator.Execute(command + " " + value.ToString(CultureInfo.InvariantCulture), null));
        var after = command.Contains("VOLT") ? bench.AcVolt : command.Contains("FREQ") ? bench.AcFreq : bench.AcCurrentLimit;
        Assert.Equal(accepted ? value : before, after); Assert.StartsWith(accepted ? "+0" : "-222", simulator.NextError());
    }

    [Theory]
    [InlineData(false, 155, true)]
    [InlineData(false, 310, true)]
    [InlineData(false, 200, false)]
    [InlineData(true, 155, false)]
    public void Voltage_range_change_is_rejected_while_output_is_enabled(bool on, double range, bool accepted)
    {
        var bench = new SimulatedBench { AcOn = on }; var simulator = new AcSourceSimulator("AC6800", bench);
        simulator.Execute("VOLT:RANG " + range.ToString(CultureInfo.InvariantCulture), null);
        Assert.Equal(accepted ? range : 310, bench.AcRange); Assert.StartsWith(accepted ? "+0" : on ? "-221" : "-222", simulator.NextError());
    }

    [Theory]
    [InlineData("OFF", false)]
    [InlineData("ON", true)]
    public void Output_queries_measurement_aliases_and_reset_reflect_the_bench(string state, bool on)
    {
        var bench = new SimulatedBench { AcVolt = 100, AcFreq = 60, AcCurrentLimit = 2 }; var simulator = new AcSourceSimulator("AC6800", bench);
        simulator.Execute("OUTPUT " + state, null); Assert.Equal(on, bench.AcOn);
        Assert.Equal(on ? "1\n" : "0\n", Encoding.ASCII.GetString(simulator.Execute("OUTP?", null)!));
        Assert.Equal(60, double.Parse(Encoding.ASCII.GetString(simulator.Execute("FREQ?", null)!), CultureInfo.InvariantCulture));
        Assert.Equal(100, double.Parse(Encoding.ASCII.GetString(simulator.Execute("VOLT?", null)!), CultureInfo.InvariantCulture));
        foreach (var query in new[] { "MEAS:VOLT:AC?", "MEAS:VOLT?", "MEAS:CURR:AC?", "MEAS:CURR?", "MEAS:POW:AC?", "MEAS:POW?" })
            Assert.True(double.IsFinite(double.Parse(Encoding.ASCII.GetString(simulator.Execute(query, null)!), CultureInfo.InvariantCulture)));
        simulator.Execute("unknown", null); Assert.StartsWith("-113", simulator.NextError());
        Assert.Equal("0\n", Encoding.ASCII.GetString(simulator.Execute("unknown?", null)!)); Assert.StartsWith("-113", simulator.NextError());
        simulator.Execute("FREQ invalid", null); Assert.StartsWith("-104", simulator.NextError());
        simulator.Execute("*RST", null); Assert.False(bench.AcOn); Assert.Equal(0, bench.AcVolt);
    }

    [Fact]
    public void Catalog_classifies_each_supported_communication_adapter()
    {
        var communication = new HashSet<string> { StreamDevice.SerialAdapter, StreamDevice.TcpAdapter, StreamDevice.UdpAdapter, ModbusDevice.RtuAdapter, ModbusDevice.TcpAdapter, CanDevice.PcanAdapter, CanDevice.KvaserAdapter, CanDevice.VirtualAdapter, CanDevice.VectorAdapter, LinDevice.PlinAdapter, LinDevice.VirtualAdapter, OpcUaDevice.AdapterName };
        foreach (var family in InstrumentCatalog.Families) { Assert.Equal(communication.Contains(family.Adapter), InstrumentCatalog.IsCommunication(family.Adapter)); Assert.Same(family, InstrumentCatalog.Family(family.Adapter)); }
        Assert.False(InstrumentCatalog.IsCommunication("unknown")); Assert.Null(InstrumentCatalog.Family("unknown"));
    }
}
