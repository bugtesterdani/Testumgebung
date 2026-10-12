using Testumgebung.Contracts;
using Testumgebung.Persistence.Reports;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Engine.Tests;

public sealed class ReportCsvBoundaryTests
{
    private sealed class EmptyText { public override string? ToString() => null; }
    [Fact]
    public void Csv_preserves_types_escapes_cells_and_uses_german_numbers()
    {
        var date = new DateTime(2026, 1, 2, 3, 4, 5);
        var offset = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        var csv = ResultAnalysis.Csv([new object?[] { null, double.NaN, 1.25, 2.5f, 3L, 4, date, offset, true, false, ResultRating.Pass, new EmptyText(), "a;b", "a\"b", "a\nb", "a\rb" }]);
        Assert.Equal(";;1,25;2,5;3;4;02.01.2026 03:04:05;02.01.2026 03:04:05;ja;nein;PASS;;\"a;b\";\"a\"\"b\";\"a\nb\";\"a\rb\"" + Environment.NewLine, csv);
        var directory = Path.Combine(Path.GetTempPath(), "csv-contract-" + Guid.NewGuid());
        try
        {
            var path = Path.Combine(directory, "nested", "report.csv"); ResultAnalysis.SaveCsv(path, csv);
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(path)[..3]); Assert.Equal(csv, File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(ResultEventKind.Step, "Schritt")]
    [InlineData(ResultEventKind.Group, "Gruppe")]
    [InlineData(ResultEventKind.GroupNotEntered, "nicht betreten")]
    [InlineData(ResultEventKind.OperatorShown, "Anzeige")]
    [InlineData(ResultEventKind.OperatorAnswer, "Werkereingabe")]
    [InlineData(ResultEventKind.Diagnostic, "Hinweis")]
    [InlineData(ResultEventKind.Development, "Entwicklung")]
    [InlineData((ResultEventKind)999, "Werkerabbruch")]
    public void Event_categories_have_readable_export_labels(ResultEventKind kind, string expected)
        => Assert.Equal(expected, ResultAnalysis.KindText(kind));
}
