using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class VectorBackendContractTests
{
    private sealed class Api : VectorXlBackend.IVectorApi
    {
        public string? Fail;
        public bool NoMask;
        public bool NoPermission;
        public byte[]? Sent;
        public ushort Tag = 0x400;
        public uint Flags;
        public uint Id = 123;
        public byte Dlc = 3;
        public short ReadStatus;
        public uint Count = 1;
        public int Opens, Closes, PortsClosed;
        private short Status(string operation) => Fail == operation ? (short)-1 : (short)0;
        public short xlOpenDriver() { Opens++; return Status("driver"); }
        public short xlCloseDriver() { Closes++; return 0; }
        public short xlGetApplConfig(string appName, uint appChannel, out uint hwType, out uint hwIndex, out uint hwChannel, uint busType)
        { hwType = 1; hwIndex = 0; hwChannel = appChannel; return Status("config"); }
        public ulong xlGetChannelMask(int hwType, int hwIndex, int hwChannel) => NoMask ? 0ul : 1ul;
        public short xlOpenPort(out int portHandle, string userName, ulong accessMask, ref ulong permissionMask, uint rxQueueSize, uint interfaceVersion, uint busType)
        { portHandle = 123; if (NoPermission) permissionMask = 0; return Status("port"); }
        public short xlCanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate) => Status("bitrate");
        public short xlActivateChannel(int portHandle, ulong accessMask, uint busType, uint flags) => Status("activate");
        public short xlDeactivateChannel(int portHandle, ulong accessMask) => 0;
        public short xlClosePort(int portHandle) { PortsClosed++; return 0; }
        public short xlCanTransmit(int portHandle, ulong accessMask, ref uint messageCount, byte[] events) { Sent = events; return Status("send"); }
        public short xlReceive(int portHandle, ref uint eventCount, byte[] events)
        {
            eventCount = Count; events[0] = (byte)Tag;
            BitConverter.TryWriteBytes(events.AsSpan(16), Id);
            BitConverter.TryWriteBytes(events.AsSpan(20), (ushort)Flags);
            BitConverter.TryWriteBytes(events.AsSpan(22), (ushort)Dlc);
            new byte[] { 1, 2, 3 }.CopyTo(events, 32);
            return ReadStatus;
        }
        public short xlCanTransmitEx(int portHandle, ulong accessMask, uint messageCount, ref uint countSent, byte[] events) { Sent = events; countSent = 1; return Status("send"); }
        public short xlCanReceive(int portHandle, byte[] canRxEvent)
        {
            BitConverter.TryWriteBytes(canRxEvent.AsSpan(), Tag);
            BitConverter.TryWriteBytes(canRxEvent.AsSpan(32), Id);
            BitConverter.TryWriteBytes(canRxEvent.AsSpan(36), Flags);
            canRxEvent[40] = Dlc;
            new byte[] { 1, 2, 3 }.CopyTo(canRxEvent, 44);
            return ReadStatus;
        }
        public short xlCanFdSetConfiguration(int portHandle, ulong accessMask, ref VectorXlBackend.Native.CanFdConf config) => Status("bitrate");
        public IntPtr xlGetErrorString(short status) => IntPtr.Zero;
    }

    public static IEnumerable<object[]> Frames()
    {
        foreach (var fd in new[] { false, true })
            foreach (var extended in new[] { false, true })
                foreach (var remote in new[] { false, true })
                    foreach (var permission in new[] { false, true })
                        foreach (var virtualChannel in new[] { false, true })
                            yield return [fd, extended, remote, permission, virtualChannel];
    }

    [Theory, MemberData(nameof(Frames))]
    public void Native_events_preserve_frame_data_and_deadlines(bool fd, bool extended, bool remote, bool permission, bool virtualChannel)
    {
        var api = new Api { NoPermission = !permission };
        using var backend = new VectorXlBackend(virtualChannel ? "virtuell:0" : "Testumgebung:0", api);
        Assert.Equal("geschlossen", backend.Status());
        backend.Open(new(500000, fd, 2000000, null, false));
        backend.Open(new(500000, fd, 2000000, null, false));
        Assert.Equal(1, api.Opens);
        Assert.Equal("Kanal aktiv", backend.Status());
        var frame = new CanFrame(123, [1, 2, 3], extended, fd, fd, remote);
        backend.Send(frame);
        Assert.Equal(123u | (extended ? 0x80000000u : 0), BitConverter.ToUInt32(api.Sent!, 16));
        Assert.Equal(frame.Data, api.Sent!.Skip(32).Take(3));
        api.Id = frame.Id | (extended ? 0x80000000u : 0);
        api.Tag = fd ? (ushort)0x400 : (ushort)1;
        api.Flags = fd ? (uint)(3 | (remote ? 0x10 : 0)) : (uint)(remote ? 0x10 : 0);
        var received = Assert.IsType<CanFrame>(backend.Read(0));
        Assert.Equal(frame.Data, received.Data);
        Assert.Equal(extended, received.Extended); Assert.Equal(remote, received.Remote);
        Assert.Equal(fd, received.Fd); Assert.Equal(fd, received.BitRateSwitch);
        api.Dlc = byte.MaxValue;
        Assert.Equal(fd ? 0 : 8, backend.Read(0)!.Data.Length);
        // The driver keeps delivering irrelevant events. Zero timeout must still terminate.
        api.Tag = fd ? (ushort)0x404 : (ushort)1;
        api.Flags = 0x40;
        Assert.Null(backend.Read(0));
        api.Tag = 0;
        Assert.Null(backend.Read(0));
        api.Count = 0;
        Assert.Null(backend.Read(0));
        api.ReadStatus = 10;
        Assert.Null(backend.Read(0));
        api.ReadStatus = -1;
        Assert.Throws<CommunicationException>(() => backend.Read(0));
        api.Fail = "send";
        Assert.Throws<CommunicationException>(() => backend.Send(frame));
        if (!fd) Assert.Throws<CommunicationException>(() => backend.Send(frame with { Fd = true }));
        backend.Close(); backend.Close();
        Assert.Equal(1, api.PortsClosed); Assert.Equal(1, api.Closes);
    }

    [Theory]
    [InlineData("driver", false)]
    [InlineData("config", false)]
    [InlineData("port", false)]
    [InlineData("bitrate", false)]
    [InlineData("bitrate", true)]
    [InlineData("activate", false)]
    [InlineData("mask", false)]
    public void Failed_open_releases_driver_and_port_then_allows_retry(string fault, bool fd)
    {
        var api = new Api { Fail = fault, NoMask = fault == "mask" };
        using var backend = new VectorXlBackend("0", api);
        var settings = new CanBusSettings(500000, fd, 2000000, null, false);
        Assert.Throws<CommunicationException>(() => backend.Open(settings));
        Assert.Equal("geschlossen", backend.Status());
        Assert.Equal(fault == "driver" ? 0 : 1, api.Closes);
        Assert.Equal(fault is "bitrate" or "activate" ? 1 : 0, api.PortsClosed);
        api.Fail = null; api.NoMask = false;
        backend.Open(settings);
        Assert.Equal("Kanal aktiv", backend.Status());
    }

    [Fact]
    public void Multiple_channels_share_the_driver_until_the_last_channel_closes()
    {
        var api = new Api();
        using var first = new VectorXlBackend("0", api);
        using var second = new VectorXlBackend("1", api);
        var settings = new CanBusSettings(500000, false, 2000000, null, false);
        first.Open(settings); second.Open(settings);
        Assert.Equal(1, api.Opens);
        first.Close();
        Assert.Equal(0, api.Closes);
        second.Close();
        Assert.Equal(1, api.Closes);
    }
}
