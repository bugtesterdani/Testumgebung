using System.Buffers.Binary;
using System.Text;
using Testumgebung.Persistence.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class ComtradeBoundaryTests
{
    private static byte[] Config(string type, int rates, int digital, string id, bool multiplier = true)
    {
        var lines = new List<string> { "Station,Device,1999", $"{2 + digital},2A,{digital}D",
            $"1,{id},A,,V,2,1", $"2,{id},B,,A,3,-1" };
        for (var i = 0; i < digital; i++) lines.Add($"{i + 1},D{i},,,0");
        lines.Add("50"); lines.Add(rates.ToString());
        for (var i = 0; i < Math.Max(1, rates); i++) lines.Add($"{(rates == 0 ? 0 : 1000)},2");
        lines.AddRange(["01/01/2026,00:00:00", "01/01/2026,00:00:00", type]);
        if (multiplier) lines.Add("2");
        return Encoding.Latin1.GetBytes(string.Join('\n', lines));
    }

    public static IEnumerable<object[]> Formats()
    {
        foreach (var type in new[] { "ASCII", "BINARY", "BINARY32", "FLOAT32" })
            foreach (var rates in new[] { 0, 1, 2 })
                foreach (var digital in new[] { 0, 1, 17 })
                    foreach (var id in new[] { "U", "" }) yield return [type, rates, digital, id];
    }

    [Theory, MemberData(nameof(Formats))]
    public void Formats_preserve_scaling_missing_samples_and_time(string type, int rates, int digital, string id)
    {
        byte[] data;
        if (type == "ASCII") data = Encoding.Latin1.GetBytes("1,1000,4,5\n2,invalid,99999,\n");
        else
        {
            var width = type == "BINARY" ? 2 : 4;
            var record = 8 + 2 * width + 2 * ((digital + 15) / 16);
            data = new byte[2 * record];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1000);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(record + 4), uint.MaxValue);
            for (var row = 0; row < 2; row++)
                for (var channel = 0; channel < 2; channel++)
                {
                    var span = data.AsSpan(row * record + 8 + channel * width);
                    if (type == "BINARY") BinaryPrimitives.WriteInt16LittleEndian(span, row == 0 ? (short)(4 + channel) : short.MinValue);
                    else if (type == "BINARY32") BinaryPrimitives.WriteInt32LittleEndian(span, row == 0 ? 4 + channel : int.MinValue);
                    else BinaryPrimitives.WriteSingleLittleEndian(span, row == 0 ? 4 + channel : float.NaN);
                }
        }
        var table = Comtrade.Parse(Config(type, rates, digital, id), data, "fixture.cfg");
        Assert.Equal(2, table.Rows);
        Assert.Equal(.002, table.Data[0][0]);
        Assert.True(double.IsNaN(table.Data[0][1]));
        Assert.Equal(9, table.Data[1][0]);
        Assert.Equal(14, table.Data[2][0]);
        Assert.True(double.IsNaN(table.Data[1][1]));
        Assert.True(double.IsNaN(table.Data[2][1]));
        Assert.Equal(rates == 1 ? 1000d : (double?)null, table.SampleRate);
        Assert.Equal(id.Length == 0 ? new[] { "Zeit", "A1", "A2" } : new[] { "Zeit", "U", "U_2" }, table.Columns.Select(c => c.Name));
        Assert.Equal((rates > 1 ? 1 : 0) + (digital > 0 ? 1 : 0), table.Warnings.Count);
    }

    [Theory]
    [InlineData("ASCII", "1,0")]
    [InlineData("BINARY", "x")]
    [InlineData("BINARY32", "x")]
    [InlineData("FLOAT32", "x")]
    [InlineData("UNSUPPORTED", "")]
    public void Truncated_or_unsupported_records_are_rejected(string type, string data)
        => Assert.Throws<CurveImportException>(() => Comtrade.Parse(Config(type, 1, 0, "U"), Encoding.Latin1.GetBytes(data), "bad.cfg"));

    [Fact]
    public void Missing_multiplier_and_legacy_revision_use_defaults()
    {
        var bytes = Config("ASCII", 0, 0, "U", false);
        bytes = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("Station,Device,1999", "Station,Device"));
        var cfg = Comtrade.ParseConfig(bytes);
        Assert.Equal("1991", cfg.Revision);
        Assert.Equal(1, cfg.TimeMult);
        Assert.Empty(cfg.Rates);
        var empty = Comtrade.Parse(bytes, [], "empty.cfg");
        Assert.Equal(0, empty.Rows);
        Assert.Empty(empty.PreviewRows);
    }
}
