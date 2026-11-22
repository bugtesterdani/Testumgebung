using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

/// <summary>Draws in printer dots; the outer editor scales the whole sheet uniformly.</summary>
internal sealed partial class CabGraphicVisual : FrameworkElement
{
    internal CabGraphicItem Item { get; }
    private readonly int _dpi;
    internal CabGraphicVisual(CabGraphicItem item, int dpi)
    {
        Item = item; _dpi = dpi;
        Width = Math.Max(1, item.LocalBounds.Width); Height = Math.Max(1, item.LocalBounds.Height);
        ToolTip = item.Command.Definition.Catalog.Title + "\n" + string.Join("\n", item.Notes);
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Item.Hidden) return;
        var bounds = Item.LocalBounds;
        context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
        if (Inverse()) context.DrawRectangle(Brushes.Black, null, Rect(bounds));
        switch (Item.Kind)
        {
            case CabGraphicKind.Text: Text(context); break;
            case CabGraphicKind.RichText: Rich(context); break;
            case CabGraphicKind.Barcode: Barcode(context); break;
            case CabGraphicKind.Image: Image(context); break;
            default: Shape(context); break;
        }
        Frame(context);
        context.Pop();
    }
    internal static Rect Rect(CabGraphicRect rect) => new(rect.X, rect.Y, Math.Max(.01, rect.Width), Math.Max(.01, rect.Height));
    private bool Inverse() => CabGraphicValues.Effects(Item.Command).Contains("n") || CabGraphicValues.Text(Item.Command, "fx").Split(',').Contains("n");
    private Brush Ink => Inverse() ? Brushes.White : Brushes.Black;

    private void Frame(DrawingContext context)
    {
        var prefix = Item.Kind == CabGraphicKind.Barcode ? "fx." : "effects.";
        if (new[] { "fl", "fu", "fr", "fd" }.Any(name => CabGraphicValues.Length(Item.Command, prefix + name, _dpi) > 0))
            context.DrawRectangle(null, new Pen(Ink, 1), Rect(Item.LocalBounds));
    }
    private void Text(DrawingContext context)
    {
        var effects = CabGraphicValues.Effects(Item.Command);
        var font = CabGraphicValues.Number(Item.Command, "font", 3);
        var family = font == 596 || font < 0 ? "Consolas" : "Arial";
        var weight = TextWeight(effects, font);
        var italic = effects.Contains("i") || effects.Contains("s") || effects.Contains("z");
        var typeface = new Typeface(new FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal, weight, FontStretches.Normal);
        var text = effects.Contains("v") ? string.Join("\n", Item.Text.EnumerateRunes().Select(r => r.ToString())) : Item.Text;
        var formatted = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight,
            typeface, Math.Max(1, Item.FontHeight), effects.Contains("g") ? Brushes.Gray : Ink, 1);
        if (effects.Contains("u")) formatted.SetTextDecorations(TextDecorations.Underline);
        var rect = Item.ContentBounds;
        var scale = new ScaleTransform(rect.Width / Math.Max(1, formatted.WidthIncludingTrailingWhitespace), rect.Height / Math.Max(1, formatted.Height));
        context.PushTransform(new TranslateTransform(rect.X, rect.Y)); context.PushTransform(scale);
        if (effects.Contains("o")) context.DrawGeometry(null, new Pen(Ink, .8), formatted.BuildGeometry(new(0, 0)));
        else context.DrawText(formatted, new(0, 0));
        context.Pop(); context.Pop();
    }
    private static FontWeight TextWeight(IReadOnlySet<string> effects, double font)
        => effects.Contains("b") || font is 5 or 7 ? FontWeights.Bold : effects.Contains("l") ? FontWeights.Light : FontWeights.Normal;
    private void Rich(DrawingContext context)
    {
        var document = new FlowDocument { PagePadding = new(0), FontFamily = new("Arial"), FontSize = Math.Max(1, Item.FontHeight), Foreground = Ink,
            PageWidth = Item.ContentBounds.Width, PageHeight = Item.ContentBounds.Height, ColumnWidth = Item.ContentBounds.Width, ColumnGap = 0 };
        var paragraph = new Paragraph { Margin = new(0) }; document.Blocks.Add(paragraph);
        if (CabGraphicValues.Present(Item.Command, "line-spacing")) document.LineHeight = Math.Max(1, Item.FontHeight + CabGraphicValues.Length(Item.Command, "line-spacing", _dpi));
        foreach (var token in CabRichText.Parse(Item.Text))
        {
            if (token.Paragraph) { paragraph = new Paragraph { Margin = new(0) }; document.Blocks.Add(paragraph); }
            AddRun(paragraph, token, document.FontSize, Item.ContentBounds.Width);
        }
        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new(Item.ContentBounds.Width, Item.ContentBounds.Height);
        var page = paginator.GetPage(0);
        context.PushClip(new RectangleGeometry(Rect(Item.ContentBounds)));
        context.DrawRectangle(new VisualBrush(page.Visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, Rect(Item.ContentBounds));
        context.Pop();
    }
    private static void AddRun(Paragraph block, CabRichRun token, double fontSize, double width)
    {
        if (token.Break && !token.Paragraph) block.Inlines.Add(new LineBreak());
        if (token.Rule) block.Inlines.Add(new InlineUIContainer(new Border { Width = Math.Max(1, width), Height = 1, Background = Brushes.Black }));
        if (token.Text.Length == 0) return;
        var style = token.Style;
        var run = new Run(token.Text) { FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal, FontSize = fontSize * style.Scale, TextDecorations = new(),
            BaselineAlignment = style.Baseline > 0 ? BaselineAlignment.Superscript : style.Baseline < 0 ? BaselineAlignment.Subscript : BaselineAlignment.Baseline };
        if (style.Underline) run.TextDecorations.Add(TextDecorations.Underline[0]);
        if (style.Strike) run.TextDecorations.Add(TextDecorations.Strikethrough[0]);
        block.TextAlignment = style.Alignment switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
        block.Inlines.Add(run);
    }
    private void Barcode(DrawingContext context)
    {
        HumanReadable(context);
        if (Item.Barcode is not { Modules: { } modules } symbol) { Placeholder(context, Item.Command.Definition.Catalog.Title + "\nVorschaufläche\n" + Item.Text); return; }
        var rect = Item.ContentBounds;
        var dx = rect.Width / symbol.Columns; var dy = rect.Height / symbol.Rows;
        for (var y = 0; y < symbol.Rows; y++)
            for (var x = 0; x < symbol.Columns; x++)
                if (modules[y * symbol.Columns + x]) context.DrawRectangle(Ink, null, new(rect.X + x * dx, rect.Y + y * dy, dx, dy));
    }
    private void HumanReadable(DrawingContext context)
    {
        if (Item.HumanReadableHeight <= 0) return;
        var text = new FormattedText(Item.Text.Length == 0 ? " " : Item.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"),
            Math.Max(1, Item.HumanReadableHeight * .8), Ink, 1) { MaxTextWidth = Math.Max(1, Item.ContentBounds.Width), MaxTextHeight = Math.Max(1, Item.HumanReadableHeight) };
        context.DrawText(text, new(Item.ContentBounds.X, Item.ContentBounds.Bottom));
    }
    private void Image(DrawingContext context)
    {
        if (Item.Image is not { PixelWidth: > 0, PixelHeight: > 0 } image || (long)image.PixelWidth * image.PixelHeight > 16_777_216)
        { Placeholder(context, "Bild: " + Item.Text + "\nVorschaufläche"); return; }
        try
        {
            var bitmap = DecodeImage(image);
            context.DrawImage(bitmap, Rect(Item.ContentBounds));
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException)
        { Placeholder(context, "Bild: " + Item.Text + "\nNicht dekodierbar"); }
    }
    private static BitmapImage DecodeImage(CabGraphicImage image)
    {
        using var input = new MemoryStream(image.Data);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    private void Placeholder(DrawingContext context, string text)
    {
        var rect = Rect(Item.ContentBounds);
        context.DrawRectangle(Brushes.WhiteSmoke, new Pen(Brushes.DimGray, 1) { DashStyle = DashStyles.Dash }, rect);
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("de-DE"), FlowDirection.LeftToRight, new Typeface("Arial"),
            Math.Max(1, Math.Min(12, rect.Height / 3)), Brushes.Black, 1) { MaxTextWidth = Math.Max(1, rect.Width), MaxTextHeight = Math.Max(1, rect.Height) };
        context.PushClip(new RectangleGeometry(rect)); context.DrawText(formatted, rect.TopLeft); context.Pop();
    }
}
