using System.Text;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Engine.Tests;

public sealed class CabContentTests
{
    private static CabPreviewSettings Settings => new() { ExampleTime = new(2024, 3, 20, 13, 45, 30) };
    private static readonly Dictionary<string, CabPreviewValue> Fields = new(StringComparer.Ordinal)
    {
        ["Data"] = new("ABCDE"), ["Num"] = new("12,5"), ["List"] = new("Red\u001dBlue"), ["HexData"] = new("FF")
    };

    public static IEnumerable<object[]> DateCases()
    {
        string[] names = ["H12", "H24", "H012", "H024", "ISOTIME", "MIN", "SEC", "TIME", "XM", "DATE", "DAY", "DAY02", "DOFY", "ISODATE", "ISOORDINAL", "WDAY", "wday", "wday2", "wday3", "ISOWDAY", "WEEK", "WEEK02", "OWEEK:0", "mon", "month", "MONTH", "MONTH02", "YY", "YYYY", "JYEAR", "JDAY", "JDAY02", "JMONTH", "JMONTH02", "JDOFY", "jmonth", "JWDAY", "SYEAR"];
        string[] expected = ["1", "13", "01", "13", "134530", "45", "30", "13:45:30", "pm", "20.03.2024", "20", "20", "080", "20240320", "2024080", "3", "Mittwoch", "Mi", "Mit", "3", "12", "12", "12", "Mär", "März", "3", "03", "24", "2024", "1403", "1", "01", "1", "01", "001", "فروردین", "4", "2567"];
        string[] boundary = ["12", "0", "12", "00", "000000", "00", "00", "00:00:00", "am", "01.01.2024", "1", "01", "001", "20240101", "2024001", "1", "Montag", "Mo", "Mon", "1", "1", "01", "1", "Jan", "Januar", "1", "01", "24", "2024", "1402", "11", "11", "10", "10", "287", "دی", "2", "2567"];
        for (var i = 0; i < names.Length; i++) yield return [names[i], expected[i], boundary[i]];
    }

    [Theory]
    [MemberData(nameof(DateCases))]
    public void Calendar_fields_have_expected_values_and_midnight_boundary(string name, string expected, string boundary)
    {
        Assert.Equal(expected, CabContentPreview.Evaluate("[" + name + "]", Settings).Text);
        var settings = Settings; settings.ExampleTime = new(2024, 1, 1);
        Assert.Equal(boundary, CabContentPreview.Evaluate("[" + name + "]", settings).Text);
    }

