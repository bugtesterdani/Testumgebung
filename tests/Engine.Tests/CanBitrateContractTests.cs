using Testumgebung.Instruments;
using Testumgebung.Instruments.Communication;

namespace Testumgebung.Engine.Tests;

public sealed class CanBitrateContractTests
{
    [Theory]
    [InlineData(1000000, 0x14, -1)]
    [InlineData(500000, 0x1c, -2)]
    [InlineData(250000, 0x11c, -3)]
    [InlineData(125000, 0x31c, -4)]
    [InlineData(100000, 0x432f, -5)]
    [InlineData(50000, 0x472f, -7)]
    [InlineData(10000, 0x672f, -9)]
    public void Shared_classic_rates_use_vendor_constants(int rate, int pcan, int kvaser)
    {
        Assert.Equal(pcan, PcanBackend.BitrateCode(rate));
        Assert.Equal(kvaser, KvaserBackend.Bitrate(rate));
    }

    [Fact]
    public void Vendor_specific_rates_and_unsupported_rates_are_explicit()
    {
        Assert.Equal(0x16, PcanBackend.BitrateCode(800000));
        Assert.Equal(0x532f, PcanBackend.BitrateCode(20000));
        Assert.Equal(-6, KvaserBackend.Bitrate(62500));
        Assert.Equal(-8, KvaserBackend.Bitrate(83333));
        foreach (var rate in new[] { -1, 0, 12345, int.MaxValue })
        {
            Assert.Throws<InstrumentException>(() => PcanBackend.BitrateCode(rate));
            Assert.Throws<InstrumentException>(() => KvaserBackend.Bitrate(rate));
            Assert.Throws<InstrumentException>(() => KvaserBackend.FdNominal(rate));
            Assert.Throws<InstrumentException>(() => KvaserBackend.FdData(rate));
            Assert.Throws<InstrumentException>(() => PcanBackend.FdBitrate(rate, 2000000));
            Assert.Throws<InstrumentException>(() => PcanBackend.FdBitrate(500000, rate));
        }
    }

    public static IEnumerable<object[]> FdRates()
    {
        foreach (var nominal in new[] { 125000, 250000, 500000, 1000000 })
            foreach (var data in new[] { 1000000, 2000000, 4000000, 5000000, 8000000 })
                yield return [nominal, data];
    }

    [Theory, MemberData(nameof(FdRates))]
    public void Fd_timing_parameters_reconstruct_the_requested_rates(int nominal, int data)
    {
        var parameters = PcanBackend.FdBitrate(nominal, data).Split(',')
            .Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        var clock = parameters["f_clock_mhz"] * 1000000;
        Assert.Equal(nominal, clock / (parameters["nom_brp"] * (1 + parameters["nom_tseg1"] + parameters["nom_tseg2"])));
        Assert.Equal(data, clock / (parameters["data_brp"] * (1 + parameters["data_tseg1"] + parameters["data_tseg2"])));
        if (nominal is 500000 or 1000000)
            Assert.Equal(nominal == 500000 ? -1000 : -1001, KvaserBackend.FdNominal(nominal));
        if (data != 5000000)
            Assert.Equal(data switch { 1000000 => -1001, 2000000 => -1002, 4000000 => -1003, _ => -1004 }, KvaserBackend.FdData(data));
        Assert.Equal(-1000, KvaserBackend.FdData(500000));
    }
}
