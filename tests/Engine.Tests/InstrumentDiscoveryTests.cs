using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class InstrumentDiscoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Discovery_combines_device_families_and_reports_missing_drivers(bool visaAvailable, bool daqAvailable)
    {
        var daqCalled = false;
        var result = InstrumentCatalog.Discover(() => ["USB0::INSTR", "GPIB0::1::INSTR"], visaAvailable, daqAvailable,
            () => { daqCalled = true; return ["Dev1", "Dev2"]; },
            name => new DaqDeviceInfo(name, "DAQ fixture", name == "Dev2", [], [], [], [], 10, 10, 1000, 1000, true),
            () => ["COM3", "COM7"]);
        Assert.Equal(daqAvailable, daqCalled); Assert.Contains("VISA: USB0::INSTR", result); Assert.Contains("Seriell: COM7", result);
        Assert.Equal(!visaAvailable, result.Any(r => r.StartsWith("Keine VISA-Bibliothek")));
        Assert.Equal(!daqAvailable, result.Contains("NI-DAQmx nicht installiert."));
        if (daqAvailable) { Assert.Contains("NI-DAQmx: Dev1 – DAQ fixture", result); Assert.Contains("NI-DAQmx: Dev2 – DAQ fixture (simuliert)", result); }
    }
    [Theory]
    [InlineData("visa")]
    [InlineData("daq-names")]
    [InlineData("daq-info")]
    public void Discovery_reports_one_family_failure_and_keeps_other_families(string failure)
    {
        var result = InstrumentCatalog.Discover(() => failure == "visa" ? throw new InstrumentException("visa failure") : ["USB0::INSTR"], true, true,
            () => failure == "daq-names" ? throw new InstrumentException("daq failure") : ["Dev1"],
            name => failure == "daq-info" ? throw new InstrumentException("info failure") : new DaqDeviceInfo(name, "DAQ", false, [], [], [], [], 10, 10, 1, 1, false),
            () => ["COM3"]);
        Assert.Contains("Seriell: COM3", result); Assert.Contains(result, r => r.Contains("failure"));
        Assert.DoesNotContain("NI-DAQmx nicht installiert.", result);
    }
}
