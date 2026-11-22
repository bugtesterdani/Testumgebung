using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows;
using System.Windows.Input;
using Testumgebung.Desktop.Views;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.UiTests;

public sealed partial class DevelopmentWorkspaceTests
{
    private static void VerifyCabSourceEditor()
    {
        var path = Path.Combine(Path.GetTempPath(), "cab-source-" + Guid.NewGuid().ToString("N") + ".lbl");
        const string text = "J\rT 0,0,0,5,3;one\rT 0,10,0,5,3;two\rA 1\r";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        var layout = new LabelLayout(); var designer = new LabelDesignerWindow(layout, true);
        CabSourceWindow? source = null;
        try
        {
            designer.Show(); designer.OpenCabFile(path);
            var unchanged = layout.Elements[1];
            var canvas = Descendants(designer).OfType<Canvas>().Single(c => c.Name == "Sheet");
            var unchangedVisual = canvas.Children[1];
            source = new CabSourceWindow(layout, designer.RefreshCabSource) { Owner = designer }; source.Show();
            var editor = Assert.Single(Descendants(source).OfType<RichTextBox>());
            var run = editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Single(r => r.Text.Contains("one", StringComparison.Ordinal));
            var start = run.ContentStart.GetPositionAtOffset(run.Text.IndexOf("one", StringComparison.Ordinal))!;
            editor.Selection.Select(start, start.GetPositionAtOffset(3)!); editor.Selection.Text = "changed";
            Assert.Same(unchanged, layout.Elements[1]); Assert.Same(unchangedVisual, canvas.Children[1]);
            designer.SaveCabFile(path);
            Assert.Equal(Encoding.UTF8.GetBytes(text.Replace("one", "changed", StringComparison.Ordinal)), File.ReadAllBytes(path));
            Assert.Contains(Descendants(source).OfType<TextBlock>(), t => t.Text.Contains("Diagnosen", StringComparison.Ordinal));
            VerifySourceDiagnosisNavigation(source);
            var search = Assert.Single(Descendants(source).OfType<TextBox>()); search.Text = "JS-5.1";
            Assert.Contains(Descendants(source).OfType<ListBox>().SelectMany(l => l.Items.Cast<object>()),
                item => item.ToString()!.Contains("JS-5.1", StringComparison.Ordinal));
        }
        finally { source?.Close(); designer.Close(); File.Delete(path); }
    }

    private static void VerifySourceDiagnosisNavigation(CabSourceWindow source)
    {
        var text = source.Editor.Sections[0].Editable!.Text.Replace("T 0,0,0,5,3;changed", "B 0,0,45,CODE128,10,.3;changed", StringComparison.Ordinal);
        source.ApplyText(0, text);
        var diagnostics = Descendants(source).OfType<ListBox>().Single(l => l.Items.Count > 0
            && l.Items[0].ToString()!.Contains("Handbuch S.", StringComparison.Ordinal));
        diagnostics.SelectedIndex = 0;
        diagnostics.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        Assert.Equal("45", Assert.Single(Descendants(source).OfType<RichTextBox>()).Selection.Text);
        Assert.Contains(Descendants(source).OfType<TextBlock>(), t => t.Text.Contains('●'));
    }
}
