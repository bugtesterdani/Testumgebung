using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class PlinBackendContractTests
{
    private sealed class Api : PlinBackend.IPlinApi
    {
        public string? Fail;
        public bool ErrorTextFails;
        public int Removed, Disconnected, Count = 1;
        public bool Written;
        public PlinBackend.Native.FrameEntry Entry;
        public PlinBackend.Native.Msg Message;
        public Queue<(int Status, PlinBackend.Native.RcvMsg Message)> Responses { get; } = new();
        private int Status(string name) => Fail == name ? 42 : 0;
        public int LIN_RegisterClient(string name, uint hWnd, out byte client) { client = 1; return Status("register"); }
        public int LIN_RemoveClient(byte client) { Removed++; return 0; }
        public int LIN_ConnectClient(byte client, ushort hw) => Status("connect");
        public int LIN_DisconnectClient(byte client, ushort hw) { Disconnected++; return 0; }
        public int LIN_InitializeHardware(byte client, ushort hw, byte mode, ushort baudrate) { Assert.Equal(2, mode); Assert.Equal(19200, baudrate); return Status("initialize"); }
        public int LIN_GetAvailableHardware(ushort[] buffer, ushort bufferSize, out int count) { buffer[0] = 123; count = Count; return Status("hardware"); }
        public int LIN_SetClientFilter(byte client, ushort hw, ulong mask) { Assert.Equal(ulong.MaxValue, mask); return Status("filter"); }
        public int LIN_SetFrameEntry(byte client, ushort hw, ref PlinBackend.Native.FrameEntry entry) { Entry = entry; return Status("entry"); }
        public int LIN_Write(byte client, ushort hw, ref PlinBackend.Native.Msg message) { Message = message; Written = true; return Status("write"); }
        public int LIN_Read(byte client, out PlinBackend.Native.RcvMsg message)
        {
            if (!Written || Responses.Count == 0) { message = default; return 3; }
            var next = Responses.Dequeue(); message = next.Message; return next.Status;
        }
        public int LIN_GetPID(ref byte frameId) { frameId = Lin.ProtectedId(frameId); return Status("pid"); }
        public int LIN_CalculateChecksum(ref PlinBackend.Native.Msg message) { message.Checksum = 42; return Status("checksum"); }
        public int LIN_GetErrorText(int error, byte language, StringBuilder buffer, int bufferSize) { buffer.Append("fixture error"); return ErrorTextFails ? 42 : 0; }
    }

    [Theory]
    [InlineData(LinChecksum.Classic)]
    [InlineData(LinChecksum.Enhanced)]
    public void Publisher_and_subscriber_use_correct_native_fields_and_cleanup(LinChecksum checksum)
    {
        var api = new Api(); using var backend = new PlinBackend("PLIN1", api);
        Assert.Equal("geschlossen", backend.Status()); backend.Close(); backend.Open(19200); backend.Open(19200);
        Assert.Contains("123", backend.Description); Assert.Equal("Master aktiv", backend.Status());
        backend.Publish(0x21, [1, 2, 3], checksum);
        Assert.Equal(1, api.Entry.Direction); Assert.Equal(checksum == LinChecksum.Classic ? 1 : 2, api.Entry.ChecksumType);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0 }, api.Entry.InitialData);
        Assert.Equal(Lin.ProtectedId(0x21), api.Message.FrameId); Assert.Equal(42, api.Message.Checksum);
        api.Written = false;
        api.Responses.Enqueue((0, new() { Type = 1, FrameId = 0x21, Data = new byte[8] }));
        api.Responses.Enqueue((0, new() { FrameId = 0x22, Data = new byte[8] }));
        api.Responses.Enqueue((0, new() { FrameId = Lin.ProtectedId(0x21), Length = 3, Data = [4, 5, 6, 0, 0, 0, 0, 0] }));
        Assert.Equal(new byte[] { 4, 5, 6 }, backend.Request(0x21, 3, checksum, 100)); Assert.Equal(2, api.Entry.Direction);
        backend.Close(); backend.Close(); Assert.Equal(1, api.Removed); Assert.Equal(1, api.Disconnected);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("hardware")]
    [InlineData("connect")]
    [InlineData("initialize")]
    [InlineData("filter")]
    public void Failed_open_reports_native_error_and_can_retry(string fail)
    {
        var api = new Api { Fail = fail }; using var backend = new PlinBackend("1", api);
        Assert.Contains("fixture error", Assert.Throws<CommunicationException>(() => backend.Open(19200)).Message);
        Assert.Equal(fail == "register" ? 0 : 1, api.Removed); Assert.Equal("geschlossen", backend.Status());
        api.Fail = null; backend.Open(19200); Assert.Equal("Master aktiv", backend.Status());
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("pid")]
    [InlineData("checksum")]
    [InlineData("write")]
    public void Publish_errors_have_status_fallback(string fail)
    {
        var api = new Api { Fail = fail, ErrorTextFails = true }; using var backend = new PlinBackend("1", api); backend.Open(19200);
        Assert.Contains("Fehler 42", Assert.Throws<CommunicationException>(() => backend.Publish(1, [1], LinChecksum.Classic)).Message);
    }

    [Fact]
    public void Missing_hardware_receive_errors_timeout_and_length_clamp()
    {
        var api = new Api { Count = 0 }; using var backend = new PlinBackend("1", api);
        Assert.Contains("nicht gefunden", Assert.Throws<CommunicationException>(() => backend.Open(19200)).Message);
        api.Count = 1; backend.Open(19200);
        api.Responses.Enqueue((42, default)); Assert.Throws<CommunicationException>(() => backend.Request(1, 8, LinChecksum.Classic, 100));
        api.Written = false; api.Responses.Enqueue((0, new() { FrameId = 1, ErrorFlags = 1 })); Assert.Null(backend.Request(1, 8, LinChecksum.Classic, 100));
        Assert.Null(backend.Request(1, 8, LinChecksum.Classic, 1));
        api.Written = false; api.Responses.Enqueue((0, new() { FrameId = 1, Length = 255, Data = new byte[8] })); Assert.Equal(8, backend.Request(1, 8, LinChecksum.Classic, 100)!.Length);
    }
}
