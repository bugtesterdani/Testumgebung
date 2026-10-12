using Testumgebung.Contracts;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class CommunicationSafeStateTests
{
    private sealed class CanPeer : ICanBackend
    {
        public List<CanFrame> Sent = [];
        public string Description => "fixture";
        public void Open(CanBusSettings settings) { }
        public void Send(CanFrame frame) => Sent.Add(frame);
        public CanFrame? Read(int timeoutMs) { Thread.Sleep(1); return null; }
        public string Status() => "fixture";
        public void Close() { }
        public void Dispose() { }
    }
    private sealed class LinPeer : ILinBackend
    {
        public List<(byte Id, byte[] Data, LinChecksum Checksum)> Sent = [];
        public string Description => "fixture";
        public void Open(int baudrate) { }
        public byte[]? Request(byte id, int length, LinChecksum checksum, int timeoutMs) => null;
        public void Publish(byte id, byte[] data, LinChecksum checksum) => Sent.Add((id, data, checksum));
        public string Status() => "fixture";
        public void Close() { }
        public void Dispose() { }
    }
    private sealed class OpcPeer : IOpcUaClient
    {
        public List<(string Node, object? Value)> Written = [];
        public int Connections;
        public Task ConnectAsync(CancellationToken cancellationToken) { Connections++; return Task.CompletedTask; }
        public Task<object?> ReadAsync(string nodeId, CancellationToken cancellationToken) => Task.FromResult<object?>(42L);
        public Task WriteAsync(string nodeId, object? value, CancellationToken cancellationToken) { Written.Add((nodeId, value)); return Task.CompletedTask; }
        public Task<IReadOnlyList<object?>> CallAsync(string objectNodeId, string methodNodeId, IReadOnlyList<object?> arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IOpcUaSubscription> SubscribeAsync(string nodeId, int samplingIntervalMs, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  |  ")]
    [InlineData("0x100 00 FF | 1ABCDE 01 | 123")]
    public async Task Can_safe_state_emits_configured_frames_with_correct_identifier_format(string? text)
    {
        var peer = new CanPeer(); var options = new Dictionary<string, string>(); if (text is not null) options["Sicherer Zustand"] = text;
        using var device = new CanDevice(CanDevice.VirtualAdapter, "fixture", new InstrumentOptions(options), peer);
        await device.SafeStateAsync(CancellationToken.None);
        if (text is not null && text.StartsWith("0x"))
        {
            Assert.Equal(new uint[] { 0x100, 0x1ABCDE, 0x123 }, peer.Sent.Select(f => f.Id));
            Assert.Equal(new byte[] { 0, 255 }, peer.Sent[0].Data); Assert.True(peer.Sent[1].Extended); Assert.Empty(peer.Sent[2].Data);
        }
        else Assert.Empty(peer.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x10 00 FF | 21 |  ")]
    public async Task Lin_safe_state_emits_master_frames_and_default_payload(string? text)
    {
        var peer = new LinPeer(); var options = new Dictionary<string, string>(); if (text is not null) options["Sicherer Zustand"] = text;
        using var device = new LinDevice(LinDevice.VirtualAdapter, "fixture", new InstrumentOptions(options), peer);
        await device.SafeStateAsync(CancellationToken.None);
        if (text is not null && text.StartsWith("0x"))
        {
            Assert.Equal(new byte[] { 0x10, 0x21 }, peer.Sent.Select(f => f.Id)); Assert.Equal(new byte[] { 0, 255 }, peer.Sent[0].Data);
            Assert.Equal(new byte[] { 0 }, peer.Sent[1].Data); Assert.All(peer.Sent, f => Assert.Equal(LinChecksum.Enhanced, f.Checksum));
        }
        else Assert.Empty(peer.Sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad;HR0=not-number; = ;")]
    public async Task Modbus_safe_state_ignores_missing_or_non_numeric_assignments(string? text)
    {
        var pair = LoopbackTransport.Pair(); var options = new Dictionary<string, string>(); if (text is not null) options["Sicherer Zustand"] = text;
        using var device = new ModbusDevice(ModbusDevice.TcpAdapter, "fixture", new InstrumentOptions(options), transport: pair.A);
        using var peer = pair.B;
        await device.SafeStateAsync(CancellationToken.None);
        Assert.Equal(0, await peer.ReadAsync(new byte[30], TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task Opc_safe_state_parses_boolean_integer_float_and_text_and_ignores_unassigned_nodes()
    {
        var peer = new OpcPeer();
        using (var empty = new OpcUaDevice("fixture", new InstrumentOptions(), peer)) await empty.SafeStateAsync(CancellationToken.None);
        Assert.Equal(0, peer.Connections);
        var options = new InstrumentOptions(new Dictionary<string, string> { ["Sicherer Zustand"] = "flag=true | disabled=FALSE | integer=42 | voltage=2.5 | text=\"hello\" | missing | =ignored | " });
        using var device = new OpcUaDevice("fixture", options, peer); await device.SafeStateAsync(CancellationToken.None);
        Assert.Equal(1, peer.Connections); Assert.Equal(new object?[] { true, false, 42L, 2.5, "hello" }, peer.Written.Select(w => w.Value));
        Assert.Equal(new[] { "flag", "disabled", "integer", "voltage", "text" }, peer.Written.Select(w => w.Node));
    }
}
