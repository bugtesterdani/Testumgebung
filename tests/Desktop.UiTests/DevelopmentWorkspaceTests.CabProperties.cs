using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Testumgebung.Desktop.Views;
using Testumgebung.Domain.Labels.CabJScript;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCabProperties()
    {
        const string source = "J\rT 0,0,0,5,3;one\rT 0,10,0,5,3;two\rA 1\r";
        var layout = CabLabelImport.Read(Encoding.UTF8.GetBytes(source));
        var unaffected = layout.Elements[1];
        var window = new CabPropertiesWindow(layout);
        try
        {
            window.Show();
            var jobs = Assert.Single(Descendants(window).OfType<ListBox>());
            jobs.SelectedIndex = 1; window.UpdateLayout();
            var text = Descendants(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "text");
            text.Text = "changed";
            var row = Assert.IsType<StackPanel>(text.Parent);
            var apply = row.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>()).Single(b => Equals(b.Content, "Übernehmen"));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Encoding.UTF8.GetBytes(source.Replace("one", "changed", StringComparison.Ordinal)), CabLabelLayout.Render(layout));
            Assert.Same(unaffected, layout.Elements[1]);
            Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Rückgängig")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Encoding.UTF8.GetBytes(source), CabLabelLayout.Render(layout));
            Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Wiederholen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("changed", Encoding.UTF8.GetString(window.Editor.Document.Write()));
            Assert.Single(Descendants(window).OfType<CheckBox>()).IsChecked = true; window.UpdateLayout();
            var name = Descendants(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "name");
            name.Text = "Label";
            var nameRow = Assert.IsType<StackPanel>(name.Parent);
            nameRow.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>()).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains("T:Label;", Encoding.UTF8.GetString(window.Editor.Document.Write()));
            var before = window.Editor.Document.Write();
            Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Nach oben")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(before, window.Editor.Document.Write());
            VerifyPropertyCommands(window);
            VerifyPropertyResources(window);
        }
        finally { window.Close(); }
        VerifyContentForms();
    }

    private static void VerifyPropertyCommands(CabPropertiesWindow window)
    {
        var jobs = Assert.Single(Descendants(window).OfType<ListBox>());
        jobs.SelectedItem = jobs.Items.Cast<CabJobItem>().Last(i => i.Command?.Syntax.Command == "A");
        var catalog = Descendants(window).OfType<ComboBox>().Single(c => c.Items.OfType<CabCatalogEntry>().Any());
        catalog.SelectedItem = catalog.Items.OfType<CabCatalogEntry>().Single(e => e.Id == "JS-4.16");
        var before = window.Editor.Document.Write();
        Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Befehl hinzufügen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(3, window.Editor.Validation.Commands.Count(c => c.Syntax.Command == "T"));
        Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Entfernen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(before, window.Editor.Document.Write());
        var described = false;
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            var definition = window.OwnedWindows.Cast<Window>().Single();
            try { described = Descendants(definition).OfType<TextBlock>().Any(t => t.Text.Contains("Handbuch S.", StringComparison.Ordinal)); }
            finally { definition.Close(); }
        }));
        Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Definition ansehen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(described);
        jobs.SelectedItem = jobs.Items.Cast<CabJobItem>().First(i => i.Command?.Syntax.Command == "T");
        var command = window.Selected!.Command!;
        var property = CabPropertyBindings.ForCommand(window.Editor, command).Single(p => p.Definition.Name == "text");
        window.Apply(CabPropertyBindings.Replace(window.Editor, property, "[SER:1]")); window.UpdateLayout();
        var inc = Descendants(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "inc");
        inc.Text = "2";
        Assert.IsType<StackPanel>(inc.Parent).Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>()).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("[SER:1,2]", Encoding.UTF8.GetString(window.Editor.Document.Write()));
        VerifyContentInsertion(window);
    }

    private static void VerifyContentInsertion(CabPropertiesWindow window)
    {
        var text = Descendants(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == "text");
        text.Select(0, 0);
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            var dialog = window.OwnedWindows.OfType<CabContentWindow>().Single();
            var catalog = Descendants(dialog).OfType<ComboBox>().Single(c => c.Items.OfType<CabCatalogEntry>().Any());
            catalog.SelectedItem = catalog.Items.OfType<CabCatalogEntry>().Single(e => e.Id == "JS-5.11");
            Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "Inhaltsfeld einfügen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        var row = Assert.IsType<StackPanel>(text.Parent);
        row.Children.OfType<Button>().Single(b => Equals(b.Content, "Inhaltsfeld einfügen …")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.StartsWith("[DAY", text.Text);
        row.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>()).Single(b => Equals(b.Content, "Übernehmen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains("[DAY", Encoding.UTF8.GetString(window.Editor.Document.Write()));
    }

    private static void VerifyPropertyResources(CabPropertiesWindow window)
    {
        var jobs = Assert.Single(Descendants(window).OfType<ListBox>()); jobs.SelectedIndex = 0;
        (string Type, System.Windows.Media.Imaging.BitmapEncoder Encoder)[] formats =
        [
            ("PNG", new System.Windows.Media.Imaging.PngBitmapEncoder()), ("BMP", new System.Windows.Media.Imaging.BmpBitmapEncoder()),
            ("GIF", new System.Windows.Media.Imaging.GifBitmapEncoder()), ("TIF", new System.Windows.Media.Imaging.TiffBitmapEncoder()),
        ];
        foreach (var (type, encoder) in formats)
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new System.IO.MemoryStream(); encoder.Save(stream);
            window.Apply(CabResourceEditor.Add(window.Editor, type, "image", stream.ToArray(), window.Selected)); window.UpdateLayout();
            Assert.Single(Descendants(window).OfType<Image>());
            Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text.Contains("Bytes", StringComparison.Ordinal));
        }
        window.Apply(CabResourceEditor.Add(window.Editor, "TMP", "data", [0, 255], window.Selected)); window.UpdateLayout();
        Assert.Empty(Descendants(window).OfType<Image>());
        Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text.StartsWith("2 Bytes", StringComparison.Ordinal));
        Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "Kommentar hinzufügen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
        Assert.Contains(Descendants(window).OfType<TextBlock>(), t => t.Text.StartsWith("Kommentare", StringComparison.Ordinal));
    }

    private static void VerifyContentForms()
    {
        var dialog = new CabContentWindow(CabPrinterGeneration.X4);
        try
        {
            dialog.Show();
            var choices = Descendants(dialog).OfType<ComboBox>().ToArray();
            Assert.Equal(2, choices.Length);
            var catalog = choices.Single(c => c.Items.OfType<CabCatalogEntry>().Any());
            Assert.Equal(107, catalog.Items.Count);
            foreach (var entry in catalog.Items.OfType<CabCatalogEntry>().ToArray())
            {
                catalog.SelectedItem = entry; dialog.UpdateLayout();
                Assert.Equal(entry.Availability(CabPrinterGeneration.X4) != CabAvailability.Unsupported,
                    Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "Inhaltsfeld einfügen")).IsEnabled);
                Assert.Contains(Descendants(dialog).OfType<TextBlock>(), t => t.Text.StartsWith("Syntax:", StringComparison.Ordinal));
                Assert.All(Descendants(dialog).OfType<TextBox>(), b => Assert.NotNull(b.Text));
            }
            Assert.Contains(CabContentParser.Definitions, d => CabContentWindow.Group(d) == "RFID");
        }
        finally { dialog.Close(); }
    }
}
