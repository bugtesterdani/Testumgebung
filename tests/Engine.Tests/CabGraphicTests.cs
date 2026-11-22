using System.Text;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Engine.Tests;

public sealed class CabGraphicTests
{
    public static IEnumerable<object[]> Entries() => CabGraphicCatalog.Definitions.Select(d => new object[] { d.Id });
    public static IEnumerable<object[]> BarcodeEntries() => CabGraphicCatalog.Definitions.Where(d => d.Kind == "barcode").Select(d => new object[] { d.Id });

    [Theory]
    [MemberData(nameof(BarcodeEntries))]
    public void Every_barcode_profile_produces_real_modules_or_an_explicit_empty_fallback(string id)
    {
        var template = CabPropertyTemplates.Get(id);
        var editor = new CabSourceEditor(template.Bytes);
        var item = CabGraphicFactory.Build(editor, CabLabelImport.Read(template.Bytes), 0).Items.First(i => i.Kind == CabGraphicKind.Barcode);
        var symbol = Assert.IsType<CabBarcodeSymbol>(item.Barcode);
        Assert.True(symbol.Columns > 0 && symbol.Rows > 0);
        if (symbol.IsEncoded)
        {
            Assert.Equal(symbol.Columns * symbol.Rows, symbol.Modules!.Length);
            Assert.Contains(true, symbol.Modules); Assert.Contains(false, symbol.Modules);
            Assert.Contains("Echte Symbolerzeugung", symbol.Note);
        }
        else
        {
            Assert.Null(symbol.Modules);
            Assert.Contains("Vorschaufläche", symbol.Note);
        }
        Assert.Equal(template.Bytes, editor.Document.Write());
    }

