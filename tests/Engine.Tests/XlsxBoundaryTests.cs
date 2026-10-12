using System.IO.Compression;
using Testumgebung.Persistence.Curves;

namespace Testumgebung.Engine.Tests;

public sealed class XlsxBoundaryTests
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static byte[] Workbook(string cells, bool shared, bool absolute, bool references, string fault = "")
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Entry(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
            if (fault != "workbook") Entry("xl/workbook.xml", $"<workbook xmlns='{Ns}' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets>{(fault == "sheets" ? "" : "<sheet name='Messung' r:id='r1'/>")}</sheets></workbook>");
            if (fault != "relations") Entry("xl/_rels/workbook.xml.rels", $"<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>{(fault == "target" ? "" : $"<Relationship Id='r1' Target='{(absolute ? "/xl/worksheets/sheet1.xml" : "worksheets/sheet1.xml")}'/>")}</Relationships>");
            if (fault != "worksheet")
            {
                var r = references ? " r='1'" : "";
                Entry("xl/worksheets/sheet1.xml", $"<worksheet xmlns='{Ns}'><sheetData>{(fault == "empty" ? "" : $"<row{r}>{cells}</row><row><c><v>2</v></c><c><v>4</v></c></row>")}</sheetData></worksheet>");
            }
            if (shared) Entry("xl/sharedStrings.xml", $"<sst xmlns='{Ns}'><si><t>U</t></si></sst>");
        }
        return bytes.ToArray();
    }

    public static IEnumerable<object[]> CellVariants()
    {
        foreach (var cell in new[] { "<c><v>1</v></c>", "<c t='inlineStr'><is><t>U</t></is></c>", "<c t='s'><v>0</v></c>", "<c t='s'><v>-1</v></c>", "<c t='s'><v>99</v></c>", "<c t='s'><v>invalid</v></c>", "<c t='s'/>", "<c t='b'><v>1</v></c>", "<c t='b'><v>0</v></c>", "<c/>", "<c r='B1'><v>1</v></c>" })
            foreach (var shared in new[] { false, true })
                foreach (var absolute in new[] { false, true })
                    foreach (var references in new[] { false, true })
                        yield return [cell, shared, absolute, references];
    }

    [Theory, MemberData(nameof(CellVariants))]
    public void Cell_representations_and_relationship_paths_are_read_without_losing_numeric_data(string cell, bool shared, bool absolute, bool references)
    {
        var bytes = Workbook(cell + "<c><v>3</v></c>", shared, absolute, references);
        Assert.Equal(new[] { "Messung" }, Xlsx.SheetNames(bytes));
        var first = Xlsx.Parse(bytes, "fixture.xlsx");
        var named = Xlsx.Parse(bytes, "fixture.xlsx", "Messung");
        Assert.Equal(first.Columns, named.Columns);
        Assert.Contains("Messung", named.SourceName);
        Assert.True(first.Rows > 0);
        Assert.Contains(first.Data, values => values.Any(v => v == 4));
        Assert.Throws<CurveImportException>(() => Xlsx.Parse(bytes, "fixture.xlsx", "missing"));
    }

    [Theory]
    [InlineData("workbook")]
    [InlineData("sheets")]
    [InlineData("relations")]
    [InlineData("target")]
    [InlineData("worksheet")]
    [InlineData("empty")]
    public void Missing_workbook_parts_are_diagnosed(string fault)
        => Assert.Throws<CurveImportException>(() => Xlsx.Parse(Workbook("", false, false, false, fault), "broken.xlsx"));

    [Fact]
    public void Non_zip_input_is_diagnosed()
        => Assert.Throws<CurveImportException>(() => Xlsx.Parse([1, 2, 3], "broken.xlsx"));
}
