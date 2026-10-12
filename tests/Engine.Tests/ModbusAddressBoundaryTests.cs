using Testumgebung.Contracts;

namespace Testumgebung.Engine.Tests;

public sealed class ModbusAddressBoundaryTests
{
    [Theory]
    [InlineData(400001, ModbusArea.HoldingRegister, 0)]
    [InlineData(465536, ModbusArea.HoldingRegister, 65535)]
    [InlineData(300001, ModbusArea.InputRegister, 0)]
    [InlineData(365536, ModbusArea.InputRegister, 65535)]
    [InlineData(40001, ModbusArea.HoldingRegister, 0)]
    [InlineData(49999, ModbusArea.HoldingRegister, 9998)]
    [InlineData(30001, ModbusArea.InputRegister, 0)]
    [InlineData(39999, ModbusArea.InputRegister, 9998)]
    [InlineData(10001, ModbusArea.DiscreteInput, 0)]
    [InlineData(19999, ModbusArea.DiscreteInput, 9998)]
    [InlineData(1, ModbusArea.Coil, 0)]
    [InlineData(9999, ModbusArea.Coil, 9998)]
    public void Traditional_address_ranges_are_one_based(int input, ModbusArea area, int address)
    {
        Assert.Equal((area, address), ModbusCodec.ParseAddress(input.ToString()));
        Assert.Equal((area, address), ModbusCodec.ParseAddress($"0x{input:X}"));
        foreach (var defaultArea in Enum.GetValues<ModbusArea>())
            Assert.Equal((defaultArea, input), ModbusCodec.ParseAddress(input.ToString(), defaultArea));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    [InlineData(20000)]
    [InlineData(50000)]
    [InlineData(300000)]
    [InlineData(365537)]
    [InlineData(465537)]
    public void Unclassified_addresses_require_an_explicit_area(int input)
        => Assert.Throws<FormatException>(() => ModbusCodec.ParseAddress(input.ToString()));

    public static IEnumerable<object[]> PrefixedAddresses()
    {
        foreach (var area in Enum.GetValues<ModbusArea>())
            foreach (var address in new[] { "0", "65535", "-1", "65536", "", "invalid", "2147483648" })
                yield return [area, address];
    }

    [Theory, MemberData(nameof(PrefixedAddresses))]
    public void Explicit_prefixes_validate_the_zero_based_address(ModbusArea area, string input)
    {
        var text = " " + ModbusCodec.AreaPrefix(area).ToLowerInvariant() + input + " ";
        if (input is "0" or "65535") Assert.Equal((area, int.Parse(input)), ModbusCodec.ParseAddress(text));
        else Assert.Throws<FormatException>(() => ModbusCodec.ParseAddress(text));
    }
}
