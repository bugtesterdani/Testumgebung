using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Definitions;
using Testumgebung.Engine.Blocks;
using Testumgebung.Persistence;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyLabelDesigner(Dispatcher dispatcher)
    {
        var folder = Path.Combine(Path.GetTempPath(), "label-designer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var layout = new LabelLayout();
            var bitmap = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr24, null, new byte[] { 0, 0, 0, 255, 255, 255 }, 6);
            VerifyLabelImages(folder, layout, bitmap);
            var loop = new GroupDefinition("loop", "Loop").Add(BlockCatalog.Default.Create(LabelPrintingBlocks.Print, "label"));
            var editor = new EditorViewModel(new TestProject(new TestDefinition("Etikett", "1", new("init", "Init"), loop, new("end", "Ende"))));
            var properties = Assert.IsType<StepPropertiesViewModel>(editor.AllNodes().Single(node => node.Id == "label").InlineProperties);
            properties.SaveLabelLayout(layout);
            var field = properties.Parameters.Single(row => row.IsLabelTemplate);
            var source = new Border();
            var owner = Sta.ShowHost(source);
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (owner.OwnedWindows.OfType<LabelDesignerWindow>().FirstOrDefault() is not { } dialog) return;
                    try
                    {
                        dialog.ImportImage(Path.Combine(folder, "logo.png"));
                        foreach (var button in Descendants(dialog).OfType<Button>().Where(button => button.Tag is string).ToArray())
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Vorlage übernehmen"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                }));
                LabelDesignerWindow.Edit(source, field);
            }
            finally { owner.Close(); }
            var zpl = field.Value;
            Assert.Contains("^GFA,", zpl);
            Assert.Contains("^BCN,", zpl);
            Assert.Contains("^BQN,", zpl);
            Assert.Contains("^GB", zpl);
            Assert.Contains("^FD{Kennung}", zpl);
            VerifyCabFileImport(folder, properties, field);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void VerifyCabFileImport(string folder, StepPropertiesViewModel properties, ParameterRowViewModel zpl)
    {
        var format = properties.Parameters.Single(row => row.Descriptor.Name == LabelPrintingBlocks.FormatParameter);
        var cab = properties.Parameters.Single(row => row.IsCabLabel);
        Assert.True(cab.IsHidden);
        format.Value = LabelPrintingBlocks.Cab;
        Assert.False(cab.IsHidden);
        Assert.False(cab.IsText);
        Assert.True(zpl.IsHidden);
        Assert.Equal("Keine Datei eingebettet", cab.CabFileStatus);
        var path = Path.Combine(folder, "bild.lbl");
        Assert.NotNull(CabLabelFile.TryEmbed(path, cab));
        File.WriteAllBytes(path, []);
        Assert.NotNull(CabLabelFile.TryEmbed(path, cab));
        byte[] data = [0x00, 0x1B, 0xFF, 0x80, 0x0D, 0x0A];
        File.WriteAllBytes(path, data);
        Assert.Null(CabLabelFile.TryEmbed(path, cab));
        Assert.Equal(data, Convert.FromBase64String(cab.Value));
        Assert.Equal("Druckdatei eingebettet", cab.CabFileStatus);
        Assert.Equal(LabelPrintingBlocks.Cab, format.Value);
        format.Value = LabelPrintingBlocks.Zpl;
        Assert.False(zpl.IsHidden);
        Assert.True(cab.IsHidden);
    }

    private static void VerifyLabelImages(string folder, LabelLayout layout, BitmapSource bitmap)
    {
        (string Extension, BitmapEncoder Encoder)[] formats =
        [
            ("png", new PngBitmapEncoder()), ("jpg", new JpegBitmapEncoder()), ("bmp", new BmpBitmapEncoder()),
            ("gif", new GifBitmapEncoder()), ("tiff", new TiffBitmapEncoder()),
        ];
        foreach (var (extension, encoder) in formats)
        {
            var file = Path.Combine(folder, "logo." + extension);
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
            var image = LabelImage.Import(file, layout.WidthDots, layout.HeightDots);
            Assert.Equal(2, image.ImageWidth);
            Assert.Equal(1, image.ImageHeight);
            Assert.Equal(new byte[] { 0x80 }, image.Bitmap);
            layout.Elements.Add(image);
        }
    }
}
