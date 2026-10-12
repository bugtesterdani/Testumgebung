using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class PcanBackendContractTests
{
    private sealed class Api : PcanBackend.IPcanApi
    {
        public uint Error { get; set; }
        public uint ReadStatus { get; set; }
        public uint TextStatus { get; set; }
        public byte Type { get; set; }
        public byte Length { get; set; } = 3;
        public int Opens { get; private set; }
        public int Closes { get; private set; }
        public PcanBackend.Native.Msg Written;
        public PcanBackend.Native.MsgFd WrittenFd;
        public string? FdParameters;
        public uint CAN_Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt) { Opens++; return Error; }
        public uint CAN_InitializeFD(ushort channel, string bitrateFd) { Opens++; FdParameters = bitrateFd; return Error; }
        public uint CAN_Uninitialize(ushort channel) { Closes++; return 0; }
        public uint CAN_GetStatus(ushort channel) => Error;
        public uint CAN_Read(ushort channel, out PcanBackend.Native.Msg message, out PcanBackend.Native.Timestamp timestamp)
        {
            message = new() { ID = 123, MSGTYPE = Type, LEN = Length, DATA = [1, 2, 3, 4, 5, 6, 7, 8] };
            timestamp = default;
            return ReadStatus;
        }
        public uint CAN_ReadFD(ushort channel, out PcanBackend.Native.MsgFd message, out ulong timestamp)
        {
            message = new() { ID = 123, MSGTYPE = Type, DLC = Length, DATA = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray() };
            timestamp = 0;
            return ReadStatus;
        }
        public uint CAN_Write(ushort channel, ref PcanBackend.Native.Msg message) { Written = message; return Error; }
        public uint CAN_WriteFD(ushort channel, ref PcanBackend.Native.MsgFd message) { WrittenFd = message; return Error; }
        public uint CAN_GetErrorText(uint error, ushort language, StringBuilder buffer) { buffer.Append("driver error"); return TextStatus; }
    }

    public static IEnumerable<object[]> Frames()
    {
        foreach (var fd in new[] { false, true })
            foreach (var extended in new[] { false, true })
                foreach (var remote in new[] { false, true })
                    foreach (var brs in new[] { false, true })
                        yield return [fd, extended, remote, brs];
    }

    [Theory, MemberData(nameof(Frames))]
    public void Frame_flags_and_data_are_preserved_at_the_driver_boundary(bool fd, bool extended, bool remote, bool brs)
    {
        var api = new Api();
        using var backend = new PcanBackend("PCAN_USBBUS1", api);
        Assert.Equal("geschlossen", backend.Status());
        Assert.Contains("PCAN_USBBUS1", backend.Description);
        var settings = new CanBusSettings(500000, fd, 2000000, null, false);
        backend.Open(settings);
        backend.Open(settings);
        Assert.Equal(1, api.Opens);
        Assert.Equal("OK", backend.Status());
        var frame = new CanFrame(123, [1, 2, 3], extended, fd, brs, remote);
        backend.Send(frame);
        var expectedType = (byte)((extended ? 2 : 0) | (remote ? 1 : 0) | (fd ? 4 : 0) | (fd && brs ? 8 : 0));
        if (fd)
        {
            Assert.Equal(expectedType, api.WrittenFd.MSGTYPE);
            Assert.Equal(3, api.WrittenFd.DLC);
            Assert.Equal(frame.Data, api.WrittenFd.DATA.Take(3));
            Assert.Equal(PcanBackend.FdBitrate(500000, 2000000), api.FdParameters);
        }
        else
        {
            Assert.Equal(expectedType, api.Written.MSGTYPE);
            Assert.Equal(3, api.Written.LEN);
            Assert.Equal(frame.Data, api.Written.DATA.Take(3));
        }
        api.Type = expectedType;
        var received = Assert.IsType<CanFrame>(backend.Read(0));
        Assert.Equal(extended, received.Extended);
        Assert.Equal(remote, received.Remote);
        Assert.Equal(fd, received.Fd);
        Assert.Equal(fd && brs, received.BitRateSwitch);
        Assert.Equal(3, received.Data.Length);
        foreach (var type in new byte[] { 0x80, 0x40 })
        {
            api.Type = type;
            Assert.Null(backend.Read(0));
        }
        foreach (var status in new uint[] { 0x20, 0x21 })
        {
            api.ReadStatus = status;
            Assert.Null(backend.Read(0));
        }
        api.ReadStatus = 1;
        Assert.Throws<CommunicationException>(() => backend.Read(0));
        api.Error = 1;
        Assert.Equal("driver error", backend.Status());
        Assert.Throws<CommunicationException>(() => backend.Send(frame));
        api.TextStatus = 1;
        Assert.Equal("Status 0x1", backend.Status());
        backend.Close();
        backend.Close();
        Assert.Equal(1, api.Closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_open_can_be_retried_without_closing_an_unopened_channel(bool fd)
    {
        var api = new Api { Error = 1 };
        using var backend = new PcanBackend("PCAN_USBBUS1", api);
        var settings = new CanBusSettings(500000, fd, 2000000, "custom", false);
        Assert.Throws<CommunicationException>(() => backend.Open(settings));
        backend.Close();
        Assert.Equal(0, api.Closes);
        api.Error = 0;
        backend.Open(settings);
        Assert.Equal(2, api.Opens);
        if (fd) Assert.Equal("custom", api.FdParameters);
    }
}