    [Theory]
    [InlineData("[+:2,3]", "5,00")]
    [InlineData("[-:2,3]", "-1,00")]
    [InlineData("[*:2,3]", "6,00")]
    [InlineData("[/:7,2]", "3,50")]
    [InlineData("[%:7,2]", "1,00")]
    [InlineData("[|:0,2]", "1")]
    [InlineData("[&:2,3]", "1")]
    [InlineData("[<:2,3]", "1")]
    [InlineData("[>:2,3]", "0")]
    [InlineData("[=:2,2.0]", "1")]
    [InlineData("[==:A,a]", "0")]
    [InlineData("[MOD10:12345]", "7")]
    [InlineData("[MOD36:AZ]", "9")]
    [InlineData("[MOD43:AZ]", "2")]
    [InlineData("[P:1200,.,-]", "1.200,-")]
    [InlineData("[*:2.333,1][R:n]", "2,33")]
    [InlineData("[*:2.333,1][R:u]", "2,34")]
    [InlineData("[*:2.339,1][R:d]", "2,33")]
    [InlineData("[*:2.335,1][R:m]", "2,34")]
    [InlineData("[+:1,1][D:4,0][C: ]", "   2")]
    [InlineData("hello[J:r40]", "hello")]
    [InlineData("[LEN:Data]", "5")]
    [InlineData("[LOWER:AB]", "ab")]
    [InlineData("[UPPER:ab]", "AB")]
    [InlineData("[LTRIM: \tab ]", "ab ")]
    [InlineData("[RTRIM: ab \t]", " ab")]
    [InlineData("[TRIM: \tab \t]", "ab")]
    [InlineData("[Data]", "ABCDE")]
    [InlineData("[Data,2,3]", "BCD")]
    [InlineData("[SELECT:Pick,List,2,1]", "2")]
    [InlineData("[SPLIT:List,2]", "Blue")]
    [InlineData("[SER:0007]", "0007")]
    [InlineData("[HEX:ĀA]", "0041")]
    [InlineData("[B2B:H,D,HexData]", "255")]
    [InlineData("[U:$20AC]", "€")]
    [InlineData("[SHA256:abc]", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData("[DAY02][S:THAI]", "๒๐")]
    public void Offline_functions_have_expected_values(string source, string expected)
    {
        var result = CabContentPreview.Evaluate(source, Settings, Fields);
        Assert.Equal(expected, result.Text);
        Assert.False(result.Value.Placeholder, string.Join(";", result.Issues.Select(i => i.Message)));
    }

    [Theory]
    [InlineData("[BIN:0,255]", "00FF")]
    [InlineData("[BIN16B:258]", "0102")]
    [InlineData("[BIN16L:258]", "0201")]
    [InlineData("[BIN32B:258]", "00000102")]
    [InlineData("[BIN32L:258]", "02010000")]
    [InlineData("[BIN64B:258]", "0000000000000102")]
    [InlineData("[BIN64L:258]", "0201000000000000")]
    [InlineData("[BITFIELD:3,2:5,3]", "B8")]
    public void Binary_functions_return_bytes_in_documented_order(string source, string expected)
        => Assert.Equal(expected, Convert.ToHexString(CabContentPreview.Evaluate(source, Settings).Value.Bytes!));

    [Theory]
    [InlineData("[/:1,0]")]
    [InlineData("[BIN:256]")]
    [InlineData("[BIN16B:65536]")]
    [InlineData("[BIN16L:-1]")]
    [InlineData("[BIN32B:4294967296]")]
    [InlineData("[BIN32L:4294967296]")]
    [InlineData("[BIN64B:18446744073709551616]")]
    [InlineData("[BIN64L:-1]")]
    [InlineData("[BITFIELD:0:1]")]
    [InlineData("[BITFIELD:2:4]")]
    [InlineData("[Data,0]")]
    [InlineData("[SPLIT:List,3]")]
    [InlineData("[SELECT:Pick,List,3,1]")]
    [InlineData("[MOD10:A]")]
    [InlineData("[MOD36:-]")]
    [InlineData("[MOD43:a]")]
    [InlineData("[U:$D800]")]
    [InlineData("[B2B:U:xx,D,Data]")]
    [InlineData("[SER:1,1,0]")]
    [InlineData("[SER:]")]
    [InlineData("[WTAG:,4,1]")]
    public void Undefined_boundaries_are_diagnosed_without_silent_coercion(string source)
    {
        var result = CabContentPreview.Evaluate(source, Settings, Fields);
        Assert.True(result.Value.Placeholder);
        Assert.Contains(result.Issues, i => i.Code is "preview-value" or "content-parameter");
    }

    [Fact]
    public void Literal_brackets_concatenation_nesting_and_case_are_distinct()
    {
        var result = CabContentPreview.Evaluate("[unknown][day][DAY][MONTH]", Settings);
        Assert.Equal("[unknown][day]203", result.Text);
        Assert.Equal(2, result.Syntax.Fields.Count);
        Assert.Equal(2, result.Issues.Count(i => i.Code == "content-literal"));
        Assert.Contains(CabContentParser.Parse("[+:1,[DAY]]").Issues, i => i.Code == "content-nesting");
        Assert.Contains(CabContentParser.Parse("[DATE").Issues, i => i.Code == "content-unclosed");
    }

    [Fact]
    public void Invisible_fields_keep_data_and_alignment_is_physical_metadata()
    {
        var result = CabContentPreview.Evaluate("secret[I][J:c25]", Settings);
        Assert.True(result.Hidden); Assert.Equal("secret", result.Value.Text);
        Assert.Equal("c", result.Alignment); Assert.Equal(25m, result.AlignmentLength);
        Assert.True(CabContentPreview.Evaluate("x[I:1]", Settings).Hidden);
        var settings = Settings; settings.VisibilityPolicy = CabPreviewVisibilityPolicy.TablePrintsWhenTrue;
        Assert.False(CabContentPreview.Evaluate("x[I:1]", settings).Hidden);
    }

    [Fact]
    public void Source_highlights_exclude_intervening_controls_and_follow_utf8_characters()
    {
        var bytes = Encoding.UTF8.GetBytes("J\rT 0,0,0,3,3;ä[DA\u001bsTE]\rA 1\r");
        var document = CabParser.Parse(bytes);
        var content = Assert.Single(CabContentValidation.Bind(document, CabValidator.Validate(document).Commands));
        Assert.Equal("ä[DATE]", content.Source.Text);
        var field = Assert.Single(content.Syntax.Fields);
        var ranges = content.Source.Ranges(field.Offset, field.Length);
        Assert.Equal(2, ranges.Count);
        Assert.Equal("[DATE]", Encoding.UTF8.GetString(ranges.SelectMany(document.Bytes).ToArray()));
        Assert.Equal(bytes, document.Write());
    }
}
