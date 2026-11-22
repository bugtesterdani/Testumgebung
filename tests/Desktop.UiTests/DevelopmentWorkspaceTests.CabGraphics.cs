using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCabGraphics()
    {
        const string source = "J\rS e;0,0,40,40,60\rT 5,10,0,5,3;example\rG 20,20,0;C:4,3\rA 1\r";
        var layout = CabLabelImport.Read(Encoding.UTF8.GetBytes(source));
        var window = new CabGraphicWindow(layout);
        try
        {
            window.Show(); window.UpdateLayout();
            var items = Assert.Single(Descendants(window).OfType<ListBox>());
            Assert.Equal(2, items.Items.Count);
            items.SelectedIndex = 0;
            var original = window.Editor.Document.Write();
            window.MoveSelectedBy(20, 10);
            Assert.NotEqual(original, window.Editor.Document.Write());
            GraphicButton(window, "Rückgängig");
            Assert.Equal(original, window.Editor.Document.Write());
            GraphicButton(window, "Wiederholen");
            Assert.NotEqual(original, window.Editor.Document.Write());
            items.SelectedIndex = 0;
            foreach (var (name, value) in new[] { ("X", "8"), ("Y", "12"), ("Drehung", "90") })
                Descendants(window).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "cab-Grafik " + name).Text = value;
            GraphicButton(window, "Position und Drehung übernehmen");
            Assert.Contains("T 8,12,90,5,3;example", Encoding.UTF8.GetString(window.Editor.Document.Write()));
            GraphicButton(window, "90° drehen");
            Assert.Contains("T 8,12,180,5,3;example", Encoding.UTF8.GetString(window.Editor.Document.Write()));
            items.SelectedIndex = 1;
            foreach (var (name, value) in new[] { ("Breite", "10"), ("Höhe", "8") })
                Descendants(window).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "cab-Grafik " + name).Text = value;
            GraphicButton(window, "Größe übernehmen");
            Assert.Contains("C:5,4", Encoding.UTF8.GetString(window.Editor.Document.Write()));
            GraphicButton(window, "Löschen"); Assert.Single(items.Items.Cast<object>());
            GraphicButton(window, "Rückgängig"); Assert.Equal(2, items.Items.Count);
            Assert.False(window.Editor.Validation.HasErrors);
            var catalog = Descendants(window).OfType<ComboBox>().Single(c => c.Items.OfType<CabCatalogEntry>().Any());
            catalog.SelectedItem = catalog.Items.OfType<CabCatalogEntry>().Single(e => e.Id == "JS-4.16");
            GraphicButton(window, "Einfügen"); Assert.Equal(3, items.Items.Count);
            VerifyGraphicImages(window);
        }
        finally { window.Close(); }
        VerifyEveryGraphicVisual();
        VerifyGraphicEffects();
        VerifyInvisibleGraphicPixels();
    }

    private static void VerifyGraphicImages(CabGraphicWindow window)
    {
        BitmapEncoder[] encoders = [new PngBitmapEncoder(), new BmpBitmapEncoder(), new GifBitmapEncoder(), new TiffBitmapEncoder()];
        string[] types = ["PNG", "BMP", "GIF", "TIF"];
        var bitmap = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray(), 128);
        for (var index = 0; index < encoders.Length; index++)
        {
            var encoder = encoders[index]; encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new System.IO.MemoryStream(); encoder.Save(stream);
            window.AddImageBytes(types[index], "user1", stream.ToArray()); window.UpdateLayout();
            var image = window.Scene!.Items.Last(i => i.Kind == CabGraphicKind.Image);
            Assert.Equal(32, image.Image!.PixelWidth); Assert.Equal(32, image.Image.PixelHeight);
            var resized = CabGraphicEditor.Resize(window.Editor, image, 64, 96, window.Scene.Dpi);
            var scene = CabGraphicFactory.Build(resized, new LabelLayout { Dpi = window.Scene.Dpi }, 0);
            var changed = scene.Items.Last(i => i.Kind == CabGraphicKind.Image);
            Assert.Equal(64, changed.ContentBounds.Width); Assert.Equal(96, changed.ContentBounds.Height);
        }
    }

    private static void VerifyGraphicEffects()
    {
        string[] lines =
        [
            "T 2,8,0,-1,x2,y2;Bitmap", "T 2,8,0,-2,x2,y2;Bitmap", "T 2,8,0,-3,x2,y2;Bitmap",
            "T 2,8,0,-4,x2,y2;Bitmap", "T 2,8,0,-5,x2,y2;Bitmap",
            "T 2,8,0,596,3;Mono", "T 2,8,0,3,3,b,i,u,n;Inverse",
            "T 2,8,0,7,3;Condensed", "T 2,8,0,1000,3;Chinese", "T 2,8,0,1001,3;Chinese", "T 2,8,0,1010,3;Thai",
            "T 2,8,0,5,3,fr1,fl1,fu1,fd1;Frame",
            "T 2,8,0,-1,x2,y2,l,v,o;Vertical", "T 2,8,0,-1,x2,y2,g;Gray",
            "W 2,2,0,50,30,3,3;<HTML><p align='center'><b>Bold</b><i>Italic</i><u>Underline</u><s>Strike</s><br><sup>up</sup><sub>down</sub><big>big</big><small>small</small><hr></p><h1>Heading</h1></HTML>",
            "G 2,2,0;R:20,10,1,2[F:left]", "G 2,2,0;R:20,10,1,2[F:right]",
            "G 2,2,0;R:20,10,1,2[F:dots]", "G 2,2,0;R:20,10,1,2[F:grid]",
            "G 2,2,0;R:20,10,1,2[F:diamond]", "G 2,2,0;R:20,10,1,2[F:user1]",
            "G 2,2,0;R:20,10,1,2[F:50%]", "G 2,2,0;R:20,10,1,2[S:20,80,90]",
            "G 2,2,0;L:20,1,a,r[D:2,1]", "G 2,2,0;R:20,10,1,2[O]",
        ];
        foreach (var line in lines)
        {
            var editor = new CabSourceEditor(Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r"));
            Assert.False(editor.Validation.HasErrors, line + ": " + string.Join(";", editor.Validation.Diagnostics.Select(d => d.Message)));
            foreach (var item in CabGraphicFactory.Build(editor, new LabelLayout(), 0).Items) RenderGraphic(item, 203);
        }
    }

    private static void RenderGraphic(CabGraphicItem item, int dpi)
    {
        var visual = new CabGraphicVisual(item, dpi);
        visual.Measure(new Size(2000, 2000)); visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.Width)), Math.Max(1, (int)Math.Ceiling(visual.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); Assert.True(bitmap.PixelWidth > 0);
    }

    private static void VerifyInvisibleGraphicPixels()
    {
        foreach (var line in new[] { "T 1,8,0,5,3,n,fr1;[I]", "B 1,2,0,CODE128,10,.3,n,fr1;[I]" })
        {
            var editor = new CabSourceEditor(Encoding.UTF8.GetBytes("J\r" + line + "\rA 1\r"));
            var item = Assert.Single(CabGraphicFactory.Build(editor, new LabelLayout(), 0).Items);
            Assert.True(item.Hidden);
            var visual = new CabGraphicVisual(item, 203);
            visual.Measure(new Size(2000, 2000)); visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.Width), (int)Math.Ceiling(visual.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            Assert.All(pixels, value => Assert.Equal((byte)0, value));
        }
    }

    private static void GraphicButton(Window window, string label)
    {
        Descendants(window).OfType<Button>().Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
    }

    private static void VerifyEveryGraphicVisual()
    {
        foreach (var definition in CabGraphicCatalog.Definitions)
        {
            var template = CabPropertyTemplates.Get(definition.Id);
            var editor = new CabSourceEditor(template.Bytes);
            var scene = CabGraphicFactory.Build(editor, CabLabelImport.Read(template.Bytes), 0);
            foreach (var item in scene.Items)
            {
                var visual = new CabGraphicVisual(item, scene.Dpi);
                visual.Measure(new Size(2000, 2000));
                visual.Arrange(new Rect(0, 0, visual.Width, visual.Height));
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.Width)), Math.Max(1, (int)Math.Ceiling(visual.Height)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                Assert.True(bitmap.PixelWidth > 0);
            }
            Assert.Equal(template.Bytes, editor.Document.Write());
        }
    }
}
