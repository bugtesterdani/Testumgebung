using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabContentBoundaryTests
{
    [Theory]
    [InlineData("[+:0,0]", "0,00")]
    [InlineData("[-:0,0]", "0,00")]
    [InlineData("[*:0,100]", "0,00")]
    [InlineData("[/:0,1]", "0,00")]
    [InlineData("[%:0,1]", "0,00")]
    [InlineData("[|:0,0]", "0")]
    [InlineData("[&:1,0]", "0")]
    [InlineData("[<:1,1]", "0")]
    [InlineData("[>:1,1]", "0")]
    [InlineData("[=:1,2]", "0")]
    [InlineData("[==:a,a]", "1")]
    [InlineData("[MOD10:0]", "0")]
    [InlineData("[MOD36:0]", "0")]
    [InlineData("[MOD43:%]", "%")]
    [InlineData("[P:0,.,-]", "0,-")]
    [InlineData("[+:0,0][R:m]", "0,00")]
    [InlineData("[+:0,0][D:1,0][C: ]", "0")]
    [InlineData("x[J:l0]", "x")]
    [InlineData("[LEN:😀]", "1")]
    [InlineData("[LOWER:İ]", "i")]
    [InlineData("[UPPER:ä]", "Ä")]
    [InlineData("[LTRIM:\t\t]", "")]
    [InlineData("[RTRIM:\t\t]", "")]
    [InlineData("[TRIM:\u00a0]", "\u00a0")]
    [InlineData("[HEX:ÿ]", "FF")]
    [InlineData("[U:$10FFFF]", "\U0010FFFF")]
    [InlineData("[U:GS]", "\u001d")]
    [InlineData("[SER:0]", "0")]
    [InlineData("[DAY02][S:ARABIC]", "٠١")]
    [InlineData("[DAY02][S:LATIN]", "01")]
    [InlineData("[SHA256:Empty]", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("[Empty]", "")]
    [InlineData("[Data,2]", "😀")]
    [InlineData("[Data,3,0]", "")]
    [InlineData("[B2B:D,H,Zero]", "0")]
    [InlineData("[SELECT:Pick,List,1,1]", "1")]
    [InlineData("[SPLIT:List,1]", "")]
    public void Offline_boundary_values_are_explicit(string source, string expected)
    {
        var fields = new Dictionary<string, CabPreviewValue> { ["Empty"] = new(""), ["Data"] = new("A😀"), ["Zero"] = new("0"), ["List"] = new("\u001dB") };
        var result = CabContentPreview.Evaluate(source, new() { ExampleTime = new(2024, 1, 1) }, fields);
        Assert.Equal(expected, result.Text);
        Assert.False(result.Value.Placeholder, string.Join(";", result.Issues.Select(i => i.Message)));
    }

    [Theory]
    [InlineData("[BIN:255]", "FF")]
    [InlineData("[BIN16B:65535]", "FFFF")]
    [InlineData("[BIN16L:65535]", "FFFF")]
    [InlineData("[BIN32B:4294967295]", "FFFFFFFF")]
    [InlineData("[BIN32L:4294967295]", "FFFFFFFF")]
    [InlineData("[BIN64B:18446744073709551615]", "FFFFFFFFFFFFFFFF")]
    [InlineData("[BIN64L:18446744073709551615]", "FFFFFFFFFFFFFFFF")]
    [InlineData("[BITFIELD:1:1]", "80")]
    public void Unsigned_binary_upper_bound_is_preserved(string source, string hex)
        => Assert.Equal(hex, Convert.ToHexString(CabContentPreview.Evaluate(source, new()).Value.Bytes!));

    [Fact]
    public void Date_offsets_expose_the_chosen_month_policy_and_support_named_values()
    {
        var settings = new CabPreviewSettings { ExampleTime = new(2024, 1, 31, 23, 0, 0) };
        var fields = new Dictionary<string, CabPreviewValue> { ["Hours"] = new("1,5") };
        Assert.Equal("003000", CabContentPreview.Evaluate("[ISOTIME:Hours]", settings, fields).Text);
        var fixedDays = CabContentPreview.Evaluate("[ISODATE:0,1]", settings);
        Assert.Equal("20240301", fixedDays.Text);
        Assert.Contains(fixedDays.Issues, i => i.Code == "preview-assumption");
        settings.MonthPolicy = CabPreviewMonthPolicy.CalendarMonths;
        Assert.Equal("20240229", CabContentPreview.Evaluate("[ISODATE:0,1]", settings).Text);
    }

    [Fact]
    public void Hidden_and_styled_values_remain_available_as_data()
    {
        var hidden = CabContentPreview.Evaluate("[DAY02][S:THAI][I]", new() { ExampleTime = new(2024, 1, 1) });
        Assert.Equal("๐๑", hidden.Text); Assert.Equal("01", hidden.Value.Text); Assert.True(hidden.Hidden);
        Assert.Equal("01", CabContentPreview.Evaluate("[DAY02][S:THAI]", new() { ExampleTime = new(2024, 1, 1) }, barcode: true).Text);
        var fields = new Dictionary<string, CabPreviewValue> { ["Device"] = new("unknown", Placeholder: true) };
        Assert.True(CabContentPreview.Evaluate("[==:Device,Device]", new(), fields).Value.Placeholder);
        Assert.True(CabContentPreview.Evaluate("x[I:Device]", new(), fields).Value.Placeholder);
    }

    [Theory]
    [InlineData("GR", "März")]
    [InlineData("GK", "Μάρτιος")]
    [InlineData("US", "March")]
    [InlineData("r", "März")]
    public void Locale_uses_country_codes_not_iso_language_codes(string code, string expected)
    {
        var source = System.Text.Encoding.UTF8.GetBytes("l " + code + "\rJ\rT 0,0,0,3,3;[month]\rA 1\r");
        Assert.Equal(expected, Assert.Single(CabDocumentPreview.Evaluate(CabParser.Parse(source), new() { ExampleTime = new(2024, 3, 20) })).Preview.Text);
    }
}
