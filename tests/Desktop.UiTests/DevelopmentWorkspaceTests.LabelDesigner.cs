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
            VerifyCabLabelDesigner(dispatcher, folder, properties);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void VerifyCabLabelDesigner(Dispatcher dispatcher, string folder, StepPropertiesViewModel properties)
    {
        var format = properties.Parameters.Single(row => row.Descriptor.Name == LabelPrintingBlocks.FormatParameter);
        format.Value = LabelPrintingBlocks.Cab;
        var cab = properties.Parameters.Single(row => row.IsCabLabel);
        properties.SaveCabLabelLayout(new());
        var source = new Border();
        var owner = Sta.ShowHost(source);
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var dialog = owner.OwnedWindows.OfType<LabelDesignerWindow>().Single();
                try
                {
                    Assert.Equal("cab-Etikett gestalten", dialog.Title);
                    var existing = new LabelLayout { Elements = [new() { Value = "Vorhandene LBL" }] };
                    dialog.ImportCab(CabLabelLayout.Render(existing));
                    var importedList = Descendants(dialog).OfType<ListBox>().Single();
                    Assert.Equal("Vorhandene LBL", Assert.IsType<LabelElement>(Assert.Single(importedList.Items.Cast<object>())).Value);
                    var invalidFile = Path.Combine(folder, "invalid.lbl");
                    File.WriteAllBytes(invalidFile, [0, 1, 2]);
                    dialog.OpenCabFile(invalidFile);
                    var status = Assert.IsType<TextBlock>(dialog.FindName("Status"));
                    Assert.NotEmpty(status.Text);
                    Assert.Equal("Vorhandene LBL", Assert.IsType<LabelElement>(Assert.Single(importedList.Items.Cast<object>())).Value);
                    var retained = existing.Elements[0];
                    var active = Assert.IsType<LabelElement>(Assert.Single(importedList.Items.Cast<object>()));
                    active.Value = "[DATE]";
                    var exportPath = Path.Combine(folder, "invalid-export.lbl");
                    File.WriteAllText(exportPath, "original");
                    dialog.SaveCabFile(exportPath);
                    Assert.Contains("eckigen Klammern", status.Text);
                    Assert.Equal("original", File.ReadAllText(exportPath));
                    active.Value = retained.Value;
                    dialog.SaveCabFile(exportPath);
                    Assert.Single(CabLabelImport.Read(File.ReadAllBytes(exportPath)).Elements);
                    dialog.ImportCab(CabLabelLayout.Render(new LabelLayout()));
                    dialog.ImportImage(Path.Combine(folder, "logo.png"));
                    foreach (var button in Descendants(dialog).OfType<Button>().Where(button => button.Tag is string).ToArray())
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var list = Descendants(dialog).OfType<ListBox>().Single();
                    var gs1 = list.Items.Cast<LabelElement>().Single(item => item.Kind == LabelElementKind.Gs1DataMatrix);
                    list.SelectedItem = gs1;
                    var identifiers = Assert.IsType<ComboBox>(dialog.FindName("Gs1Identifiers"));
                    var serial = identifiers.Items.Cast<Gs1ApplicationIdentifier>().Single(item => item.Id == "21");
                    Assert.Contains("Seriennummer", serial.Display);
                    identifiers.SelectedItem = serial;
                    var content = Assert.IsType<TextBox>(dialog.FindName("BarcodeContent"));
                    content.Select(content.Text.Length, 0);
                    Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Kennung einfügen"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.EndsWith("(21)", gs1.Value);
                    content.SetCurrentValue(TextBox.TextProperty, gs1.Value + "{Kennung}");
                    content.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();

                    list.SelectedItem = list.Items.Cast<LabelElement>().Single(item => item.Kind == LabelElementKind.Text);
                    var sample = Descendants(dialog).OfType<TextBox>().Single(box =>
                        box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(LabelElement.ExampleValues));
                    sample.SetCurrentValue(TextBox.TextProperty, "Kennung=DEMO-101");
                    sample.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                    Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text == "DEMO-101");
                    Descendants(dialog).OfType<Button>().Single(button => Equals(button.Content, "Vorlage übernehmen"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }));
            LabelDesignerWindow.Edit(source, cab);

            var layout = LabelLayout.Load(properties.Step.Parameters[CabLabelLayout.Parameter]);
            Assert.Equal(8, layout.Elements.Count);
            Assert.Contains(layout.Elements, item => item.Kind == LabelElementKind.DataMatrix);
            Assert.Contains(layout.Elements, item => item.Kind == LabelElementKind.Gs1DataMatrix);
            Assert.Contains(layout.Elements, item => item.Kind == LabelElementKind.Gs1Qr);
            Assert.Equal(CabLabelLayout.Render(layout), Convert.FromBase64String(cab.Value));

            properties.ImportCabPrintData(CabLabelLayout.Render(layout));
            var readExisting = LabelDesignerWindow.ReadLayout(cab, true);
            Assert.Equal(layout.Elements.Count, readExisting.Elements.Count);
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                owner.OwnedWindows.OfType<LabelDesignerWindow>().Single().Close()));
            var beforeCancel = cab.Value;
            LabelDesignerWindow.Edit(source, cab);
            Assert.Equal(beforeCancel, cab.Value);

            var imported = new byte[] { 0, 0x1B, 0xFF };
            properties.ImportCabPrintData(imported);
            Assert.Throws<InvalidDataException>(() => LabelDesignerWindow.ReadLayout(cab, true));
            Assert.Equal(imported, Convert.FromBase64String(cab.Value));
        }
        finally { owner.Close(); }
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
