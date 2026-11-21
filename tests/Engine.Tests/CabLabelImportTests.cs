using System.Text;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CabLabelImportTests
{
    [Fact]
    public void Editing_a_projected_field_preserves_unknown_commands_formatting_and_other_jobs()
    {
        var source = "; keep\r\nJ\rS l1;0,0,40,43,60\rO R\r  T 01.000,2,0,5,3;old,;[DATE]\n"
            + "T 4,5,90,3,5;rotated\rA 5\rJ\rS e;0,0,5,5,5\rA 9\runknown\n";
        var imported = CabLabelImport.Read(Encoding.UTF8.GetBytes(source));
        var item = Assert.Single(imported.Elements);
        item.Value = "new [DAY]";
        Assert.Equal(source.Replace("old,;[DATE]", "new [DAY]", StringComparison.Ordinal),
            Encoding.UTF8.GetString(CabLabelLayout.Render(imported)));
        var persisted = LabelLayout.Load(imported.Save());
        Assert.Equal(CabLabelLayout.Render(imported), CabLabelLayout.Render(persisted));
        item.X += 1;
        Assert.Contains("\nT 4,5,90,3,5;rotated\rA 5", Encoding.UTF8.GetString(CabLabelLayout.Render(imported)));
    }

    [Fact]
    public void Codepage_edits_do_not_convert_unrelated_bytes_or_silently_replace_characters()
    {
        var prefix = Encoding.ASCII.GetBytes("\u001bowindows-1252;\rJ\rS l1;0,0,40,43,60\rT 0,0,0,5,3;");
        var bytes = prefix.Concat(new byte[] { 0xe4 }).Concat(Encoding.ASCII.GetBytes("\rA 1\r")).ToArray();
        var imported = CabLabelImport.Read(bytes);
        var item = Assert.Single(imported.Elements);
        Assert.Equal("ä", item.Value);
        Assert.Equal(bytes, CabLabelLayout.Render(imported));
        item.Value = "ö";
        var expected = bytes.ToArray(); expected[prefix.Length] = 0xf6;
        Assert.Equal(expected, CabLabelLayout.Render(imported, out var offset));
        Assert.Equal(expected.Length, offset);
        item.Value = "漢";
        Assert.Throws<InvalidDataException>(() => CabLabelLayout.Render(imported));
    }

    [Fact]
    public void Canvas_edits_keep_downloads_and_can_add_delete_and_resize_objects()
    {
        var original = new LabelLayout { Elements = [new() { Kind = LabelElementKind.Image, ImageWidth = 8, ImageHeight = 1, Bitmap = [0x1b] }, new() { Value = "old" }] };
        var bytes = CabLabelLayout.Render(original);
        var imported = CabLabelImport.Read(bytes);
        imported.Elements[0].X = 10;
        var moved = CabLabelLayout.Render(imported);
        var headerLength = Encoding.Latin1.GetString(bytes).IndexOf("J\r", StringComparison.Ordinal);
        Assert.Equal(bytes[..headerLength], moved[..headerLength]);
        imported.Elements.RemoveAt(1);
        imported.Elements.Add(new() { Value = "added" });
        imported.WidthMm = 70; imported.HeightMm = 50; imported.GapMm = 0;
        var rendered = CabLabelLayout.Render(imported);
        var reopened = CabLabelImport.Read(rendered);
        Assert.Equal(70, reopened.WidthMm);
        Assert.Equal(50, reopened.HeightMm);
        Assert.Equal(0m, reopened.GapMm);
        Assert.Equal(2, reopened.Elements.Count);
        Assert.Equal("added", reopened.Elements[1].Value);
        Assert.Equal(rendered, CabLabelLayout.Render(reopened));
        imported.Dpi = 300;
        Assert.Throws<InvalidDataException>(() => CabLabelLayout.Render(imported));
    }

    [Fact]
    public void All_projected_object_edits_are_reopened_and_new_download_framing_uses_file_ending()
    {
        var original = new LabelLayout();
        foreach (var kind in Enum.GetValues<LabelElementKind>())
            original.Elements.Add(new() { Kind = kind, X = 10, Y = 15, Size = 3, Length = 30,
                Value = kind is LabelElementKind.Gs1DataMatrix or LabelElementKind.Gs1Qr ? "(21)123" : "123",
                ImageWidth = 8, ImageHeight = 1, Bitmap = [0x1b] });
        var imported = CabLabelImport.Read(CabLabelLayout.Render(original));
        foreach (var item in imported.Elements)
        {
            item.X += 3; item.Y += 4; item.Size += 1; item.Length += 5;
            if (item.Kind == LabelElementKind.Text) item.Value = "[DATE]";
            if (item.Kind == LabelElementKind.Image) { item.ImageWidth = 9; item.Bitmap = [0x80, 0]; }
        }
        var reopened = CabLabelImport.Read(CabLabelLayout.Render(imported));
        Assert.Equal(imported.Elements.Count, reopened.Elements.Count);
        for (var i = 0; i < reopened.Elements.Count; i++)
        {
            Assert.Equal(imported.Elements[i].X, reopened.Elements[i].X);
            Assert.Equal(imported.Elements[i].Y, reopened.Elements[i].Y);
        }
        Assert.Equal("[DATE]", reopened.Elements[0].Value);
        Assert.Equal(new byte[] { 0x80, 0 }, reopened.Elements.Single(e => e.Kind == LabelElementKind.Image).Bitmap);
        var lf = CabLabelImport.Read(Encoding.UTF8.GetBytes("J\nS e;0,0,40,40,60\nA 1\n"));
        lf.Elements.Add(new() { Kind = LabelElementKind.Image, ImageWidth = 8, ImageHeight = 1, Bitmap = [0x0d] });
        var bytes = CabLabelLayout.Render(lf);
        Assert.StartsWith("d BMP;", Encoding.Latin1.GetString(bytes));
        Assert.DoesNotContain((byte)'\r', bytes.TakeWhile(b => b != 0x1b));
        Assert.Equal(bytes, CabLabelLayout.Render(CabLabelImport.Read(bytes)));
        var changedKind = CabLabelImport.Read(CabLabelLayout.Render(new LabelLayout { Elements = [new()] }));
        changedKind.Elements[0].Kind = LabelElementKind.Line;
        Assert.Equal(LabelElementKind.Line, Assert.Single(CabLabelImport.Read(CabLabelLayout.Render(changedKind)).Elements).Kind);
        var raw = CabLabelImport.Read("raw"u8.ToArray());
        raw.Elements.Add(new());
        Assert.Throws<InvalidDataException>(() => CabLabelLayout.Render(raw));
    }

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
    public void Repeated_image_downloads_preserve_even_conflicting_original_bytes(bool conflict)
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
            Assert.Equal(bytes, CabLabelLayout.Render(CabLabelImport.Read(bytes)));
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
    public void Unsupported_or_incomplete_jobs_are_retained_without_silent_content_loss(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        Assert.Equal(bytes, CabLabelLayout.Render(CabLabelImport.Read(bytes)));
    }

    [Fact]
    public void Cr_lf_and_lf_line_endings_are_accepted()
    {
        var source = "J\nS l1;0,0,40,43,60\nT 1,2,0,5,3;Text\nA 1\n";
        foreach (var ending in new[] { "\r", "\n", "\r\n" })
            Assert.Equal("Text", Assert.Single(CabLabelImport.Read(Encoding.UTF8.GetBytes(source.Replace("\n", ending))).Elements).Value);
    }

    [Fact]
    public void Truncated_and_invalid_binary_blocks_remain_byte_identical()
    {
        foreach (var bytes in new byte[][] {
            Encoding.ASCII.GetBytes("d BMP;LOGO\r"),
            Encoding.ASCII.GetBytes("d BMP;LOGO\r\u001b.BM\u001b."),
            Encoding.ASCII.GetBytes("d BMP;LOGO\r\u001b.BM\u001bx"),
            Encoding.ASCII.GetBytes("d BMP;LOGO\r\u001b.BM")
        })
            Assert.Equal(bytes, CabLabelLayout.Render(CabLabelImport.Read(bytes)));
    }
}
