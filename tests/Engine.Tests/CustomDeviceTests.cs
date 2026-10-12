using System.Text;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Persistence;
using Testumgebung.Persistence.Stations;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CustomDeviceTests
{
    private static CustomDeviceProfile Profile() => new()
    {
        Id = "meter", Name = "Mein Messgerät", ProbeQuery = "ID?", SafeCommands = "OFF\r\nSTOP",
        Channels = [new() { Name = "VOLT", Unit = "V", Min = 0, Max = 30, ReadQuery = "V?", WriteCommand = "V {value}" }]
    };
    private static TestProject Project() => new(Kit.Test(Kit.Group("loop")));
    private sealed class Wire : IInstrumentIo
    {
        public string Resource => "test";
        public List<string> Commands { get; } = [];
        public string Reply = "12.5";
        public string? Fail;
        public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var command = Encoding.ASCII.GetString(data).Trim(); Commands.Add(command);
            if (command == Fail) throw new InstrumentException("wire failed");
            return Task.CompletedTask;
        }
        public Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(new IoChunk(Encoding.ASCII.GetBytes(Reply + "\n"), true));
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
    private static CustomTextInstrument Driver(CustomDeviceProfile profile, Wire wire) => new(profile, new ScpiSession("test", () => wire));

    [Theory]
    [InlineData("12.5", 12.5)] [InlineData("ON", 1)] [InlineData("off", 0)] [InlineData("1e-3", .001)]
    public async Task Numeric_reads_and_explicit_writes_use_only_profile_commands(string reply, double expected)
    {
        var profile = Profile(); var wire = new Wire { Reply = reply }; using var driver = Driver(profile, wire);
        profile.Channels[0].ReadQuery = "mutated";
        Assert.Equal(expected, await driver.ReadAsync("volt", default));
        await driver.WriteAsync("VOLT", 12.5, default);
        Assert.Equal(new[] { "V?", "V 12.5" }, wire.Commands);
        Assert.Equal("V", driver.GetCapabilities("VOLT").Unit);
        Assert.Equal("test", driver.Resource); Assert.Equal(Profile().Signature(), driver.ProfileSignature);
        wire.Reply = "Manufacturer,Model,123"; Assert.True((await driver.ProbeAsync(default)).Ok);
        await driver.SafeStateAsync(default); Assert.Equal(new[] { "OFF", "STOP" }, wire.Commands.TakeLast(2));
    }
    [Theory]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("12 V")] [InlineData("")]
    public async Task Invalid_read_responses_fail_without_fabricating_values(string reply)
    {
        using var driver = Driver(Profile(), new Wire { Reply = reply });
        await Assert.ThrowsAsync<InstrumentException>(() => driver.ReadAsync("VOLT", default));
    }
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)] [InlineData(31)]
    public async Task Invalid_writes_never_reach_device(double value)
    {
        var wire = new Wire(); using var driver = Driver(Profile(), wire);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.WriteAsync("VOLT", value, default)); Assert.Empty(wire.Commands);
    }
    [Fact]
    public async Task Access_safe_state_and_probe_failure_are_explicit()
    {
        var profile = Profile(); profile.Channels[0].WriteCommand = ""; profile.SafeCommands = ""; profile.ProbeQuery = "";
        var wire = new Wire(); using var reader = Driver(profile, wire);
        await Assert.ThrowsAsync<InstrumentException>(() => reader.WriteAsync("VOLT", 1, default));
        await reader.SafeStateAsync(default); Assert.False((await reader.ProbeAsync(default)).Ok); Assert.Empty(wire.Commands);
        profile.Channels[0].ReadQuery = ""; profile.Channels[0].WriteCommand = "V {value}";
        using var writer = Driver(profile, wire);
        await Assert.ThrowsAsync<InstrumentException>(() => writer.ReadAsync("VOLT", default));
        await Assert.ThrowsAsync<InstrumentException>(() => writer.SafeStateAsync(default));
        profile.SafeCommands = "OFF\nSTOP"; profile.ProbeQuery = "ID?"; wire.Fail = "OFF";
        using var failed = Driver(profile, wire);
        await Assert.ThrowsAsync<InstrumentException>(() => failed.SafeStateAsync(default)); Assert.Contains("STOP", wire.Commands);
        wire.Fail = "ID?"; Assert.False((await failed.ProbeAsync(default)).Ok);
        wire.Fail = null; wire.Reply = ""; Assert.False((await failed.ProbeAsync(default)).Ok);
    }
    [Theory]
    [InlineData("empty")] [InlineData("duplicate")] [InlineData("range")] [InlineData("finite")] [InlineData("commands")] [InlineData("template")] [InlineData("newline")] [InlineData("probe")]
    [InlineData("nullfields")] [InlineData("nullchannel")]
    public void Invalid_profiles_are_rejected_before_opening_a_connection(string kind)
    {
        var profile = Profile(); var channel = profile.Channels[0];
        switch (kind)
        {
            case "empty": profile.Id = ""; profile.Name = ""; profile.Channels.Clear(); break;
            case "duplicate": profile.Channels.Add(new() { Name = "volt", ReadQuery = "V?" }); break;
            case "range": channel.Min = 31; channel.Name = " "; break;
            case "finite": channel.Max = double.NaN; break;
            case "commands": channel.ReadQuery = ""; channel.WriteCommand = ""; break;
            case "template": channel.WriteCommand = "V 12"; break;
            case "newline": channel.ReadQuery = "V?\nX"; channel.WriteCommand = "V {value}\rX"; break;
            case "probe": profile.ProbeQuery = "ID?\nX"; break;
            case "nullfields": profile.ProbeQuery = null!; profile.SafeCommands = null!; break;
            case "nullchannel": profile.Channels.Add(null!); break;
        }
        Assert.NotEmpty(profile.Validate()); var wire = new Wire();
        Assert.Throws<InstrumentException>(() => Driver(profile, wire)); Assert.Empty(wire.Commands);
    }
    [Fact]
    public void Project_roundtrip_channels_and_device_pool_preserve_profiles_and_reject_changed_definitions()
    {
        var project = Project(); project.DeviceProfiles.Add(Profile()); var serializer = new ProjectSerializer();
        var copy = serializer.Deserialize(serializer.Serialize(project)); Assert.Equal(Profile().Signature(), Assert.Single(copy.DeviceProfiles).Signature());
        copy.DeviceProfiles[0].Name = "Independent"; Assert.Equal("Mein Messgerät", project.DeviceProfiles[0].Name);
        var setup = StationSetup.Default(project, 1);
        var device = new DeviceConfig { Id = "custom", Name = "Meter", Adapter = Profile().Adapter, Resource = "COM9", Model = Profile().Name };
        setup.Devices.Add(device); setup.Stations[0].Resources.Add(new() { Name = "Messung", Device = "custom", Channel = "VOLT" });
        Assert.Equal("VOLT", Assert.Single(StationHardware.ChannelsOf(device, project)!).Name);
        Assert.DoesNotContain(StationHardware.Check(setup, project), i => i.IsError);
        var pool = new DevicePool(); var driver = pool.Get(setup, setup.Stations[0].Name, device.Id, project);
        Assert.Same(driver, pool.Get(setup, setup.Stations[0].Name, device.Id, project));
        var registry = StationHardware.Build(setup, setup.Stations[0].Name, project, pool); Assert.NotNull(registry);
        project.DeviceProfiles[0].Channels[0].Max = 20;
        Assert.Throws<StationSetupException>(() => pool.Get(setup, setup.Stations[0].Name, device.Id, project)); pool.Reset();
        using var tcp = ProjectInstruments.Create(project, new() { Adapter = device.Adapter, Resource = "localhost:5025" });
        project.DeviceProfiles.Add(Profile()); Assert.Contains(StationHardware.Check(setup, project), i => i.IsError);
    }
    [Fact]
    public void Editor_adds_edits_maps_removes_and_persists_custom_devices()
    {
        var project = Project(); var editor = new EditorViewModel(project); var coordinator = new StationCoordinator(StationSetup.Default(project, 1), new ResourceManager());
        var running = false; var page = new StationsViewModel(coordinator, editor, null, new QueueDispatcher(), () => running);
        page.RemoveProfileCommand.Execute(null); page.AddProfileChannelCommand.Execute(null); page.RemoveProfileChannelCommand.Execute(null); page.AddProfileDeviceCommand.Execute(null);
        page.AddProfileCommand.Execute(null); Assert.True(page.HasSelectedProfile); Assert.False(page.ApplyProfiles());
        page.AddProfileChannelCommand.Execute(null); var channel = Assert.Single(page.ProfileChannels);
        channel.Name = "V"; channel.Unit = "V"; channel.Min = 0; channel.Max = 30; channel.ReadQuery = "V?";
        page.SelectedProfile!.Name = "My instrument"; page.SelectedProfile.ProbeQuery = "ID?";
        running = true; Assert.False(page.ApplyProfiles()); Assert.Empty(project.DeviceProfiles);
        running = false; Assert.True(page.ApplyProfiles()); Assert.True(editor.IsDirty);
        page.AddProfileDeviceCommand.Execute(null); var device = page.SelectedDevice!;
        Assert.Equal(page.SelectedProfile.Adapter, device.Adapter); Assert.Equal(new[] { "My instrument" }, device.Models);
        device.Resource = "COM9"; page.AddResourceCommand.Execute(null); var row = page.SelectedRow!; row.Device = device; row.Channel = "V";
        Assert.Equal("V", row.Unit); Assert.Equal(new[] { "V" }, row.ChannelChoices);
        page.Save(); Assert.False(page.HasError); Assert.Equal(device.Adapter, coordinator.Setup.Devices.Last().Adapter);
        page.RemoveProfileCommand.Execute(null); Assert.True(page.HasError);
        page.SelectedProfileChannel = channel; page.RemoveProfileChannelCommand.Execute(null); Assert.Empty(page.ProfileChannels);
        page.AddProfileCommand.Execute(null); Assert.Equal(2, page.DeviceProfiles.Count); page.RemoveProfileCommand.Execute(null); Assert.Single(page.DeviceProfiles);
        device.Adapter = StationSetup.SimulationAdapter; page.RemoveProfileCommand.Execute(null); Assert.Empty(page.DeviceProfiles); Assert.True(page.ApplyProfiles()); Assert.Empty(project.DeviceProfiles);
    }

    [Fact]
    public void Profile_files_roundtrip_without_replacing_existing_identity_and_errors_are_displayed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tu-device-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var project = Project(); project.DeviceProfiles.Add(Profile());
            var page = new StationsViewModel(new StationCoordinator(StationSetup.Default(project, 1), new ResourceManager()), new EditorViewModel(project), null, new QueueDispatcher(), () => false);
            page.ImportProfileCommand.Execute(null); page.ExportProfileCommand.Execute(null);
            var file = Path.Combine(directory, "profile.tdevice.json"); page.ExportProfile(file); Assert.False(page.HasError);
            page.ImportProfile(file); Assert.Equal(2, page.DeviceProfiles.Count); Assert.NotEqual("meter", page.SelectedProfile!.Id);
            Assert.Equal(Profile().Channels[0].WriteCommand, page.SelectedProfile.Channels[0].WriteCommand);
            Assert.True(page.ApplyProfiles());
            page.SelectedProfile = page.DeviceProfiles.First(); page.RemoveProfileCommand.Execute(null);
            page.ImportProfile(file); Assert.Equal("meter", page.SelectedProfile!.Id);
            page.ExportProfile(directory); Assert.True(page.HasError);
            page.ImportProfile(Path.Combine(directory, "missing")); Assert.True(page.HasError);
            foreach (var invalid in new[] { "null", "{broken", "{}", "{\"Id\":\"x\",\"Name\":\"test\",\"Channels\":null}" })
            { File.WriteAllText(file, invalid); page.ImportProfile(file); Assert.True(page.HasError); Assert.Equal(2, page.DeviceProfiles.Count); }
            page.SelectedProfile.Channels.Clear(); page.ExportProfile(file); Assert.True(page.HasError);
            page.SelectedProfile = null; page.ExportProfile(file);
            var serializer = new ProjectSerializer(); var json = System.Text.Json.Nodes.JsonNode.Parse(serializer.Serialize(project))!;
            var profileJson = System.Text.Json.JsonSerializer.Serialize(Profile(), new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            foreach (var invalid in new[] { "[null]", "[{}]", "[" + profileJson + "," + profileJson + "]" })
            { json["deviceProfiles"] = System.Text.Json.Nodes.JsonNode.Parse(invalid); Assert.Throws<ProjectFormatException>(() => serializer.Deserialize(json.ToJsonString())); }
        }
        finally { Directory.Delete(directory, true); }
    }
}
