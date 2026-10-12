using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class MessagePackBoundaryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65535)]
    [InlineData(65536)]
    public void Variable_length_encodings_survive_round_trip_and_reject_truncation(int length)
    {
        var text = new string('x', length);
        var encoded = new MsgPackWriter().Str(text).ToArray();
        Assert.Equal(text, MsgPackReader.Parse(encoded));
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(encoded[..^1]));
        var bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        encoded = new MsgPackWriter().Bin(bytes).ToArray();
        Assert.Equal(bytes, Assert.IsType<byte[]>(MsgPackReader.Parse(encoded)));
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(encoded[..^1]));
        var list = Enumerable.Repeat<object?>(null, length).ToArray();
        encoded = new MsgPackWriter().Value(list).ToArray();
        Assert.Equal(length, Assert.IsType<object[]>(MsgPackReader.Parse(encoded)).Length);
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(encoded[..^1]));
        var map = Enumerable.Range(0, length).ToDictionary(i => i.ToString(), _ => (object?)true);
        encoded = new MsgPackWriter().Value(map).ToArray();
        Assert.Equal(map, Assert.IsType<Dictionary<string, object?>>(MsgPackReader.Parse(encoded)));
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(encoded[..^1]));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(int.MinValue)]
    [InlineData(-33)]
    [InlineData(-32)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(int.MaxValue)]
    [InlineData(long.MaxValue)]
    public void Signed_integer_boundaries_preserve_exact_value(long value)
    {
        var bytes = new MsgPackWriter().Int(value).ToArray();
        Assert.Equal(value, Assert.IsType<long>(MsgPackReader.Parse(bytes)));
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void Nested_values_and_optional_numbers_preserve_types()
    {
        var value = new Dictionary<string, object?> { ["values"] = new object?[] { null, false, true, 1, 2L, 1.5, "ä日本", new byte[] { 1, 2 } } };
        var parsed = Assert.IsType<Dictionary<string, object?>>(MsgPackReader.Parse(new MsgPackWriter().Value(value).ToArray()));
        var array = Assert.IsType<object[]>(parsed["values"]);
        Assert.Null(array[0]); Assert.Equal(false, array[1]); Assert.Equal(true, array[2]);
        Assert.Equal(1L, array[3]); Assert.Equal(2L, array[4]); Assert.Equal(1.5, array[5]);
        Assert.Equal("ä日本", array[6]); Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(array[7]));
        Assert.Null(MsgPackReader.Parse(new MsgPackWriter().Str(null).ToArray()));
        Assert.Null(MsgPackReader.Parse(new MsgPackWriter().OptDouble(null).ToArray()));
        Assert.Null(MsgPackReader.Parse(new MsgPackWriter().OptInt(null).ToArray()));
        Assert.Equal(1.25, MsgPackReader.Parse(new MsgPackWriter().OptDouble(1.25).ToArray()));
        Assert.Equal(5L, MsgPackReader.Parse(new MsgPackWriter().OptInt(5).ToArray()));
        Assert.Throws<ArgumentException>(() => new MsgPackWriter().Value(new object()));
    }

    [Theory]
    [InlineData("CCFF", 255L)]
    [InlineData("CDFFFF", 65535L)]
    [InlineData("CEFFFFFFFF", 4294967295L)]
    [InlineData("CF0000000000000001", 1L)]
    [InlineData("D0FF", -1L)]
    [InlineData("D1FFFF", -1L)]
    public void Reader_accepts_standard_integer_widths(string hex, long expected)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(expected, MsgPackReader.Parse(bytes));
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<FormatException>(() => MsgPackReader.Parse(bytes[..length]));
    }

    [Fact]
    public void Reader_rejects_overflow_unsupported_tags_and_non_string_map_keys()
    {
        Assert.Throws<OverflowException>(() => MsgPackReader.Parse(Convert.FromHexString("CFFFFFFFFFFFFFFFFF")));
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(new byte[] { 0xc1 }));
        Assert.Throws<FormatException>(() => MsgPackReader.Parse(new byte[] { 0x81, 1, 0xc0 }));
        Assert.Equal(1.0, MsgPackReader.Parse(Convert.FromHexString("CA3F800000")));
    }
}
