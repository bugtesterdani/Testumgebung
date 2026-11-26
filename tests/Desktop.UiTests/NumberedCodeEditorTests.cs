using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Testumgebung.Desktop.Controls;

namespace Testumgebung.Desktop.UiTests;

public sealed class NumberedCodeEditorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("erste Zeile\nzweite Zeile")]
    public void Rendern_ohne_Textlayout_zeichnet_nur_Hintergrund_und_gleicht_Clip_aus(string text)
    {
        Sta.Run(_ =>
        {
            var control = new NumberedCodeEditor { Text = text };
            var editor = control.Children.OfType<TextBox>().Single();
            var numbers = control.Children.OfType<FrameworkElement>().First();
            editor.ApplyTemplate();
            // Nur die Randleiste anordnen: Die TextBox hat bewusst noch kein Layout.
            numbers.Measure(new Size(80, 100));
            numbers.Arrange(new Rect(0, 0, 80, 100));
            Assert.Equal(0, editor.LineCount);

            var drawing = Render(numbers);
            Assert.Single(drawing.Children); // ausschließlich Hintergrund
            Assert.IsType<GeometryDrawing>(drawing.Children[0]);
        });
    }

    [Fact]
    public void Rendern_nach_Scrollen_und_Textkuerzung_zeichnet_gueltige_Zeilen()
    {
        Sta.Run(dispatcher =>
        {
            var control = new NumberedCodeEditor
            {
                Text = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"Zeile {i}"))
            };
            var window = Sta.ShowHost(control);
            try
            {
                Sta.Flush(dispatcher);
                var editor = control.Children.OfType<TextBox>().Single();
                var numbers = control.Children.OfType<FrameworkElement>().First();
                editor.ScrollToEnd();
                Sta.Flush(dispatcher);
                Assert.True(editor.GetFirstVisibleLineIndex() > 0);
                Assert.NotEmpty(Glyphs(Render(numbers)));

                control.Text = "kurz";
                Sta.Flush(dispatcher);
                Assert.Equal(1, editor.LineCount);
                Assert.Single(Glyphs(Render(numbers)));

                control.Text = "";
                Sta.Flush(dispatcher);
                Assert.Single(Glyphs(Render(numbers)));
            }
            finally { window.Close(); }
        });
    }

    private static DrawingGroup Render(FrameworkElement numbers)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            numbers.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(numbers, new object[] { context });
            // Bei fehlendem Pop würde diese Markierung ebenfalls im Clip stecken.
            context.DrawRectangle(Brushes.Red, null, new Rect(1000, 1000, 1, 1));
        }
        var marker = Assert.IsType<GeometryDrawing>(drawing.Children.Last());
        Assert.Equal(new Rect(1000, 1000, 1, 1), marker.Bounds);
        return Assert.IsType<DrawingGroup>(drawing.Children.First());
    }

    private static GlyphRunDrawing[] Glyphs(DrawingGroup drawing) => drawing.Children
        .SelectMany(child => child is DrawingGroup group ? Glyphs(group) :
            child is GlyphRunDrawing glyph ? new[] { glyph } : Array.Empty<GlyphRunDrawing>()).ToArray();
}
