using Microsoft.Win32.SafeHandles;
using Testumgebung.Contracts;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class SerialTransportContractTests
{
    private sealed class Api : ISerialApi
    {
        public string? Fail;
        public bool PartialWrite;
        public SafeFileHandle Handle = new(new IntPtr(123), false);
        public int Opens, Cancels;
        public string? Name;
        public byte[] Written = [];
        public SerialTransport.Native.Dcb Settings;
        public SerialTransport.Native.CommTimeouts Timeouts;
        public List<uint> Purges = [];
        public Action? OnRead;
        public SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template)
        { Opens++; Name = name; return Fail == "open" ? new SafeFileHandle(new IntPtr(-1), false) : Handle; }
        public bool GetCommState(SafeFileHandle handle, ref SerialTransport.Native.Dcb dcb) => Fail != "get";
        public bool SetCommState(SafeFileHandle handle, ref SerialTransport.Native.Dcb dcb) { Settings = dcb; return Fail != "set"; }
        public bool SetCommTimeouts(SafeFileHandle handle, ref SerialTransport.Native.CommTimeouts timeouts) { Timeouts = timeouts; return true; }
        public bool PurgeComm(SafeFileHandle handle, uint flags) { Purges.Add(flags); return Fail != "purge"; }
        public bool ReadFile(SafeFileHandle handle, byte[] buffer, int count, out int read, IntPtr overlapped)
        { OnRead?.Invoke(); read = Math.Min(count, 3); for (var i = 0; i < read; i++) buffer[i] = (byte)(i + 1); return Fail != "read"; }
        public bool WriteFile(SafeFileHandle handle, byte[] buffer, int count, out int written, IntPtr overlapped)
        { Written = buffer.ToArray(); written = PartialWrite ? count - 1 : count; return Fail != "write"; }
        public bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped) { Cancels++; return true; }
    }

    public static IEnumerable<object[]> Configurations()
    {
        foreach (var parity in new[] { 'N', 'O', 'E', 'M', 'S' })
            foreach (var stop in new[] { 1.0, 1.5, 2.0 })
                foreach (var handshake in new[] { "Kein", "RTS-CTS", "XON-XOFF", "RTS XON" })
                    foreach (var rs485 in new[] { false, true }) yield return [parity, stop, handshake, rs485];
    }

    [Theory, MemberData(nameof(Configurations))]
    public async Task Opening_configures_parity_flow_control_and_closes_handle(char parity, double stop, string handshake, bool rs485)
    {
        var api = new Api();
        using var transport = new SerialTransport(new SerialSettings("COM_TEST", 115200, 7, parity, stop, handshake, rs485), api);
        Assert.False(transport.IsOpen); transport.DiscardInput(); Assert.Empty(api.Purges);
        await transport.OpenAsync(default); await transport.OpenAsync(default);
        Assert.True(transport.IsOpen); Assert.Equal(1, api.Opens); Assert.Equal(@"\\.\COM_TEST", api.Name);
        Assert.Equal((uint)115200, api.Settings.BaudRate); Assert.Equal((byte)7, api.Settings.ByteSize);
        Assert.Equal((byte)(parity switch { 'O' => 1, 'E' => 2, 'M' => 3, 'S' => 4, _ => 0 }), api.Settings.Parity);
        Assert.Equal((byte)(stop == 1.5 ? 1 : stop == 2 ? 2 : 0), api.Settings.StopBits);
        Assert.Equal(parity != 'N', (api.Settings.Flags & 2) != 0);
        Assert.Equal(handshake.Contains("RTS"), (api.Settings.Flags & 4) != 0);
        Assert.Equal(handshake.Contains("XON"), (api.Settings.Flags & (1 << 8)) != 0);
        Assert.Equal((uint)(rs485 ? 3 : handshake.Contains("RTS") ? 2 : 1), (api.Settings.Flags >> 12) & 3);
        Assert.Equal((byte)(handshake.Contains("XON") ? 0x11 : 0), api.Settings.XonChar);
        transport.DiscardInput(); Assert.Equal((uint)8, api.Purges.Last());
        await transport.WriteAsync(new byte[] { 8, 9 }, default); Assert.Equal(new byte[] { 8, 9 }, api.Written);
        Assert.Equal((uint)2000, api.Timeouts.WriteTotalTimeoutConstant);
        var buffer = new byte[4]; Assert.Equal(3, await transport.ReadAsync(buffer, TimeSpan.Zero, default));
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, buffer); Assert.Equal((uint)1, api.Timeouts.ReadTotalTimeoutConstant);
        transport.Close(); Assert.False(transport.IsOpen); Assert.True(api.Handle.IsClosed);
        transport.Close();
        await Assert.ThrowsAsync<CommunicationException>(() => transport.ReadAsync(buffer, TimeSpan.Zero, default));
        await Assert.ThrowsAsync<CommunicationException>(() => transport.WriteAsync(buffer, default));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("purge")]
    public async Task Setup_failure_does_not_publish_or_leak_the_handle(string failure)
    {
        var api = new Api { Fail = failure };
        using var transport = new SerialTransport(new SerialSettings("COM_TEST"), api);
        await Assert.ThrowsAsync<CommunicationException>(() => transport.OpenAsync(default));
        Assert.False(transport.IsOpen);
        if (failure != "open") Assert.True(api.Handle.IsClosed);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("partial")]
    public async Task Transfer_errors_are_reported(string failure)
    {
        var api = new Api();
        using var transport = new SerialTransport(new SerialSettings("COM_TEST"), api);
        await transport.OpenAsync(default);
        api.Fail = failure; api.PartialWrite = failure == "partial";
        if (failure == "read") await Assert.ThrowsAsync<CommunicationException>(() => transport.ReadAsync(new byte[4], TimeSpan.FromSeconds(1), default));
        else await Assert.ThrowsAsync<CommunicationException>(() => transport.WriteAsync(new byte[] { 1, 2 }, default));
        Assert.True(transport.IsOpen);
    }

    [Fact]
    public async Task Cancellation_interrupts_native_read_and_reports_cancellation()
    {
        var api = new Api { Fail = "read" };
        using var cancel = new CancellationTokenSource();
        using var transport = new SerialTransport(new SerialSettings("COM_TEST"), api);
        await transport.OpenAsync(default); api.OnRead = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ReadAsync(new byte[4], TimeSpan.FromSeconds(1), cancel.Token));
        Assert.Equal(1, api.Cancels);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.WriteAsync(new byte[] { 1 }, cancel.Token));
    }
}
