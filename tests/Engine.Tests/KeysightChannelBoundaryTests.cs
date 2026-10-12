using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class KeysightChannelBoundaryTests
{
    private sealed class Io(Func<string, byte[]> response, int width = 1024) : IInstrumentIo
    {
        private readonly Queue<IoChunk> _chunks = new();
        public List<string> Commands { get; } = [];
        public string Resource => "fixture";
        public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var command = Encoding.ASCII.GetString(data).Trim(); Commands.Add(command);
            if (command.Contains('?'))
            {
                var bytes = response(command);
                for (var i = 0; i < bytes.Length; i += width) { var count = Math.Min(width, bytes.Length - i); _chunks.Enqueue(new IoChunk(bytes.AsSpan(i, count).ToArray(), i + count == bytes.Length)); }
            }
            return Task.CompletedTask;
        }
        public Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(_chunks.Dequeue());
        public Task ClearAsync(CancellationToken cancellationToken) { _chunks.Clear(); return Task.CompletedTask; }
        public void Dispose() { }
    }

    [Theory]
    [InlineData("33509B", "50")]
    [InlineData("33510B", "HighZ")]
    [InlineData("33522B", "INF")]
    [InlineData("33611A", "50")]
    [InlineData("33612A", "HighZ")]
    public async Task Generator_catalog_and_scalar_commands_follow_model_and_load(string model, string load)
    {
        var io = new Io(_ => "2\n"u8.ToArray());
        using var driver = new Keysight33500Generator(model, new ScpiSession(() => io) { CheckErrors = false }, new InstrumentOptions(new Dictionary<string, string> { ["Last"] = load, ["Kurvenkanäle"] = "A=1;Invalid=0;Outside=3;Text=none", ["Speicher"] = "1234" }));
        var capabilities = driver.GetOutputCapabilities("ARB"); Assert.Equal(new[] { "A" }, capabilities.Channels); Assert.Equal(1234, driver.MaxSamples); Assert.Equal(load == "50" ? 5 : 10, driver.Channels.Single(c => c.Name == "CH1:OFFS").Max); Assert.Equal(model.StartsWith("336") ? 1e9 : 250e6, driver.MaxRate);
        foreach (var (channel, query, write) in new[] { ("CH1:FREQ", "SOUR1:FREQ?", "SOUR1:FREQ 2"), ("CH1:AMPL", "SOUR1:VOLT?", "SOUR1:VOLT 2"), ("CH1:OFFS", "SOUR1:VOLT:OFFS?", "SOUR1:VOLT:OFFS 2"), ("CH1:OUT", "OUTP1?", "OUTP1 ON") })
        {
            Assert.Equal(2, await driver.ReadAsync(channel, default)); await driver.WriteAsync(channel, channel.EndsWith("OUT") ? 1 : 2, default); Assert.Contains(query, io.Commands); Assert.Contains(write, io.Commands);
        }
        await driver.WriteAsync("CH1:DC", 2, default); Assert.Contains("SOUR1:FUNC DC", io.Commands);
        await Assert.ThrowsAsync<InstrumentException>(() => driver.ReadAsync("CH1:DC", default));
        Assert.Throws<InstrumentException>(() => driver.GetOutputCapabilities("CH1")); Assert.Null(driver.LoadedCurve("ARB"));
        using var defaults = new Keysight33500Generator(model, new ScpiSession(() => new Io(_ => []))); Assert.Equal(model.StartsWith("336") ? 4_000_000 : 1_000_000, defaults.MaxSamples);
        Assert.Equal(model.EndsWith("0B") || model.EndsWith("2B") || model.EndsWith("2A") ? 2 : 1, defaults.GetOutputCapabilities("ARB").Channels.Count);
    }

    [Theory]
    [InlineData("DSOX1204G", null, null)]
    [InlineData("DSOX1102G", "CHAN1", null)]
    [InlineData("unknown", "CHAN1", "1.5")]
    public async Task Scope_commands_capture_fragmented_words_and_respect_trigger_options(string model, string? trigger, string? level)
    {
        var io = new Io(command => command switch { "*OPC?" => "1\n"u8.ToArray(), ":WAV:PRE?" => "1,0,2,1,0.001,0,0,0.5,1,0\n"u8.ToArray(), ":WAV:DATA?" => new byte[] { 35, 49, 52, 2, 0, 254, 255, 10 }, _ => "2\n"u8.ToArray() }, 1);
        var options = new Dictionary<string, string>(); if (trigger is not null) options["Triggerquelle"] = trigger; if (level is not null) options["Triggerpegel"] = level;
        using var scope = new KeysightInfiniiVisionScope(model, new ScpiSession(() => io) { CheckErrors = false }, new InstrumentOptions(options));
        foreach (var (channel, command) in new[] { ("TIMEBASE", ":TIM:SCAL?"), ("CHAN1:VRMS", ":MEAS:VRMS? DISP,AC,CHAN1"), ("CHAN1:VPP", ":MEAS:VPP? CHAN1"), ("CHAN1:VAVG", ":MEAS:VAV? DISP,CHAN1"), ("CHAN1:FREQ", ":MEAS:FREQ? CHAN1"), ("CHAN1:SCALE", ":CHAN1:SCAL?"), ("CHAN1:OFFSET", ":CHAN1:OFFS?") }) { Assert.Equal(2, await scope.ReadAsync(channel, default)); Assert.Contains(command, io.Commands); }
        await scope.WriteAsync("TIMEBASE", .01, default); await scope.WriteAsync("CHAN1:SCALE", 2, default); await scope.WriteAsync("CHAN1:OFFSET", 2, default);
        Assert.Contains(":TIM:SCAL 0.01", io.Commands); Assert.Contains(":CHAN1:SCAL 2", io.Commands); Assert.Contains(":CHAN1:OFFS 2", io.Commands);
        var set = await scope.CaptureAsync(["CHAN1"], 1000, 2, default); var wave = Assert.Single(set.Channels); Assert.Equal(new[] { 2d, 0d }, wave.Samples); Assert.Equal(1000, wave.SampleRate); Assert.DoesNotContain("angefordert", wave.Quality);
        set = await scope.CaptureAsync(["CHAN1"], 2000, 4, default); Assert.Contains("angefordert", Assert.Single(set.Channels).Quality);
        Assert.Contains(io.Commands, c => c.Contains(trigger is null ? "TRIG:SWE AUTO" : "TRIG:EDGE:SOUR CHAN1")); if (level is not null) Assert.Contains(io.Commands, c => c.Contains("TRIG:EDGE:LEV 1.5"));
        await Assert.ThrowsAsync<InstrumentException>(() => scope.CaptureAsync([], 1000, 2, default)); await Assert.ThrowsAsync<InstrumentException>(() => scope.CaptureAsync(["TIMEBASE"], 1000, 2, default)); await scope.SafeStateAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_rejects_incomplete_preamble_and_odd_word_payload(bool oddPayload)
    {
        var io = new Io(command => command switch { "*OPC?" => "1\n"u8.ToArray(), ":WAV:PRE?" => Encoding.ASCII.GetBytes(oddPayload ? "1,0,2,1,0.001,0,0,0.5,1,0\n" : "1,2\n"), ":WAV:DATA?" => "#13abc\n"u8.ToArray(), _ => "0\n"u8.ToArray() });
        using var scope = new KeysightInfiniiVisionScope("DSOX1102G", new ScpiSession(() => io) { CheckErrors = false });
        var error = await Assert.ThrowsAsync<InstrumentException>(() => scope.CaptureAsync(["CHAN1"], 1000, 2, default)); Assert.Contains(oddPayload ? "ungerade" : "statt 10", error.Message);
    }
}
