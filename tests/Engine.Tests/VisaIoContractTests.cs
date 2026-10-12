using Testumgebung.Instruments;

namespace Testumgebung.Engine.Tests;

public sealed class VisaIoContractTests
{
    private const int Timeout = unchecked((int)0xbfff0015);
    private sealed class Api : IVisaApi
    {
        public int RmStatus, OpenStatus, WriteStatus, ClearStatus;
        public bool PartialWrite, FailAttribute;
        public int Opens;
        public List<uint> Closed = [];
        public List<(uint Attribute, ulong Value)> Attributes = [];
        public Queue<(int Status, byte[] Bytes)> Reads = [];
        public byte[] Written = [];
        public Action? BeforeRead;
        public int OpenDefaultRM(out uint rm) { rm = 10; return RmStatus; }
        public int Open(uint rm, string resource, uint mode, uint timeout, out uint vi) { Opens++; vi = 20; return OpenStatus; }
        public int Close(uint vi) { Closed.Add(vi); return 0; }
        public int Read(uint vi, byte[] buffer, uint count, out uint read)
        {
            BeforeRead?.Invoke(); var next = Reads.Count > 0 ? Reads.Dequeue() : (Timeout, Array.Empty<byte>());
            next.Item2.CopyTo(buffer, 0); read = (uint)next.Item2.Length; return next.Item1;
        }
        public int Write(uint vi, byte[] buffer, uint count, out uint written)
        { Written = buffer.ToArray(); written = PartialWrite ? count - 1 : count; return WriteStatus; }
        public int Clear(uint vi) => ClearStatus;
        public void SetAttribute(uint vi, uint attribute, ulong value)
        { if (FailAttribute) throw new InstrumentException("attribute failure"); Attributes.Add((attribute, value)); }
        public string Describe(uint vi, int status) => "controlled error";
        public void Check(uint vi, int status, string call) { if (status < 0) throw new InstrumentException(call); }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0x3fff0005, true)]
    [InlineData(0x3fff0006, false)]
    [InlineData(1, false)]
    [InlineData(Timeout, false)]
    public async Task Read_preserves_bytes_and_end_of_message_status(int status, bool end)
    {
        var api = new Api();
        api.Reads.Enqueue((Timeout, [])); api.Reads.Enqueue((0, [])); api.Reads.Enqueue((status, [1, 0, 10, 255]));
        using var io = new VisaIo("TEST::INSTR", api);
        await io.ClearAsync(default); Assert.Equal(0, api.Opens);
        var chunk = await io.ReadAsync(TimeSpan.FromSeconds(1), default);
        Assert.Equal(new byte[] { 1, 0, 10, 255 }, chunk.Data); Assert.Equal(end, chunk.End);
        Assert.Equal(1, api.Opens);
        await io.WriteAsync([5, 6], TimeSpan.Zero, default);
        Assert.Equal(new byte[] { 5, 6 }, api.Written); Assert.Equal(1, api.Opens);
        Assert.Contains((0x3fff001au, 1UL), api.Attributes);
        await io.ClearAsync(default);
        io.Dispose(); io.Dispose();
        Assert.Equal(new uint[] { 20, 10 }, api.Closed);
    }

    [Theory]
    [InlineData("rm")]
    [InlineData("open")]
    [InlineData("attribute")]
    public async Task Failed_session_setup_releases_open_handles(string failure)
    {
        var api = new Api { RmStatus = failure == "rm" ? -1 : 0, OpenStatus = failure == "open" ? -1 : 0, FailAttribute = failure == "attribute" };
        using var io = new VisaIo("TEST::INSTR", api);
        await Assert.ThrowsAsync<InstrumentException>(() => io.WriteAsync([1], TimeSpan.FromSeconds(1), default));
        Assert.Equal(failure == "attribute" ? new uint[] { 20, 10 } : failure == "open" ? new uint[] { 10 } : Array.Empty<uint>(), api.Closed);
        api.FailAttribute = false; api.OpenStatus = 0; api.RmStatus = 0;
        await io.WriteAsync([2], TimeSpan.FromSeconds(1), default);
        Assert.Equal(new byte[] { 2 }, api.Written);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("error")]
    [InlineData("partial")]
    public async Task Write_reports_driver_errors_and_partial_transfers(string failure)
    {
        var api = new Api { WriteStatus = failure == "timeout" ? Timeout : failure == "error" ? -1 : 0, PartialWrite = failure == "partial" };
        using var io = new VisaIo("TEST::INSTR", api);
        if (failure == "timeout") await Assert.ThrowsAsync<TimeoutException>(() => io.WriteAsync([1], TimeSpan.FromSeconds(1), default));
        else await Assert.ThrowsAsync<InstrumentException>(() => io.WriteAsync([1], TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task Read_reports_errors_timeout_and_cancellation_without_opening_cancelled_session()
    {
        var api = new Api();
        using var io = new VisaIo("TEST::INSTR", api);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => io.ReadAsync(TimeSpan.FromSeconds(1), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => io.WriteAsync([1], TimeSpan.FromSeconds(1), cancelled.Token));
        Assert.Equal(0, api.Opens);
        await Assert.ThrowsAsync<TimeoutException>(() => io.ReadAsync(TimeSpan.Zero, default));
        api.Reads.Enqueue((-1, []));
        await Assert.ThrowsAsync<InstrumentException>(() => io.ReadAsync(TimeSpan.FromSeconds(1), default));
        using var during = new CancellationTokenSource(); api.BeforeRead = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => io.ReadAsync(TimeSpan.FromSeconds(1), during.Token));
        api.ClearStatus = -1;
        await Assert.ThrowsAsync<InstrumentException>(() => io.ClearAsync(default));
    }
}
