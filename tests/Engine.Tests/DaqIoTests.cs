using Testumgebung.Contracts;
using Testumgebung.Instruments;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class DaqIoTests
{
    public static IEnumerable<object[]> Models => DaqModels.All.Select(model => new object[] { model });
    [Theory, MemberData(nameof(Models))]
    public void Model_transport_matrix_is_enforced_in_ui_and_driver(DaqModel model)
    {
        var adapter = model.Sdk == DaqSdk.UniversalLibrary ? DaqModels.RedLab : DaqModels.LabJack;
        var config = new DeviceConfig { Adapter = adapter, Model = model.Name, Resource = "123456" };
        var row = new DeviceRow(config, () => { });
        Assert.Equal(model.Connections, row.ConnectionChoices);
        Assert.True(row.HasConnectionChoices); Assert.False(row.HasCommandTimeout);
        foreach (var connection in new[] { "USB", "Ethernet", "WLAN" })
        {
            var options = Options(("Verbindung", connection));
            if (model.Connections.Contains(connection))
            {
                using var device = InstrumentCatalog.Create(adapter, model.Name, "123456", new Dictionary<string,string> { ["Verbindung"] = connection });
                Assert.IsType<DaqIoDevice>(device);
            }
            else Assert.Throws<InstrumentException>(() => new DaqIoDevice(adapter, model.Name, "123456", options));
        }
    }
    [Fact]
    public void Model_change_removes_inapplicable_connection_and_persists_one_canonical_option()
    {
        var changes = 0;
        var config = new DeviceConfig { Adapter = DaqModels.LabJack, Model = "T7-Pro", Resource = "470010001" };
        var row = new DeviceRow(config, () => changes++);
        row.Connection = "WLAN";
        Assert.Equal("WLAN", config.Options!["Verbindung"]);
        Assert.DoesNotContain("Verbindung", row.OptionsText);
        row.OptionsText = "Sichere Ausgänge=DAC0=0";
        Assert.Equal("WLAN", config.Options!["Verbindung"]);
        row.Model = "U3-LV";
        Assert.Equal(new[] { "USB" }, row.ConnectionChoices); Assert.Equal("USB", row.Connection);
        row.Connection = "Ethernet"; Assert.Equal("USB", row.Connection);
        row.Model = "t7-pro"; Assert.Contains("WLAN", row.ConnectionChoices);
        Assert.Equal("USB", config.Options!["Verbindung"]);
        Assert.True(changes > 0);
    }
    [Theory]
    [InlineData("T4", "DIO0", false)] [InlineData("T4", "DIO4", true)]
    [InlineData("U3-HV", "DIO0", false)] [InlineData("U3-LV", "DIO0", true)]
    [InlineData("T8", "AIN8", false)] [InlineData("T7", "AIN13", true)]
    public async Task Simulated_channels_match_physical_model_and_have_state(string model, string channel, bool exists)
    {
        using var device = InstrumentCatalog.Create(DaqModels.Simulation, model, "test");
        Assert.Equal(exists, device.Channels.Any(item => item.Name == channel));
        await device.WriteAsync("DAC0", 3.2, default);
        await device.WriteAsync("DIO4", 1, default);
        Assert.Equal(1, await device.ReadAsync("DIO4", default));
        Assert.False(device.GetCapabilities("AIN0").SynchronousTimebase);
        Assert.True((await device.ProbeAsync(default)).Ok);
    }
    [Fact]
    public async Task Simulator_supports_feedback_safety_and_abort_without_writing_unconfigured_outputs()
    {
        using var device = InstrumentCatalog.Create(DaqModels.Simulation, "T7", "sim",
            new Dictionary<string,string> { ["Rueckkopplung"] = "AIN0=DAC0", ["Sichere Ausgänge"] = "DAC0=0" });
        await device.WriteAsync("DAC0", 4.2, default);
        Assert.Equal(4.2, await device.ReadAsync("AIN0", default));
        await device.SafeStateAsync(default); Assert.Equal(0, await device.ReadAsync("AIN0", default));
        await Assert.ThrowsAsync<InstrumentException>(() => device.WriteAsync("DAC0", 6, default));
        await Assert.ThrowsAsync<InstrumentException>(() => device.WriteAsync("DIO0", .5, default));
        await Assert.ThrowsAsync<InstrumentException>(() => device.ReadAsync("DAC0", default));
        await Assert.ThrowsAsync<InstrumentException>(() => device.WriteAsync("AIN0", 1, default));
        await Assert.ThrowsAsync<InstrumentException>(() => device.WriteAsync("DAC0", double.NaN, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.WriteAsync("DAC0", 2, cancelled.Token));
        Assert.Equal(0, await device.ReadAsync("AIN0", default));
    }
    [Fact]
    public async Task Device_is_lazy_releases_once_and_rejects_unknown_invalid_or_nonfinite_reads()
    {
        var opened = 0; var session = new FakeSession();
        using var device = new DaqIoDevice(DaqModels.LabJack, "T7", "470010001", Options(), () => { opened++; return session; });
        Assert.Equal(0, opened);
        await device.ProbeAsync(default); Assert.Equal(1, opened);
        Assert.Throws<InstrumentException>(() => device.GetCapabilities("missing"));
        session.Value = double.PositiveInfinity;
        await Assert.ThrowsAsync<InstrumentException>(() => device.ReadAsync("AIN0", default));
        device.Dispose(); device.Dispose(); Assert.Equal(1, session.Disposed);
        Assert.Throws<ObjectDisposedException>(() => device.GetCapabilities("AIN0"));
        Assert.Throws<InstrumentException>(() => new DaqIoDevice(DaqModels.LabJack, "missing", "1", Options()));
        Assert.Throws<InstrumentException>(() => new DaqIoDevice(DaqModels.LabJack, "T7", "ANY", Options()));
    }
    [Fact]
    public void Ul_simulation_requires_explicit_counts_and_valid_input_feedback()
    {
        using var device = InstrumentCatalog.Create(DaqModels.Simulation, DaqModels.AutoUsb, "ul-sim", new Dictionary<string,string> { ["AI-Kanaele"] = "3", ["AO-Kanaele"] = "1", ["DIO-Kanaele"] = "8" });
        Assert.Equal(12, device.Channels.Count);
        Assert.Throws<InstrumentException>(() => new SimulatedDaqIoSession(DaqModels.Get(DaqModels.RedLab, DaqModels.AutoUsb), Options(("AI-Kanaele", "1.5"))));
        Assert.Throws<InstrumentException>(() => new SimulatedDaqIoSession(DaqModels.Get(DaqModels.LabJack, "T7"), Options(("Eingaenge", "unknown=2"))));
        Assert.Throws<InstrumentException>(() => new SimulatedDaqIoSession(DaqModels.Get(DaqModels.LabJack, "T7"), Options(("Rueckkopplung", "AIN0=DIO0"))));
    }
    [Fact]
    public async Task Safe_state_attempts_remaining_outputs_even_if_one_is_invalid()
    {
        using var device = InstrumentCatalog.Create(DaqModels.Simulation, "T7", "safe-test",
            new Dictionary<string,string> { ["Rueckkopplung"] = "AIN0=DAC0", ["Sichere Ausgänge"] = "unknown=abc;DAC0=0;DIO4=0" });
        await device.WriteAsync("DAC0", 3, default); await device.WriteAsync("DIO4", 1, default);
        var error = await Assert.ThrowsAsync<InstrumentException>(() => device.SafeStateAsync(default));
        Assert.Single(Assert.IsType<AggregateException>(error.InnerException).InnerExceptions);
        Assert.Equal(0, await device.ReadAsync("AIN0", default)); Assert.Equal(0, await device.ReadAsync("DIO4", default));
    }
    [Theory]
    [InlineData("AIN0")] [InlineData("=1")] [InlineData("AIN0=")] [InlineData("AIN0=1;ain0=2")]
    public void Daq_mapping_does_not_ignore_malformed_or_duplicate_assignments(string mapping)
    {
        Assert.Throws<InstrumentException>(() => new SimulatedDaqIoSession(DaqModels.Get(DaqModels.LabJack, "T7"), Options(("Eingaenge",mapping))));
    }
    [Fact]
    public async Task Daq_feedback_mapping_is_case_insensitive()
    {
        using var device = InstrumentCatalog.Create(DaqModels.Simulation,"T7","case-test",new Dictionary<string,string> { ["Rueckkopplung"]="ain0=dac0" });
        await device.WriteAsync("DAC0",2,default);
        Assert.Equal(2,await device.ReadAsync("AIN0",default));
    }
    internal static InstrumentOptions Options(params (string Key, string Value)[] values) => new(values.ToDictionary(value => value.Key, value => value.Value));
    private sealed class FakeSession : IDaqIoSession
    {
        public double Value; public int Disposed;
        public string Identity => "test";
        public IReadOnlyList<InstrumentChannel> Channels => [new("AIN0", "V", -10, 10, ChannelAccess.Read, "test")];
        public double Read(string channel) => Value;
        public void Write(string channel, double value) { }
        public void Dispose() => Disposed++;
    }
}