    [Theory]
    [InlineData("B 1,2,0,CODE128,10,.3;ABC123", "ABC123")]
    [InlineData("B 1,2,0,QRCODE,0.5;ABC123", "ABC123")]
    public void Encoded_preview_can_be_decoded_by_the_independent_reader(string line, string expected)
    {
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r"));
        Assert.False(editor.Validation.HasErrors);
        var item = Assert.Single(CabGraphicFactory.Build(editor, new LabelLayout(), 0).Items);
        var symbol = Assert.IsType<CabBarcodeSymbol>(item.Barcode);
        Assert.True(symbol.IsEncoded, symbol.Note);
        const int scale = 4, quiet = 40;
        var width = symbol.Columns * scale + quiet * 2;
        var height = Math.Max(symbol.Rows * scale, 100) + quiet * 2;
        var pixels = Enumerable.Repeat((byte)255, width * height * 3).ToArray();
        for (var y = quiet; y < height - quiet; y++)
            for (var x = quiet; x < width - quiet; x++)
            {
                var row = symbol.Rows == 1 ? 0 : (y - quiet) / scale;
                if (row >= symbol.Rows || !symbol.Modules![row * symbol.Columns + (x - quiet) / scale]) continue;
                var offset = (y * width + x) * 3; pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
            }
        var source = new ZXing.RGBLuminanceSource(pixels, width, height, ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
        var decoded = new ZXing.MultiFormatReader().decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(source)));
        Assert.NotNull(decoded); Assert.Equal(expected, decoded.Text);
    }

    [Fact]
    public void Graphic_catalog_contains_all_50_definitions_and_43_barcode_profiles()
    {
        Assert.Equal(50, CabGraphicCatalog.Definitions.Count);
        Assert.Equal(43, CabGraphicCatalog.Definitions.Count(d => d.Kind == "barcode"));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Every_graphic_template_has_finite_transformed_bounds_and_byte_preserving_gestures(string id)
    {
        var template = CabPropertyTemplates.Get(id);
        var editor = new CabSourceEditor(template.Bytes);
        Assert.False(editor.Validation.HasErrors);
        var layout = CabLabelImport.Read(template.Bytes);
        var scene = CabGraphicFactory.Build(editor, layout, 0);
        var item = scene.Items.First(i => i.Command.Definition.Catalog.Id == template.DefinitionId || i.Command.BaseCatalogId == template.DefinitionId);
        Assert.True(double.IsFinite(item.Bounds.X)); Assert.True(double.IsFinite(item.Bounds.Y));
        Assert.True(double.IsFinite(item.Bounds.Width)); Assert.True(double.IsFinite(item.Bounds.Height));
        Assert.True(item.Bounds.Width > 0); Assert.True(item.Bounds.Height > 0);
        Assert.Equal(template.Bytes, editor.Document.Write());
        var moved = CabGraphicEditor.Move(editor, item, 2, 3);
        Assert.False(moved.Validation.HasErrors);
        AssertNeighbours(editor, moved, item.Command.Syntax.Range);
        var rotated = CabGraphicEditor.Rotate(editor, item, 90);
        Assert.False(rotated.Validation.HasErrors);
        AssertNeighbours(editor, rotated, item.Command.Syntax.Range);
        var removed = CabGraphicEditor.Remove(editor, item);
        Assert.False(removed.Validation.HasErrors);
        Assert.DoesNotContain(removed.Validation.Commands, c => c.Syntax.Range.Offset == item.Command.Syntax.Range.Offset && c.Syntax.Command == item.Command.Syntax.Command);
        var added = CabGraphicEditor.Add(editor, id, 0);
        Assert.False(added.Validation.HasErrors);
        Assert.Equal(scene.Items.Count + 1, CabGraphicFactory.Build(added, layout, 0).Items.Count);
    }

    private static void AssertNeighbours(CabSourceEditor before, CabSourceEditor after, CabSourceRange changed)
    {
        var prefix = before.Document.Bytes(new(0, changed.Offset));
        var suffix = before.Document.Bytes(new(changed.End, before.Document.Length - changed.End));
        Assert.True(after.Document.Write().AsSpan().StartsWith(prefix));
        Assert.True(after.Document.Write().AsSpan().EndsWith(suffix));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void Inserted_templates_keep_their_physical_dimensions_in_inch_jobs(string id)
    {
        var mm = new CabSourceEditor("m m\rJ\rA 1\r"u8.ToArray());
        var inches = new CabSourceEditor("m i\rJ\rA 1\r"u8.ToArray());
        var a = CabGraphicEditor.Add(mm, id, 0);
        var b = CabGraphicEditor.Add(inches, id, 0);
        var layout = new LabelLayout { Dpi = 254 };
        var first = Assert.Single(CabGraphicFactory.Build(a, layout, 0).Items);
        var second = Assert.Single(CabGraphicFactory.Build(b, layout, 0).Items);
        Assert.Equal(first.ContentBounds.Width, second.ContentBounds.Width, 3);
        Assert.Equal(first.ContentBounds.Height, second.ContentBounds.Height, 3);
        Assert.False(b.Validation.HasErrors);
    }

    [Fact]
    public void Label_options_offsets_and_multiple_jobs_preserve_world_drag_direction()
    {
        var editor = new CabSourceEditor("J\rS e;1,2,40,43,60\rD 3,4\rO M,R\rG 10,20,0;C:5,3\rA 1\rJ\rG 1,2,0;C:2\rA 1\r"u8.ToArray());
        Assert.False(editor.Validation.HasErrors);
        Assert.Equal(2, CabGraphicCatalog.Jobs(editor).Count);
        var layout = new LabelLayout { Dpi = 254 };
        var scene = CabGraphicFactory.Build(editor, layout, 0);
        Assert.Equal(600, scene.Width, 6); Assert.Equal(400, scene.Height, 6); Assert.Equal(30, scene.Gap, 6);
        var item = Assert.Single(scene.Items);
        Assert.Equal(140, item.Transform.OffsetX, 6); Assert.Equal(140, item.Transform.OffsetY, 6);
        var moved = CabGraphicEditor.MoveBy(editor, scene, item, 20, 30);
        var next = Assert.Single(CabGraphicFactory.Build(moved, layout, 0).Items);
        Assert.Equal(item.Bounds.X + 20, next.Bounds.X, 6); Assert.Equal(item.Bounds.Y + 30, next.Bounds.Y, 6);
        AssertNeighbours(editor, moved, item.Command.Syntax.Range);
        Assert.Single(CabGraphicFactory.Build(editor, layout, 1).Items);
    }

    [Theory]
    [InlineData("G 10,20,0;C:5,3", 120, 80, "radius1", 6, "radius2", 4)]
    [InlineData("G 10,20,0;R:5,3,1,1", 120, 80, "width", 12, "height", 8)]
    [InlineData("W 1,2,0,10,6,3,3;abc", 120, 80, "width", 12, "height", 8)]
    [InlineData("G 10,20,0;L:5,1", 120, 20, "length", 12, "width", 2)]
    public void Resizing_edits_only_the_documented_size_parameters(string line, double width, double height, string first, double x, string second, double y)
    {
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r"));
        var item = Assert.Single(CabGraphicFactory.Build(editor, new LabelLayout { Dpi = 254 }, 0).Items);
        var next = CabGraphicEditor.Resize(editor, item, width, height, 254);
        Assert.False(next.Validation.HasErrors);
        var changed = next.Validation.Commands.Single(c => c.Syntax.Command == item.Command.Syntax.Command);
        Assert.Equal(x, CabGraphicValues.Number(changed, first), 6); Assert.Equal(y, CabGraphicValues.Number(changed, second), 6);
        AssertNeighbours(editor, next, item.Command.Syntax.Range);
        Assert.Throws<ArgumentException>(() => CabGraphicEditor.Resize(editor, item, 0, height, 254));
    }

    [Fact]
    public void Rich_text_preserves_nested_styles_entities_alignment_and_all_declared_tags()
    {
        var runs = CabRichText.Parse("<!-- hidden --><p align='center'><b><i><u><s>A&amp;B</s></u></i></b><sup>up</sup><sub>down</sub><big>big</big><small>small</small><br><hr></p><h1>1</h1><h2>2</h2><h3>3</h3><h4>4</h4><h5>5</h5>");
        var nested = runs.Single(r => r.Text == "A&B");
        Assert.True(nested.Style.Bold && nested.Style.Italic && nested.Style.Underline && nested.Style.Strike);
        Assert.Equal("center", nested.Style.Alignment);
        Assert.Equal(1, runs.Single(r => r.Text == "up").Style.Baseline);
        Assert.Equal(-1, runs.Single(r => r.Text == "down").Style.Baseline);
        Assert.True(runs.Single(r => r.Text == "big").Style.Scale > 1);
        Assert.True(runs.Single(r => r.Text == "small").Style.Scale < 1);
        Assert.Contains(runs, r => r.Rule); Assert.Contains(runs, r => r.Break);
        Assert.All(runs.Where(r => new[] { "1", "2", "3", "4", "5" }.Contains(r.Text)), r => Assert.True(r.Style.Bold && r.Style.Scale > 1));
        Assert.DoesNotContain(runs, r => r.Text.Contains("hidden", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("JS-4.2.19")]
    [InlineData("JS-4.2.27")]
    [InlineData("JS-4.2.28")]
    public void Gs1_application_identifiers_generate_real_symbols_and_invalid_identifiers_fall_back(string id)
    {
        var editor = new CabSourceEditor(CabPropertyTemplates.Get(id).Bytes);
        var command = editor.Validation.Commands.First(c => c.Syntax.Command == "B");
        var symbol = CabBarcodePreview.Create(command, new("(01)09506000134352(10)LOT42(21)SERIAL", new(""), []));
        Assert.True(symbol.IsEncoded, symbol.Note);
        var invalid = CabBarcodePreview.Create(command, new("(9999)invalid", new(""), []));
        Assert.False(invalid.IsEncoded); Assert.Contains("Vorschaufläche", invalid.Note);
    }

    [Theory]
    [InlineData("JS-4.2.11")]
    [InlineData("JS-4.2.41")]
    public void Barcode_resize_uses_modules_and_rejects_invalid_matrix_proportions(string id)
    {
        var editor = new CabSourceEditor(CabPropertyTemplates.Get(id).Bytes);
        var item = CabGraphicFactory.Build(editor, new LabelLayout(), 0).Items.First(i => i.Kind == CabGraphicKind.Barcode);
        var next = CabGraphicEditor.Resize(editor, item, item.ContentBounds.Width * 2, item.ContentBounds.Height * 2, 203);
        Assert.False(next.Validation.HasErrors); AssertNeighbours(editor, next, item.Command.Syntax.Range);
        if (CabGraphicValues.Present(item.Command, "dotsize"))
            Assert.Throws<InvalidOperationException>(() => CabGraphicEditor.Resize(editor, item, item.ContentBounds.Width * 2, item.ContentBounds.Height, 203));
    }

    [Fact]
    public void Image_dimensions_cover_supported_headers_and_reject_out_of_range_or_truncated_data()
    {
        var png = new byte[24]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 16);
        Assert.Equal((32, 16), CabGraphicImages.Dimensions(png));
        var bmp = new byte[26]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 20);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -10);
        Assert.Equal((20, 10), CabGraphicImages.Dimensions(bmp));
        var gif = new byte[10]; "GIF"u8.CopyTo(gif); gif[6] = 8; gif[8] = 4;
        Assert.Equal((8, 4), CabGraphicImages.Dimensions(gif));
        var pcx = new byte[128]; pcx[0] = 10; pcx[8] = 31; pcx[10] = 15;
        Assert.Equal((32, 16), CabGraphicImages.Dimensions(pcx));
        Assert.Equal((0, 0), CabGraphicImages.Dimensions([]));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 1_000_001);
        Assert.Equal((0, 0), CabGraphicImages.Dimensions(png));
        foreach (var little in new[] { true, false })
        {
            var tiff = TiffHeader(little);
            Assert.Equal((32, 16), CabGraphicImages.Dimensions(tiff));
            Assert.Equal((0, 0), CabGraphicImages.Dimensions(tiff[..20]));
            tiff[2] = 0; tiff[3] = 0; Assert.Equal((0, 0), CabGraphicImages.Dimensions(tiff));
        }
    }

    private static byte[] TiffHeader(bool little)
    {
        var bytes = new byte[38]; bytes[0] = bytes[1] = (byte)(little ? 'I' : 'M');
        void Word(int at, ushort value) { if (little) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value); else System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), value); }
        void Long(int at, uint value) { if (little) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value); else System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value); }
        Word(2, 42); Long(4, 8); Word(8, 2);
        Word(10, 256); Word(12, 3); Long(14, 1); Word(18, 32);
        Word(22, 257); Word(24, 4); Long(26, 1); Long(30, 16);
        return bytes;
    }

    [Fact]
    public void Examples_expand_before_content_fields_without_mutating_mixed_codepages_or_binary_data()
    {
        var bytes = Encoding.Latin1.GetBytes("J\rT:one;1,8,0,5,3;ä{Kennung}\rT 1,12,0,5,3;[one]\rA 1\r");
        var editor = new CabSourceEditor(bytes, "ISO-8859-1");
        var layout = CabLabelImport.Read(bytes, setupCodePage: "ISO-8859-1");
        layout.CabGraphicExampleValues = "Kennung=EXAMPLE";
        var items = CabGraphicFactory.Build(editor, layout, 0).Items;
        Assert.Equal("äEXAMPLE", items[0].Text); Assert.Equal("äEXAMPLE", items[1].Text);
        Assert.Equal(bytes, editor.Document.Write());
        Assert.Equal("{literal} value {missing}", CabGraphicContent.Expand("{{literal}} {key} {missing}", "key=value", out var missing));
        Assert.True(missing);
        Assert.Throws<InvalidDataException>(() => CabGraphicContent.Expand("{key}", "key=bad\tvalue", out _));
        var download = CabResourceEditor.Add(editor, "TMP", "raw", "{Kennung}"u8.ToArray(), CabJobEditor.Items(editor).First());
        _ = CabGraphicFactory.Build(download, layout, 0);
        Assert.Equal("{Kennung}"u8.ToArray(), CabResourceEditor.Read(download, Assert.Single(CabResourceEditor.Resources(download))));
    }

    [Fact]
    public void Bitmap_magnification_is_read_and_resized_with_its_required_prefixes()
    {
        var editor = new CabSourceEditor("J\rT 1,8,0,-1,x2,y3;AB\rA 1\r"u8.ToArray());
        var item = Assert.Single(CabGraphicFactory.Build(editor, new LabelLayout(), 0).Items);
        Assert.Equal(48, item.ContentBounds.Width); Assert.Equal(36, item.ContentBounds.Height);
        var resized = CabGraphicEditor.Resize(editor, item, 96, 72, 203);
        Assert.False(resized.Validation.HasErrors);
        Assert.Contains("x4,y6", Encoding.UTF8.GetString(resized.Document.Write()));
        AssertNeighbours(editor, resized, item.Command.Syntax.Range);
    }

    [Fact]
    public void Baseline_circle_origin_units_and_counterclockwise_rotation_have_independent_expected_bounds()
    {
        const string source = "J\rS e;0,0,40,40,60\rT 10,20,90,5,4;AB\rG 30,20,0;C:5,3\rW 1,2,0,10,6,3,3;abc\rA 1\r";
        var editor = new CabSourceEditor(Encoding.UTF8.GetBytes(source));
        var scene = CabGraphicFactory.Build(editor, new LabelLayout { Dpi = 254 }, 0);
        var text = scene.Items[0];
        Assert.Equal(68, text.Bounds.X, 6); // 10 mm minus the 0.8-em ascent after a 90-degree turn.
        Assert.Equal(200 - text.ContentBounds.Width, text.Bounds.Y, 6);
        var circle = scene.Items[1]; Assert.Equal(new CabGraphicRect(250, 170, 100, 60), circle.Bounds);
        Assert.Equal(new CabGraphicRect(10, 20, 100, 60), scene.Items[2].Bounds);
        var transformed = CabGraphicTransform.Placement(10, 20, 90).Apply(new(4, 0));
        Assert.Equal(10, transformed.X, 8); Assert.Equal(16, transformed.Y, 8);
    }
}
