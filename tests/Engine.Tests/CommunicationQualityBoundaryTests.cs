using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Hardware;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;
using Testumgebung.Persistence;

namespace Testumgebung.Engine.Tests;

public sealed class CommunicationQualityBoundaryTests
{
    private sealed class ModbusPeer(bool failOpen = false) : IStreamTransport
    {
        private readonly Queue<byte> _response = new();
        public byte[] Request { get; private set; } = [];
        public bool Closed;
        public string Description => "fixture";
        public bool IsOpen => !Closed;
        public Task OpenAsync(CancellationToken token)
            => failOpen ? Task.FromException(new CommunicationException("fixture offline")) : Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            Request = data.ToArray();
            byte[] pdu = Request[7] == 3 ? [3, 2, 0, 123] : [Request[7], .. Request.AsSpan(8, 4).ToArray()];
            foreach (var b in new byte[] { Request[0], Request[1], 0, 0, 0, (byte)(pdu.Length + 1), Request[6] }.Concat(pdu))
                _response.Enqueue(b);
            return Task.CompletedTask;
        }
        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken token)
        {
            var count = Math.Min(buffer.Length, _response.Count);
            for (var i = 0; i < count; i++) buffer.Span[i] = _response.Dequeue();
            return Task.FromResult(count);
        }
        public void DiscardInput() => _response.Clear();
        public void Close() => Closed = true;
        public void Dispose() { }
    }

    [Fact]
    public async Task ModbusProbeChecksTheConfiguredRegisterReportsOfflineAndClosesSerialPorts()
    {
        using var tcp = new ModbusDevice(ModbusDevice.TcpAdapter, "localhost:502", new InstrumentOptions(), transport: new ModbusPeer());
        Assert.True((await tcp.ProbeAsync(default)).Ok);
        using var checkedDevice = new ModbusDevice(ModbusDevice.TcpAdapter, "localhost:502",
            new InstrumentOptions(new Dictionary<string, string> { ["Prüfadresse"] = "1/HR0" }), transport: new ModbusPeer());
        var probe = await checkedDevice.ProbeAsync(default);
        Assert.True(probe.Ok);
        Assert.Contains("123", probe.Identity);
        var serialPeer = new ModbusPeer();
        using var serial = new ModbusDevice(ModbusDevice.RtuAdapter, "COM4", new InstrumentOptions(), transport: serialPeer);
        Assert.True((await serial.ProbeAsync(default)).Ok);
        Assert.True(serialPeer.Closed);
        using var offline = new ModbusDevice(ModbusDevice.TcpAdapter, "localhost:502", new InstrumentOptions(), transport: new ModbusPeer(true));
        var failed = await offline.ProbeAsync(default);
        Assert.False(failed.Ok);
        Assert.Contains("fixture offline", failed.Details);
    }

    [Fact]
    public async Task ModbusWritesSingleAndPackedCoilsAndRejectsInvalidCounts()
    {
        var peer = new ModbusPeer();
        using var device = new ModbusDevice(ModbusDevice.TcpAdapter, "localhost:502", new InstrumentOptions(), transport: peer);
        await device.WriteBitsAsync("1", 10, [true], default);
        Assert.Equal(5, peer.Request[7]);
        Assert.Equal(255, peer.Request[10]);
        await device.WriteBitsAsync("1", 10, [false], default);
        Assert.Equal(0, peer.Request[10]);
        await device.WriteBitsAsync("1", 10, [true, false, true, false, true, false, true, false, true], default);
        Assert.Equal(15, peer.Request[7]);
        Assert.Equal(new byte[] { 0x55, 1 }, peer.Request[13..]);
        await Assert.ThrowsAsync<CommunicationException>(() => device.WriteBitsAsync("1", 0, [], default));
        await Assert.ThrowsAsync<CommunicationException>(() => device.WriteBitsAsync("1", 0, new bool[1969], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task J1939ClaimQueriesParticipantsAndReportsAddressConflicts(bool conflict)
    {
        var block = BlockCatalog.Default.Create(BusBlocks.J1939Claim, "claim", "Address Claim");
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Ressource"] = "CAN",
            ["Adresse"] = "0xF9",
            ["Name"] = "0x00000000000000FF",
            ["Abfragen"] = "true",
            ["Ziel"] = "Participants"
        }) block.Parameters[key] = value;
        Assert.Empty(BlockCatalog.Default.Rebuild(block));
        var loop = Kit.Group("loop").Var("Participants", VarType.String).Add(block);
        var project = new Testumgebung.Persistence.TestProject(Kit.Test(loop));
        project.SimulationComm.Add(new SimulatedComm("CAN", "can", CommKind.Can,
            "0x18EAFFF9 00 EE 00 => 0x18EEFF80 01 00 00 00 00 00 00 00; 0x18EEFF80 01 00 00 00 00 00 00 00; 0x18EEFF81 02 00 00 00 00 00 00 00\n"
            + (conflict ? "0x18EEFFF9 FF 00 00 00 00 00 00 00 => 0x18EEFFF9 00 00 00 00 00 00 00 00" : "")));
        var registry = Testumgebung.Persistence.SampleProjects.CreateSimulation(project).Registry;
        var run = new TestRun(project.Definition, new TestRunOptions { Hardware = registry });
        await run.RunAsync();
        var completed = Assert.Single(run.Events.OfType<StepCompleted>(), e => e.NodeId == "claim");
        Assert.Equal(conflict ? ResultRating.Fail : ResultRating.NResult, completed.Rating);
        if (!conflict) Assert.Contains("2 Teilnehmer", completed.Message);
    }
}
