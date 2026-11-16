using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;
using static Testumgebung.Engine.Tests.Kit;

namespace Testumgebung.Engine.Tests;

public sealed class LabelLayoutTests
{
    [Fact]
    public void Layout_and_generated_ZPL_are_saved_together_and_survive_project_reopen()
    {
        var layout = new LabelLayout { WidthMm = 50, HeightMm = 25, Dpi = 300 };
        layout.Elements.Add(new() { X = 20, Y = 30, Value = "{_Kennung}" });
        layout.Elements.Add(new() { Kind = LabelElementKind.Image, X = 15, Y = 5, ImageWidth = 9, ImageHeight = 1, Bitmap = [0x80, 0x80] });
        var step = BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label");
        var editor = new EditorViewModel(new TestProject(Test(Group("loop").Add(step))));
        var node = editor.AllNodes().Single(node => node.Id == "label");
        editor.SelectedNode = node;
        var properties = Assert.IsType<StepPropertiesViewModel>(node.InlineProperties);
        properties.SaveLabelLayout(layout);

        var reopened = new ProjectSerializer().Clone(editor.Project);
        var printed = Assert.IsType<Testumgebung.Domain.Definitions.StepDefinition>(reopened.Definition.Find("label"));
        var reconstructed = LabelLayout.Load(printed.Parameters[LabelLayout.Parameter]);
        var zpl = printed.Parameters["ZplVorlage"];
        Assert.Equal(zpl, reconstructed.ToZpl());
        Assert.Contains("^PW591\n^LL295", zpl);
        Assert.Contains("^FO20,30^A0N,30,30^FD{_Kennung}^FS", zpl);
        Assert.Contains("^FO15,5^GFA,2,2,2,8080^FS", zpl);
    }

    [Fact]
    public void Graphic_threshold_uses_white_for_transparent_pixels_and_pads_each_row()
    {
        byte[] pixels =
        [
            0, 0, 0, 255,  0, 0, 0, 0,  255, 255, 255, 255,
            255, 255, 255, 255,  0, 0, 0, 255,  0, 0, 0, 127,
        ];
        Assert.Equal(new byte[] { 0x80, 0x40 }, LabelLayout.PackMonochrome(pixels, 3, 2));
    }
}
