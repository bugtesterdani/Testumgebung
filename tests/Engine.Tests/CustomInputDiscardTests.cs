using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using Testumgebung.Persistence;

namespace Testumgebung.Engine.Tests;

public sealed class CustomInputDiscardTests
{
    private sealed class Wire : IInstrumentIo, IStreamTransport
    {
        public Queue<byte[]> Replies = [];
        public List<string> Commands = [];
        public int Clears, Closes, Discards;
        public bool IsOpen { get; private set; }
        public string Resource => "simulation";
        public string Description => Resource;
        public bool AcknowledgeSelection;
        public bool FailDiscard;
        public Task OpenAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); IsOpen = true; return Task.CompletedTask; }
        public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var command = Encoding.ASCII.GetString(data).Trim(); Commands.Add(command);
            if (command == "*IDN?") Replies.Enqueue("Vendor,Model,Serial,Firmware\n"u8.ToArray());
            if (command == "OUTP?") Replies.Enqueue("0\n"u8.ToArray());
            if (command == "MEAS?") Replies.Enqueue("12.5\n"u8.ToArray());
            if (command == "CH1" && AcknowledgeSelection) Replies.Enqueue("0\n"u8.ToArray());
            return Task.CompletedTask;
        }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token) => WriteAsync(data.ToArray(), TimeSpan.Zero, token);
        public Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new IoChunk(Replies.Dequeue(), true));
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Replies.Count == 0) { await Task.Delay(timeout, token); return 0; }
            var data = Replies.Dequeue(); data.CopyTo(buffer); return data.Length;
        }
        public Task<DiscardedInput> DiscardInputAsync(TimeSpan settle, CancellationToken token)
        {
            Discards++;
            if (FailDiscard) throw new InstrumentException("discard failure");
            return InputDiscard.DrainAsync(settle, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : Array.Empty<byte>()); }, token);
        }
        public Task ClearAsync(CancellationToken token) { Clears++; Replies.Clear(); return Task.CompletedTask; }
        public void DiscardInput() => throw new InvalidOperationException("Must read discarded bytes for the audit log.");
        public void Close() { Closes++; IsOpen = false; }
        public void Dispose() { }
    }

    private static CustomDeviceProfile Profile(bool enabled, string safe = "*IDN?") => new()
    {
        Id = "discard-meter", Name = "Meter", SafeCommands = safe, ProbeQuery = "*IDN?",
        InputDiscard = enabled ? new() { SettleMilliseconds = 0 } : null,
        Channels = [new() { Name = "U", Unit = "V", ReadQuery = "MEAS?", WriteCommand = "SET {value}" }]
    };

    [Theory]
    [InlineData(false, "*IDN?", false)]
    [InlineData(true, "*IDN?", true)]
    [InlineData(false, "OUTP?", false)]
    [InlineData(true, "OUTP?", true)]
    public async Task Safe_state_replies_do_not_become_measurements_when_enabled(bool enabled, string safe, bool fresh)
    {
        var wire = new Wire();
        using var driver = new CustomTextInstrument(Profile(enabled, safe), new ScpiSession(() => wire));
        await driver.SafeStateAsync(default);
        if (!enabled && safe == "*IDN?") await Assert.ThrowsAsync<InstrumentException>(() => driver.ReadAsync("U", default));
        else Assert.Equal(fresh ? 12.5 : 0, await driver.ReadAsync("U", default));
        Assert.Equal(0, wire.Clears);
        if (enabled) Assert.Contains(driver.Log, line => line.Contains(safe == "*IDN?" ? "Vendor,Model" : "0\\n") && line.Contains("verworfen"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_acknowledgements_are_discarded_under_the_same_session_lock(bool serial)
    {
        var profile = Profile(true); profile.Channels[0].SelectCommands = "CH1";
        var wire = new Wire { AcknowledgeSelection = true };
        using var io = serial ? (IInstrumentIo)new SerialInstrumentIo("COM_TEST", wire, "LF") : wire;
        using var driver = new CustomTextInstrument(profile, new ScpiSession(() => io));
        Assert.Equal(12.5, await driver.ReadAsync("U", default));
        Assert.Equal(new[] { "CH1", "MEAS?" }, wire.Commands);
        Assert.Equal(0, wire.Closes); Assert.Equal(0, wire.Clears);
    }

    [Fact]
    public async Task Safe_state_still_attempts_commands_after_a_discard_failure()
    {
        var wire = new Wire { FailDiscard = true };
        using var driver = new CustomTextInstrument(Profile(true, "OFF"), new ScpiSession(() => wire));
        await Assert.ThrowsAsync<InstrumentException>(() => driver.SafeStateAsync(default));
        Assert.Contains("OFF", wire.Commands);
    }

    [Fact]
    public void Disabled_option_does_not_change_serialization_or_profile_signature()
    {
        var profile = Profile(false);
        var baseline = profile.Signature();
        Assert.DoesNotContain("Discard", baseline);
        profile.DiscardInputBeforeSend = true;
        profile.DiscardInputSettleMilliseconds = 60;
        var exported = JsonSerializer.Deserialize<CustomDeviceProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(60, exported.DiscardInputSettleMilliseconds);
        var project = new TestProject(Kit.Test(Kit.Group("loop"))); project.DeviceProfiles.Add(profile);
        Assert.Equal(profile.Signature(), Assert.Single(new ProjectSerializer().Clone(project).DeviceProfiles).Signature());
        profile.DiscardInputBeforeSend = false;
        Assert.Equal(baseline, profile.Signature());
        foreach (var bad in new[] { -1, 5001 }) { exported.DiscardInputSettleMilliseconds = bad; Assert.NotEmpty(exported.Validate()); }
        exported.DiscardInputSettleMilliseconds = 0; Assert.Empty(exported.Validate());
    }

    [Fact]
    public async Task Bounded_audit_preview_and_cancellation_are_preserved()
    {
        var chunks = new Queue<byte[]>([Enumerable.Repeat((byte)'x', 500).ToArray(), []]);
        var result = await InputDiscard.DrainAsync(TimeSpan.Zero, (_, _) => Task.FromResult(chunks.Dequeue()), default);
        Assert.Equal(500, result.ByteCount); Assert.Equal(200, result.Preview.Length);
        Assert.Contains("…", InputDiscard.Describe(result));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InputDiscard.DrainAsync(TimeSpan.Zero, (_, _) => Task.FromResult(Array.Empty<byte>()), stop.Token));
    }

    [Fact]
    public async Task Simulator_uses_the_same_opt_in_cleanup_over_tcp()
    {
        var bench = new SimulatedBench();
        await using var server = new ScpiSimulatorServer(new DmmSimulator("34465A", bench));
        using var driver = new CustomTextInstrument(Profile(true), new ScpiSession(server.Resource));
        await driver.SafeStateAsync(default);
        // The simulator accepts normal SCPI measurement queries.
        var profile = Profile(true); profile.InputDiscard!.SettleMilliseconds = 100; profile.Channels[0].ReadQuery = "MEAS:VOLT:DC?";
        using var measurement = new CustomTextInstrument(profile, new ScpiSession(server.Resource));
        await measurement.SafeStateAsync(default);
        Assert.True(double.IsFinite(await measurement.ReadAsync("U", default)));
        Assert.Contains(measurement.Log, line => line.Contains("Vendor") || line.Contains("verworfen") && line.Contains("34465A"));
    }
}
