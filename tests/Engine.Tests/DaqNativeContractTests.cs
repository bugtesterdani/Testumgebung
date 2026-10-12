using System.Text;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class DaqNativeContractTests
{
    [Theory]
    [InlineData("DAQmxGetDevAIPhysicalChans")]
    [InlineData("DAQmxGetDevAOPhysicalChans")]
    [InlineData("DAQmxGetDevDOLines")]
    [InlineData("DAQmxGetDevDILines")]
    [InlineData("DAQmxGetDevAIVoltageRngs")]
    [InlineData("DAQmxGetDevAOVoltageRngs")]
    [InlineData("DAQmxGetDevAIMaxMultiChanRate")]
    [InlineData("DAQmxGetDevAOMaxRate")]
    [InlineData("DAQmxGetDevAISimultaneousSamplingSupported")]
    public void Optional_device_queries_can_fail_without_hiding_the_device(string function)
    {
        var stub = new DaqNativeApiStub { Fail = function }; var api = new NativeDaqmxApi(stub);
        var device = api.DeviceInfo("Dev1");
        Assert.Equal("Dev1", device.Name); Assert.Equal(10, device.AiMax); Assert.Equal(10, device.AoMax);
        Assert.Empty(device.AiChannels); Assert.Empty(device.AoChannels); Assert.Empty(device.DoLines); Assert.Empty(device.DiLines);
        Assert.Equal(0, device.AiMaxRate); Assert.Equal(0, device.AoMaxRate); Assert.False(device.SimultaneousSampling);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Required_device_query_and_missing_library_produce_instrument_error(bool missingLibrary)
    {
        var stub = new DaqNativeApiStub { Fail = missingLibrary ? null : "DAQmxGetDevProductType", MissingLibrary = missingLibrary ? "DAQmxGetDevProductType" : "DAQmxGetExtendedErrorInfo" };
        var api = new NativeDaqmxApi(stub);
        var error = Assert.Throws<InstrumentException>(() => api.DeviceInfo("Dev1"));
        Assert.Contains(missingLibrary ? "nicht installiert" : "DAQmxGetDevProductType", error.Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timing_trigger_regeneration_and_layout_flags_match_native_contract(bool enabled)
    {
        var stub = new DaqNativeApiStub(); var api = new NativeDaqmxApi(stub);
        api.CfgSampClkTiming(123, 2000, enabled, 100);
        Assert.Equal(enabled ? 10123 : 10178, stub.Arguments["mode"]); Assert.Equal(10280, stub.Arguments["edge"]); Assert.Equal(100ul, stub.Arguments["samples"]); Assert.Equal(2000d, stub.Arguments["rate"]);
        api.CfgDigEdgeStartTrig(123, "/Dev1/PFI0", enabled); Assert.Equal(enabled ? 10280 : 10171, stub.Arguments["edge"]);
        api.SetWriteRegenMode(123, enabled); Assert.Equal(enabled ? 10097 : 10158, stub.Arguments["mode"]);
        stub.Outputs["DAQmxWriteAnalogF64.written"] = 2;
        Assert.Equal(2, api.WriteAnalogByChannel(123, 2, enabled, 1, [1, 2, 3, 4])); Assert.Equal(enabled ? 1u : 0u, stub.Arguments["autoStart"]); Assert.Equal(0u, stub.Arguments["layout"]);
    }

    [Theory]
    [InlineData(AiTerminal.Default)]
    [InlineData(AiTerminal.Rse)]
    [InlineData(AiTerminal.Nrse)]
    [InlineData(AiTerminal.Diff)]
    [InlineData(AiTerminal.PseudoDiff)]
    public void Channel_creation_and_task_lifecycle_preserve_handles_and_ranges(AiTerminal terminal)
    {
        var stub = new DaqNativeApiStub(); var api = new NativeDaqmxApi(stub); stub.Outputs["DAQmxCreateTask.task"] = (nint)123;
        Assert.Equal((nint)123, api.CreateTask("fixture"));
        api.CreateAIVoltageChan(123, "Dev1/ai0", terminal, -5, 5); Assert.Equal((int)terminal, stub.Arguments["terminal"]); Assert.Equal(-5d, stub.Arguments["min"]); Assert.Equal(5d, stub.Arguments["max"]); Assert.Equal(10348, stub.Arguments["units"]); Assert.Null(stub.Arguments["scale"]);
        api.CreateAOVoltageChan(123, "Dev1/ao0", -10, 10); Assert.Equal("Dev1/ao0", stub.Arguments["physical"]); Assert.Equal(10d, stub.Arguments["max"]);
        api.CreateDOChan(123, "Dev1/port0/line0"); Assert.Equal(0, stub.Arguments["grouping"]);
        api.CreateDIChan(123, "Dev1/port0/line1"); Assert.Equal("Dev1/port0/line1", stub.Arguments["lines"]);
        api.StartTask(123); Assert.Equal("DAQmxStartTask", stub.LastCall); api.StopTask(123); Assert.Equal("DAQmxStopTask", stub.LastCall); api.ClearTask(123); Assert.Equal("DAQmxClearTask", stub.LastCall); Assert.Equal((nint)123, stub.Arguments["task"]);
    }

    [Fact]
    public void Read_and_write_buffers_preserve_sample_counts_and_order()
    {
        var stub = new DaqNativeApiStub(); var api = new NativeDaqmxApi(stub);
        stub.Outputs["DAQmxReadAnalogScalarF64.value"] = 3.5; Assert.Equal(3.5, api.ReadAnalogScalar(123, 2));
        stub.Outputs["DAQmxReadAnalogF64.read"] = 2; stub.Buffers["DAQmxReadAnalogF64"] = new double[] { 1, 2, 3, 4 };
        var buffer = new double[4]; Assert.Equal(2, api.ReadAnalogInterleaved(123, 2, 1, buffer)); Assert.Equal(new double[] { 1, 2, 3, 4 }, buffer); Assert.Equal(1u, stub.Arguments["fill"]);
        api.WriteAnalogScalar(123, -2, 1); Assert.Equal(-2d, stub.Arguments["value"]); Assert.Equal(1u, stub.Arguments["autoStart"]);
        api.WriteDigitalLines(123, [1, 0], 1); Assert.Equal(new byte[] { 1, 0 }, stub.Arguments["data"]);
        stub.Buffers["DAQmxReadDigitalLines"] = new byte[] { 1, 0 }; Assert.Equal(new byte[] { 1, 0 }, api.ReadDigitalLines(123, 2, 1)); Assert.Single(api.ReadDigitalLines(123, 0, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Device_information_returns_channels_ranges_rates_and_optional_fallbacks(bool optionalFailure)
    {
        var stub = new DaqNativeApiStub(); var api = new NativeDaqmxApi(stub);
        stub.Buffers["DAQmxGetSysDevNames"] = Encoding.ASCII.GetBytes("Dev1, Dev2, "); Assert.Equal(new[] { "Dev1", "Dev2" }, api.DeviceNames());
        stub.Buffers["DAQmxGetDevProductType"] = Encoding.ASCII.GetBytes("USB fixture");
        stub.Buffers["DAQmxGetDevAIPhysicalChans"] = Encoding.ASCII.GetBytes("Dev1/ai0,Dev1/ai1");
        stub.Buffers["DAQmxGetDevAIVoltageRngs"] = new double[] { -5, 5 };
        stub.Buffers["DAQmxGetDevAOVoltageRngs"] = new double[] { -10, 10 };
        stub.Outputs["DAQmxGetDevIsSimulated.value"] = 1u; stub.Outputs["DAQmxGetDevAISimultaneousSamplingSupported.value"] = optionalFailure ? 0u : 1u;
        stub.Outputs["DAQmxGetDevAIMaxMultiChanRate.value"] = 2000d; stub.Outputs["DAQmxGetDevAOMaxRate.value"] = 1000d;
        if (optionalFailure) stub.Fail = "DAQmxGetDevAIMaxMultiChanRate";
        var info = api.DeviceInfo("Dev1"); Assert.Equal("USB fixture", info.ProductType); Assert.True(info.IsSimulated); Assert.Equal(new[] { "Dev1/ai0", "Dev1/ai1" }, info.AiChannels);
        Assert.Equal(5, info.AiMax); Assert.Equal(10, info.AoMax); Assert.Equal(optionalFailure ? 0 : 2000, info.AiMaxRate); Assert.Equal(1000, info.AoMaxRate); Assert.Equal(!optionalFailure, info.SimultaneousSampling); Assert.Empty(info.AoChannels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_failure_reports_driver_message_or_fallback(bool withText)
    {
        var stub = new DaqNativeApiStub { Fail = "DAQmxStartTask" }; var api = new NativeDaqmxApi(stub);
        if (withText) stub.Buffers["DAQmxGetExtendedErrorInfo"] = Encoding.ASCII.GetBytes("fixture failure");
        Assert.Contains(withText ? "fixture failure" : "Fehler", Assert.Throws<InstrumentException>(() => api.StartTask(123)).Message);
    }
}
