using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class ProtocolDiagnosticContractTests
{
    public static IEnumerable<object[]> Codes() => Enumerable.Range(0, 256).Select(i => new object[] { (byte)i });

    [Theory, MemberData(nameof(Codes))]
    public void Protocol_errors_keep_numeric_codes_and_readable_diagnostics(byte code)
    {
        Assert.False(string.IsNullOrWhiteSpace(DoIpTransport.DescribeActivation(code)));
        Assert.False(string.IsNullOrWhiteSpace(DoIpTransport.DescribeNack(code)));
        Assert.Equal(code is 0x10 or 0x11, DoIpTransport.DescribeActivation(code).StartsWith("erfolgreich"));
        var uds = new UdsNegativeResponseException(0x22, code);
        Assert.Equal((byte)0x22, uds.Service); Assert.Equal(code, uds.Code);
        Assert.Contains($"NRC 0x{code:X2}", uds.Message);
        Assert.Contains(Uds.Describe(code), uds.Message);
        Assert.False(string.IsNullOrWhiteSpace(Uds.Describe(code)));
        if (code is >= 0x81 and <= 0x8f) Assert.Contains("Bedingung", Uds.Describe(code));
        var modbus = new ModbusException(3, code);
        Assert.Equal((byte)3, modbus.Function); Assert.Equal(code, modbus.Code);
        Assert.Contains(ModbusException.Describe(code), modbus.Message);
        Assert.False(string.IsNullOrWhiteSpace(ModbusException.Describe(code)));
    }
}
