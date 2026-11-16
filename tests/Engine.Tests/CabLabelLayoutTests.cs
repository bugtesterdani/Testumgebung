using System.Buffers.Binary;
using System.Text;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class CabLabelLayoutTests
{
    [Fact]
    public void Cab_designer_persists_editable_elements_and_downloads_an_escaped_monochrome_image()
    {
        var layout = new LabelLayout { WidthMm = 60, HeightMm = 40, GapMm = 2.5m, Dpi = 254 };
        layout.Elements.Add(new() { Value = "Prüfling", X = 10, Y = 20 });
        layout.Elements.Add(new() { Kind = LabelElementKind.Code128, Value = "SN123", Size = 50 });
        layout.Elements.Add(new() { Kind = LabelElementKind.Qr, Value = "ABC", Size = 4 });
        layout.Elements.Add(new() { Kind = LabelElementKind.Line, Size = 2, Length = 100 });
        layout.Elements.Add(new() { Kind = LabelElementKind.Image, ImageWidth = 8, ImageHeight = 2, Bitmap = [0x1B, 0x80] });

        var step = BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label", parameters: new Dictionary<string, string>
        {
            [LabelPrintingBlocks.FormatParameter] = LabelPrintingBlocks.Cab,
        });
        var editor = new EditorViewModel(new TestProject(Test(Group("loop").Add(step))));
        var properties = Assert.IsType<StepPropertiesViewModel>(editor.AllNodes().Single(node => node.Id == "label").InlineProperties);
        properties.SaveCabLabelLayout(layout);

        var saved = new ProjectSerializer().Clone(editor.Project).Definition.Find("label");
        var parameters = Assert.IsType<Testumgebung.Domain.Definitions.StepDefinition>(saved).Parameters;
        var bytes = Convert.FromBase64String(parameters[LabelPrintingBlocks.CabDataParameter]);
        var reconstructed = LabelLayout.Load(parameters[CabLabelLayout.Parameter]);
        Assert.Equal(bytes, CabLabelLayout.Render(reconstructed));
        Assert.Equal(5, reconstructed.Elements.Count);
        Assert.Equal(2.5m, reconstructed.GapMm);

        var commands = Encoding.UTF8.GetString(bytes);
        Assert.Contains("S l1;0,0,40,42.5,60\r", commands);
        Assert.Contains("T 1,2,0,5,3;Prüfling\r", commands);
        Assert.Contains("B 0,0,0,CODE128,5,0.2;SN123\r", commands);
        Assert.Contains("B 0,0,0,QRCODE,0.4;ABC\r", commands);
        Assert.Contains("G 0,0,0;L:10,0.2\r", commands);
        Assert.EndsWith("A 1\r", commands);

        var download = Encoding.ASCII.GetString(bytes);
        var name = download[(download.IndexOf("d BMP;", StringComparison.Ordinal) + 6)..].Split('\r')[0];
        Assert.Equal(8, name.Length);
        Assert.Contains($"I 0,0,0;{name}\r", commands);
        var begin = bytes.AsSpan().IndexOf(new byte[] { 0x1B, (byte)'.' }) + 2;
        var bmpEscaped = bytes.AsSpan(begin);
        bmpEscaped = bmpEscaped[..bmpEscaped.IndexOf(new byte[] { 0x1B, (byte)'.' })];
        Assert.Equal(new byte[] { 0x1B, 0x1B }, bmpEscaped.Slice(66, 2).ToArray());
        Assert.Equal(62, BinaryPrimitives.ReadInt32LittleEndian(bmpEscaped[10..]));
        Assert.Equal(new byte[] { 0x80, 0, 0, 0 }, bmpEscaped.Slice(62, 4).ToArray());

        properties.ImportCabPrintData([0x00, 0xFF, 0x1B]);
        Assert.DoesNotContain(CabLabelLayout.Parameter, properties.Step.Parameters.Keys);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x1B },
            Convert.FromBase64String(properties.Step.Parameters[LabelPrintingBlocks.CabDataParameter]));
    }

    [Fact]
    public void Sample_values_only_change_the_preview_and_keep_surrounding_text()
    {
        var item = new LabelElement
        {
            Value = "{{Los}} {Kennung} / {Pruefwert} {Unbekannt}",
            ExampleValues = "Kennung=SN-42\r\nPruefwert=12,50 V",
        };
        Assert.Equal("{Los} SN-42 / 12,50 V {Unbekannt}", item.PreviewText());

        var layout = LabelLayout.Load(new LabelLayout { Elements = [item] }.Save());
        Assert.Equal(item.PreviewText(), Assert.Single(layout.Elements).PreviewText());
        var text = Encoding.UTF8.GetString(CabLabelLayout.Render(layout));
        Assert.Contains(item.Value, text);
        Assert.DoesNotContain("SN-42", text);
    }

    [Theory]
    [InlineData(LabelElementKind.Text, "Text\rA 99")]
    [InlineData(LabelElementKind.Text, "[DATE]")]
    [InlineData(LabelElementKind.Code128, "Ä")]
    [InlineData(LabelElementKind.Code128, "")]
    public void Invalid_cab_fields_are_rejected_before_a_job_is_created(LabelElementKind kind, string text)
    {
        var layout = new LabelLayout { Elements = [new() { Kind = kind, Value = text }] };
        Assert.Throws<InvalidDataException>(() => CabLabelLayout.Render(layout));
    }

    [Fact]
    public void Zero_gap_uses_continuous_media_and_invalid_dimensions_are_rejected()
    {
        var layout = new LabelLayout { GapMm = 0 };
        Assert.Contains("S e;0,0,40,40,60", Encoding.UTF8.GetString(CabLabelLayout.Render(layout)));
        layout.GapMm = -1;
        Assert.Throws<InvalidDataException>(() => CabLabelLayout.Render(layout));
    }
}
