using System.Text;
using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class AcSourceDriverTests
{
    private sealed class Wire : IInstrumentIo
    {
        public string Resource => "test";
        public List<string> Commands { get; } = [];
        public Dictionary<string, string> Replies { get; } = new() { ["SYST:ERR?"] = "No Error", ["*IDN?"] = "CHROMA,61602,SN,2.2" };
        public string? Fail;
        private string _command = "";
        public Task WriteAsync(byte[] data, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); _command = Encoding.ASCII.GetString(data).Trim(); Commands.Add(_command);
            if (_command == Fail) throw new InstrumentException("Gerätefehler");
            return Task.CompletedTask;
        }
        public Task<IoChunk> ReadAsync(TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new IoChunk(Encoding.ASCII.GetBytes(Replies[_command] + "\n"), true));
        }
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
    private static ScpiSession Session(Wire wire, string resource = "test") => new(resource, () => wire);

    [Theory]
    [InlineData("VOLT", "VOLT:AC 230")]
    [InlineData("FREQ", "FREQ 230")]
    [InlineData("CURR", "CURR:LIM 2")]
    public async Task Chroma_initializes_once_then_uses_documented_set_commands(string channel, string command)
    {
        var wire = new Wire(); using var driver = new Chroma61602Source(Session(wire, "COM3"));
        var value = channel == "CURR" ? 2 : 230;
        await driver.WriteAsync(channel, value, default); await driver.WriteAsync(channel, value, default);
        var sent = wire.Commands.Where(c => c != "SYST:ERR?").ToArray();
        Assert.Equal(new[] { "OUTP OFF", "OUTP:REL OFF", "SYST:REM", "VOLT:RANG HIGH", "OUTP:COUP AC", command, command }, sent);
        Assert.Equal("COM3", driver.Resource);
    }
    [Theory]
    [InlineData("VOLT", "MEAS:VOLT:ACDC?")]
    [InlineData("FREQ", "MEAS:FREQ?")]
    [InlineData("CURR", "MEAS:CURR:AC?")]
    [InlineData("POW", "MEAS:POW:AC?")]
    public async Task Chroma_read_does_not_initialize_or_switch_output(string channel, string query)
    {
        var wire = new Wire(); wire.Replies[query] = "2.5";
        using var driver = new Chroma61602Source(Session(wire)); Assert.Equal(2.5, await driver.ReadAsync(channel, default));
        Assert.Equal(new[] { query }, wire.Commands);
    }
    [Theory]
    [InlineData("ON", 1)] [InlineData("OFF", 0)] [InlineData("1", 1)] [InlineData("0", 0)]
    public async Task Chroma_output_text_is_parsed(string reply, double expected)
    {
        var wire = new Wire(); wire.Replies["OUTP?"] = reply;
        using var driver = new Chroma61602Source(Session(wire)); Assert.Equal(expected, await driver.ReadAsync("OUT", default));
    }
    [Fact]
    public async Task Chroma_bounds_errors_and_relay_shutdown_are_checked()
    {
        var wire = new Wire(); using var driver = new Chroma61602Source(Session(wire), new InstrumentOptions(new Dictionary<string,string> { ["Bereich"] = "LOW" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.WriteAsync("VOLT", 151, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.WriteAsync("OUT", .5, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.WriteAsync("VOLT", double.NaN, default));
        await driver.WriteAsync("OUT", 1, default); await driver.WriteAsync("OUT", 0, default);
        Assert.Contains("VOLT:RANG LOW", wire.Commands); Assert.Contains("OUTP:REL ON", wire.Commands);
        wire.Fail = "OUTP OFF"; await Assert.ThrowsAsync<InstrumentException>(() => driver.SafeStateAsync(default));
        Assert.Equal("SYST:ERR?", wire.Commands.Last()); Assert.Equal("OUTP:REL OFF", wire.Commands[^2]);
        wire.Fail = null; wire.Replies["OUTP?"] = "UNKNOWN";
        await Assert.ThrowsAsync<InstrumentException>(() => driver.ReadAsync("OUT", default));
        wire.Replies["SYST:ERR?"] = "Data Range Error";
        await Assert.ThrowsAsync<InstrumentException>(() => driver.WriteAsync("VOLT", 100, default));
        Assert.Throws<InstrumentException>(() => new Chroma61602Source(Session(new Wire()), new InstrumentOptions(new Dictionary<string,string> { ["Bereich"] = "invalid" })));
    }
    [Fact]
    public async Task Chroma_probe_is_read_only_and_text_error_queue_is_empty()
    {
        var wire = new Wire(); using var driver = new Chroma61602Source(Session(wire));
        Assert.True((await driver.ProbeAsync(default)).Ok);
        Assert.Equal(new[] { "*IDN?", "SYST:ERR?" }, wire.Commands);
    }
    [Theory]
    [InlineData("VOLT", 115, "SOUR:VOLT 115")]
    [InlineData("CURR", 4, "SOUR:CURR 4")]
    [InlineData("FREQ", 60, "SOUR:FREQ 60")]
    [InlineData("DC", -10, "SOUR:VOLT:DC -10")]
    [InlineData("PHAS", 90, "SOUR:PHAS 90")]
    public async Task Eaq_uses_documented_set_commands_without_assumed_queries(string channel, double value, string command)
    {
        var wire = new Wire(); using var driver = new EtEaq1000Source("EAQ-1000", Session(wire));
        await driver.WriteAsync(channel, value, default); Assert.Equal(new[] { command }, wire.Commands);
        await Assert.ThrowsAsync<InstrumentException>(() => driver.ReadAsync(channel, default));
    }
    [Fact]
    public async Task Eaq_outputs_probe_and_optional_queries_are_explicit()
    {
        var wire = new Wire();
        using var driver = new EtEaq1000Source("EAQ-M-1000", Session(wire), new InstrumentOptions(new Dictionary<string,string> { ["Pruefbefehl"] = "IDENT?", ["AbfrageVOLT"] = "VOLT?" }));
        wire.Replies["IDENT?"] = "ET EAQ-1000"; wire.Replies["VOLT?"] = "115";
        await driver.WriteAsync("OUT", 1, default); await driver.WriteAsync("OUT", 0, default);
        Assert.Equal(new[] { "SOUR ON", "SOUR:OUTP ON", "SOUR:OUTP OFF", "SOUR OFF" }, wire.Commands);
        Assert.True((await driver.ProbeAsync(default)).Ok); Assert.Equal(115, await driver.ReadAsync("VOLT", default));
        wire.Fail = "IDENT?"; Assert.False((await driver.ProbeAsync(default)).Ok);
        wire.Fail = null; wire.Replies["IDENT?"] = ""; Assert.False((await driver.ProbeAsync(default)).Ok);
        wire.Fail = "SOUR:OUTP OFF"; await Assert.ThrowsAsync<InstrumentException>(() => driver.SafeStateAsync(default));
        Assert.Equal("SOUR OFF", wire.Commands.Last());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.WriteAsync("OUT", .5, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => driver.WriteAsync("VOLT", double.NaN, default));
        using var unconfigured = new EtEaq1000Source("EAQ-1000", Session(new Wire())); Assert.False((await unconfigured.ProbeAsync(default)).Ok);
    }
    [Fact]
    public void Both_devices_are_available_in_catalog_without_opening_ports()
    {
        using var chroma = InstrumentCatalog.Create(Chroma61602Source.AdapterName, "61602", "COM3");
        using var eaq = InstrumentCatalog.Create(EtEaq1000Source.AdapterName, "EAQ-M-1000", "COM4");
        using var lan = InstrumentCatalog.Create(Chroma61602Source.AdapterName, "61602", "host:5025");
        Assert.IsType<Chroma61602Source>(chroma); Assert.IsType<EtEaq1000Source>(eaq);
        Assert.NotNull(InstrumentCatalog.Family(chroma.Adapter)); Assert.NotNull(InstrumentCatalog.Family(eaq.Adapter));
    }
    private sealed class Port : IStreamTransport
    {
        public string Description => "fake COM"; public bool IsOpen { get; private set; }
        public byte[] Data = []; public byte[] Reply = "OK\r\n"u8.ToArray();
        public bool Fail;
        public Task OpenAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); IsOpen = true; return Task.CompletedTask; }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) { if (Fail) throw new Testumgebung.Contracts.CommunicationException("Portfehler"); Data = data.ToArray(); return Task.CompletedTask; }
        public Task<int> ReadAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct) { ct.ThrowIfCancellationRequested(); if (Fail) throw new Testumgebung.Contracts.CommunicationException("Portfehler"); Reply.CopyTo(buffer); return Task.FromResult(Reply.Length); }
        public void DiscardInput() { } public void Close() => IsOpen = false; public void Dispose() => Close();
    }
    [Theory]
    [InlineData("LF", "\n")] [InlineData("CR", "\r")] [InlineData("CRLF", "\r\n")]
    public async Task Serial_scpi_preserves_terminator_timeout_cancellation_and_error_details(string kind, string ending)
    {
        var port = new Port(); using var io = new SerialInstrumentIo("COM4", port, kind);
        await io.WriteAsync("SOUR ON\n"u8.ToArray(), TimeSpan.FromSeconds(1), default);
        Assert.Equal("SOUR ON" + ending, Encoding.ASCII.GetString(port.Data));
        port.Reply = Encoding.ASCII.GetBytes("OK" + ending); Assert.True((await io.ReadAsync(TimeSpan.FromSeconds(1), default)).End);
        port.Reply = "O"u8.ToArray(); Assert.False((await io.ReadAsync(TimeSpan.FromSeconds(1), default)).End);
        port.Reply = []; await Assert.ThrowsAsync<TimeoutException>(() => io.ReadAsync(TimeSpan.FromMilliseconds(1), default));
        port.Fail = true; await Assert.ThrowsAsync<InstrumentException>(() => io.WriteAsync([], TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<InstrumentException>(() => io.ReadAsync(TimeSpan.FromSeconds(1), default));
        port.Fail = false; await io.ClearAsync(default); Assert.False(port.IsOpen);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => io.ClearAsync(cancelled.Token));
        Assert.Throws<InstrumentException>(() => new SerialInstrumentIo("COM4", port, "invalid"));
    }
}
