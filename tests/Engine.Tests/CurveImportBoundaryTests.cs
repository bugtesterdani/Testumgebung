using System.Text;
using Testumgebung.Persistence.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class CurveImportBoundaryTests
{
    public static IEnumerable<object[]> TimeColumns()
    {
        foreach (var name in new[] { "t", "time", "zeit", "Zeitstempel", "time_s", "t_ms", "t_us", "t_µs" })
            foreach (var unit in new[] { "s", "ms", "us", "µs", "ns", "min" })
                foreach (var delimiter in new[] { ';', '\t', ',', '|' })
                    yield return [name, unit, delimiter];
    }

    [Theory, MemberData(nameof(TimeColumns))]
    public void Imported_time_units_and_channel_mapping_produce_correct_curve(string name, string unit, char delimiter)
    {
        var text = $"# ignored comment\n{name} [{unit}]{delimiter}U_L 1 [V]{delimiter}status\n0{delimiter}1{delimiter}0\n1{delimiter}2{delimiter}1\n2{delimiter}3{delimiter}0\n";
        var table = Csv.Parse(Encoding.UTF8.GetBytes(text), "fixture.csv", delimiter);
        var settings = CurveImport.SuggestSettings(table);
        Assert.Equal(name, settings.TimeColumn);
        Assert.Equal("L1", Assert.Single(settings.Channels).TargetChannel);
        var factor = unit switch { "ms" => .001, "us" or "µs" => .000001, "ns" => .000000001, "min" => 60, _ => 1 };
        Assert.Equal(factor, settings.TimeFactor);
        var curve = CurveImport.ToCurve(table, settings, "curve", "Kurve", "1", "Tester", DateTimeOffset.UnixEpoch);
        Assert.Equal(new[] { 0, factor, 2 * factor }, curve.Times);
        Assert.Equal(1 / factor, curve.SampleRate);
        Assert.Equal(new[] { 1d, 2, 3 }, Assert.Single(curve.Channels).Samples);
        Assert.Contains("Qualitätscode", curve.Quality);
        var summary = Assert.Single(CurveImport.Summarize(table, settings));
        Assert.Equal(1, summary.Min);
        Assert.Equal(3, summary.Max);
        Assert.Contains("1 Warnung", summary.Quality);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# only comment")]
    [InlineData(" \n\n")]
    public void Empty_import_is_rejected(string text)
        => Assert.Throws<CurveImportException>(() => Csv.Parse(Encoding.UTF8.GetBytes(text), "empty.csv"));

    [Theory]
    [InlineData("channels")]
    [InlineData("duplicate")]
    [InlineData("time")]
    [InlineData("unknown-time")]
    [InlineData("descending")]
    [InlineData("rate")]
    [InlineData("zero-rate")]
    [InlineData("negative-rate")]
    [InlineData("missing-value")]
    [InlineData("missing-channel")]
    public void Invalid_mapping_is_rejected_with_a_useful_message(string scenario)
    {
        var data = scenario switch
        {
            "descending" => "t;U\n1;2\n0;3",
            "missing-value" => "t;U\n0;2\n1;invalid",
            _ => "t;U\n0;2\n1;3"
        };
        var table = Csv.Parse(Encoding.UTF8.GetBytes(data), "fixture.csv");
        var settings = CurveImport.SuggestSettings(table);
        settings = scenario switch
        {
            "channels" => settings with { Channels = [] },
            "duplicate" => settings with { Channels = [new("U", "U", "V"), new("U", "U", "V")] },
            "time" => settings with { TimeColumn = null },
            "unknown-time" => settings with { TimeColumn = "missing" },
            "rate" => settings with { Mode = TimeBaseMode.FixedRate, SampleRate = null },
            "zero-rate" => settings with { Mode = TimeBaseMode.FixedRate, SampleRate = 0 },
            "negative-rate" => settings with { Mode = TimeBaseMode.FixedRate, SampleRate = -1 },
            "missing-channel" => settings with { Channels = [new("missing", "U", "V")] },
            // A malformed signal must still be selectable so its missing samples are diagnosed.
            "missing-value" => settings with { Channels = [new("U", "U", "V")] },
            _ => settings
        };
        var error = Assert.Throws<CurveImportException>(() => CurveImport.ToCurve(table, settings, "curve", "Kurve", "1", "Tester", DateTimeOffset.UnixEpoch));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData("U;U\n1;2\n3;4", "U_2")]
    [InlineData(";U\n1;2\n3;4", "Spalte 1")]
    [InlineData("1;2\n3;4", "Spalte 1")]
    [InlineData("U (V);I\n1;2\n3;4", "U")]
    [InlineData("U;I\n[V];[A]\n1;2\n3;4", "U")]
    public void Header_variants_keep_numeric_columns_and_fixed_rate_samples(string input, string expectedColumn)
    {
        var table = Csv.Parse(Encoding.UTF8.GetBytes(input), "fixture.csv");
        Assert.Contains(table.Columns, c => c.Name == expectedColumn);
        var settings = CurveImport.SuggestSettings(table) with { SampleRate = 10 };
        var curve = CurveImport.ToCurve(table, settings, "curve", "Kurve", "1", "Tester", DateTimeOffset.UnixEpoch);
        Assert.Null(curve.Times);
        Assert.Equal(10, curve.SampleRate);
        Assert.Equal(2, curve.Channels.Count);
        Assert.All(CurveImport.Summarize(table, settings), c => Assert.Equal("—", c.Quality));
    }
}
