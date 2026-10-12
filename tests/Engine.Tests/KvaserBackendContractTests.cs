using System.Text;
using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class KvaserBackendContractTests
{
    private sealed class Api : KvaserBackend.IKvaserApi
    {
        public string? Fail;
        public int ReadStatus;
        public uint ReadFlags;
        public uint ReadLength = 3;
        public uint WrittenFlags;
        public byte[]? WrittenData;
        public int OpenFlags;
        public int Opens;
        public int Closes;
        public int BusOffs;
        public int Nominal;
        public int DataRate;
        private int Status(string operation) => Fail == operation ? -1 : 0;
        public void canInitializeLibrary() { }
        public int canOpenChannel(int channel, int flags) { Opens++; OpenFlags = flags; return Fail == "open" ? -1 : 123; }
        public int canSetBusParams(int handle, int freq, uint tseg1, uint tseg2, uint sjw, uint noSamp, uint syncmode) { Nominal = freq; return Status("nominal"); }
        public int canSetBusParamsFd(int handle, int freqBrs, uint tseg1Brs, uint tseg2Brs, uint sjwBrs) { DataRate = freqBrs; return Status("data"); }
        public int canBusOn(int handle) => Status("on");
        public int canBusOff(int handle) { BusOffs++; return 0; }
        public int canClose(int handle) { Closes++; return 0; }
        public int canWrite(int handle, int id, byte[] msg, uint dlc, uint flag) { WrittenData = msg.Take((int)dlc).ToArray(); WrittenFlags = flag; return Status("write"); }
        public int canReadWait(int handle, out int id, byte[] msg, out uint dlc, out uint flag, out uint time, uint timeout)
        {
            id = 123; dlc = ReadLength; flag = ReadFlags; time = 0;
            for (var i = 0; i < msg.Length; i++) msg[i] = (byte)i;
            return ReadStatus;
        }
        public int canGetErrorText(int err, StringBuilder buf, uint bufsiz) { buf.Append("driver error"); return 0; }
    }

    public static IEnumerable<object[]> Frames()
    {
        foreach (var fd in new[] { false, true })
            foreach (var extended in new[] { false, true })
                foreach (var remote in new[] { false, true })
                    foreach (var brs in new[] { false, true })
                        foreach (var virtualChannel in new[] { false, true })
                            yield return [fd, extended, remote, brs, virtualChannel];
    }

    [Theory, MemberData(nameof(Frames))]
    public void Native_channel_configuration_and_frame_flags_roundtrip(bool fd, bool extended, bool remote, bool brs, bool virtualChannel)
    {
        var api = new Api();
        using var backend = new KvaserBackend("0", api);
        Assert.Equal("geschlossen", backend.Status());
        Assert.Contains("0", backend.Description);
        var settings = new CanBusSettings(500000, fd, 2000000, null, virtualChannel);
        backend.Open(settings); backend.Open(settings);
        Assert.Equal(1, api.Opens);
        Assert.Equal((fd ? 0x400 : 0) | (virtualChannel ? 0x20 : 0), api.OpenFlags);
        Assert.Equal(fd ? -1000 : -2, api.Nominal);
        if (fd) Assert.Equal(-1002, api.DataRate);
        Assert.Equal("Bus ein", backend.Status());
        var frame = new CanFrame(123, [1, 2, 3], extended, fd, brs, remote);
        backend.Send(frame);
        var flags = (uint)((extended ? 4 : 2) | (remote ? 1 : 0) | (fd ? 0x10000 : 0) | (brs ? 0x20000 : 0));
        Assert.Equal(flags, api.WrittenFlags);
        Assert.Equal(frame.Data, api.WrittenData);
        api.ReadFlags = flags;
        var received = Assert.IsType<CanFrame>(backend.Read(0));
        Assert.Equal(extended, received.Extended); Assert.Equal(fd, received.Fd);
        Assert.Equal(remote, received.Remote); Assert.Equal(brs, received.BitRateSwitch);
        Assert.Equal(new byte[] { 0, 1, 2 }, received.Data);
        api.ReadLength = 100;
        Assert.Equal(fd ? 64 : 8, backend.Read(0)!.Data.Length);
        api.ReadFlags = 0x20;
        Assert.Null(backend.Read(0));
        api.ReadStatus = -2;
        Assert.Null(backend.Read(0));
        api.ReadStatus = -1;
        Assert.Throws<CommunicationException>(() => backend.Read(0));
        api.Fail = "write";
        Assert.Throws<CommunicationException>(() => backend.Send(frame));
        backend.Close(); backend.Close();
        Assert.Equal(1, api.Closes); Assert.Equal(1, api.BusOffs);
        Assert.Equal("geschlossen", backend.Status());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("nominal")]
    [InlineData("data")]
    [InlineData("on")]
    public void Failed_initialization_closes_only_open_handles_and_can_be_retried(string operation)
    {
        var api = new Api { Fail = operation };
        using var backend = new KvaserBackend("0", api);
        var settings = new CanBusSettings(500000, true, 2000000, null, false);
        Assert.Throws<CommunicationException>(() => backend.Open(settings));
        Assert.Equal(operation == "open" ? 0 : 1, api.Closes);
        Assert.Equal("geschlossen", backend.Status());
        api.Fail = null;
        backend.Open(settings);
        Assert.Equal("Bus ein", backend.Status());
    }
}
