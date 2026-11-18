using System.Text;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CabLabelImportTests
{
    [Theory]
    [InlineData(203)]
    [InlineData(254)]
    [InlineData(300)]
    public void Existing_elements_and_binary_images_can_be_edited_and_reopened(int dpi)
    {
        var original = new LabelLayout { Dpi = dpi, GapMm = 2.5m };
        foreach (var kind in Enum.GetValues<LabelElementKind>())
            original.Elements.Add(new() { Kind = kind, X = 12, Y = 25, Size = 4,
                Value = kind is LabelElementKind.Gs1DataMatrix or LabelElementKind.Gs1Qr ? "(21){Kennung}" : "Pruefling {Kennung}  ",
                ImageWidth = 9, ImageHeight = 2, Bitmap = [0x1b, 0x80, 0xff, 0x00] });
        var bytes = CabLabelLayout.Render(original);
        var imported = CabLabelImport.Read(bytes, dpi);
        Assert.Equal(original.Elements.Count, imported.Elements.Count);
        Assert.Equal(2.5m, imported.GapMm);
        Assert.Equal(bytes, CabLabelLayout.Render(imported));
        imported.Elements[0].Value = "Edited {Kennung}";
        var reopened = CabLabelImport.Read(CabLabelLayout.Render(imported), dpi);
        Assert.Equal("Edited {Kennung}", reopened.Elements[0].Value);
        Assert.Equal(original.Elements.Single(item => item.Kind == LabelElementKind.Image).Bitmap,
            reopened.Elements.Single(item => item.Kind == LabelElementKind.Image).Bitmap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_image_downloads_accept_matching_content_and_reject_conflicts(bool conflict)
    {
        var layout = new LabelLayout { Elements =
        [
            new() { Kind = LabelElementKind.Image, X = 0, ImageWidth = 8, ImageHeight = 1, Bitmap = [0x1b] },
            new() { Kind = LabelElementKind.Image, X = 20, ImageWidth = 8, ImageHeight = 1, Bitmap = [0x1b] }
        ] };
        var bytes = CabLabelLayout.Render(layout);
        if (conflict)
        {
            var firstHeader = Encoding.ASCII.GetString(bytes, 0, bytes.Length).IndexOf("d BMP;", StringComparison.Ordinal);
            var secondHeader = Encoding.ASCII.GetString(bytes, 0, bytes.Length).IndexOf("d BMP;", firstHeader + 1, StringComparison.Ordinal);
            var bitmapStart = bytes.AsSpan(secondHeader).IndexOf(new byte[] { 0x1b, (byte)'.' }) + secondHeader + 2;
            bytes[bitmapStart + 62] = 0x1c;
            bytes[bitmapStart + 63] = 0;
            Assert.Throws<InvalidDataException>(() => CabLabelImport.Read(bytes));
            return;
        }

        var imported = CabLabelImport.Read(bytes);
        Assert.Equal(2, imported.Elements.Count);
        Assert.Equal(20, imported.Elements[1].X);
        Assert.Equal(imported.Elements[0].Bitmap, imported.Elements[1].Bitmap);
        Assert.Equal(bytes, CabLabelLayout.Render(imported));
    }

    [Theory]
    [InlineData("")]
    [InlineData("J\r")]
    [InlineData("J\rT 0,0,0,5,3;test\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rO R\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rT 0,0,90,5,3;test\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rI 0,0,0;MISSING\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rA 1\rJ\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rT 0,0,0,5,3;[DATE]\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rB 0,0,0,EAN13,3;123\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rB 0,0,0,CODE128,3,0.5;123\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rG 0,0,0;C:3,3\rA 1\r")]
    [InlineData("J\rS l1;0,0,40.5,43,60\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,39,60\rA 1\r")]
    [InlineData("J\rS l1;0,0,40,43,60\rT -1,0,0,5,3;x\rA 1\r")]
    public void Unsupported_or_incomplete_jobs_are_rejected_without_silent_content_loss(string source)
        => Assert.Throws<InvalidDataException>(() => CabLabelImport.Read(Encoding.UTF8.GetBytes(source)));

    [Fact]
    public void Cr_lf_and_lf_line_endings_are_accepted()
    {
        var source = "J\nS l1;0,0,40,43,60\nT 1,2,0,5,3;Text\nA 1\n";
        foreach (var ending in new[] { "\r", "\n", "\r\n" })
            Assert.Equal("Text", Assert.Single(CabLabelImport.Read(Encoding.UTF8.GetBytes(source.Replace("\n", ending))).Elements).Value);
    }

    [Fact]
    public void Truncated_and_invalid_binary_blocks_are_rejected()
    {
        foreach (var bytes in new byte[][] {
            Encoding.ASCII.GetBytes("d BMP;LOGO\r"),
            Encoding.ASCII.GetBytes("d BMP;LOGO\r\u001b.BM\u001b."),
            Encoding.ASCII.GetBytes("d BMP;LOGO\r\u001b.BM\u001bx"),
            Encoding.ASCII.GetBytes("d BMP;LOGO\r\u001b.BM")
        })
            Assert.Throws<InvalidDataException>(() => CabLabelImport.Read(bytes));
    }
}
