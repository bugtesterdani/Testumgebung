using Testumgebung.Domain.Curves;
using Testumgebung.Persistence.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class CurveImportQualityBoundaryTests
{
    [Theory]
    [InlineData("empty", false)]
    [InlineData("single", true)]
    [InlineData("bad-time", false)]
    [InlineData("good-quality", true)]
    [InlineData("warning-quality", false)]
    [InlineData("table-rate", true)]
    public void Import_preserves_quality_warnings_and_filters_missing_time_rows(string mode, bool warning)
    {
        double[] times = mode switch { "empty" => [], "single" => [5], "bad-time" => [double.NaN, 5, 6], _ => [5, 6] };
        var values = Enumerable.Range(0, times.Length).Select(i => (double)(10 + i)).ToArray(); var quality = times.Select(_ => mode == "warning-quality" ? 1d : double.NaN).ToArray();
        var table = new ImportTable { Format = "Ergebnisdatei", SourceName = "fixture.tres", Sha256 = new string('0', 64), Columns = [new(0, "t", "s", true, 0, 1), new(1, "U", "V", true, 0, 20), new(2, "Quality", null, true, 0, 1)], Data = [times, values, quality], PreviewRows = [], TimeColumn = "t", QualityColumn = "Quality", SampleRate = 10 };
        if (warning) table.Warnings.Add("fixture warning");
        var settings = CurveImport.SuggestSettings(table); if (mode == "table-rate") settings = settings with { Mode = TimeBaseMode.FixedRate, SampleRate = null, QualityColumn = null };
        var curve = CurveImport.ToCurve(table, settings, "id", "Curve", "1", "tester", DateTimeOffset.UnixEpoch); Assert.Equal(CurveOrigin.Measurement, curve.Origin); Assert.Equal(mode == "bad-time" ? values[1..] : values, Assert.Single(curve.Channels).Samples);
        if (mode == "table-rate") { Assert.Equal(10, curve.SampleRate); Assert.Null(curve.Times); }
        else { Assert.Equal(mode == "bad-time" ? 2 : times.Length, curve.Times!.Length); if (curve.Times.Length > 0) Assert.Equal(0, curve.Times[0]); }
        Assert.Equal(warning, curve.Quality?.Contains("fixture warning") == true); if (mode == "warning-quality") Assert.Contains("Qualitätscode", curve.Quality);
        var summary = Assert.Single(CurveImport.Summarize(table, settings)); Assert.Equal(mode == "empty" ? 0 : values.Min(), summary.Min); Assert.Equal(mode == "table-rate" ? "—" : mode == "warning-quality" ? "2 Warnung(en)" : "0 (keine Warnungen)", summary.Quality);
    }
}
