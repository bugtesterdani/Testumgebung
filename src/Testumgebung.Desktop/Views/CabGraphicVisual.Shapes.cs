using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Views;

internal sealed partial class CabGraphicVisual
{
    private void Shape(DrawingContext context)
    {
        var brush = Fill();
        var outline = CabGraphicValues.Text(Item.Command, "options").Contains("[O]", StringComparison.Ordinal);
        switch (Item.Kind)
        {
            case CabGraphicKind.Circle:
                var pen = Item.StrokeX > 0 || outline ? new Pen(brush, outline ? 1 : Item.StrokeX) : null;
                var inset = pen?.Thickness / 2 ?? 0;
                context.DrawEllipse(pen is null ? brush : null, pen, new(0, 0), Math.Max(0, Item.ContentBounds.Width / 2 - inset), Math.Max(0, Item.ContentBounds.Height / 2 - inset));
                break;
            case CabGraphicKind.Line: Line(context, brush, outline); break;
            default: Rectangle(context, brush, outline); break;
        }
    }

    private void Rectangle(DrawingContext context, Brush brush, bool outline)
    {
        var outer = Rect(Item.ContentBounds);
        if (outline) { context.DrawRectangle(null, new Pen(brush, 1), new(outer.X + .5, outer.Y + .5, Math.Max(0, outer.Width - 1), Math.Max(0, outer.Height - 1))); return; }
        if (Item.StrokeX <= 0 && Item.StrokeY <= 0) { context.DrawRectangle(brush, null, outer); return; }
        var inner = new Rect(outer.X + Item.StrokeX, outer.Y + Item.StrokeY, Math.Max(0, outer.Width - 2 * Item.StrokeX), Math.Max(0, outer.Height - 2 * Item.StrokeY));
        context.DrawGeometry(brush, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(outer), new RectangleGeometry(inner)));
    }

    private void Line(DrawingContext context, Brush brush, bool outline)
    {
        var width = Math.Max(.01, Item.StrokeX); var length = CabGraphicValues.Length(Item.Command, "length", _dpi);
        var start = CabGraphicValues.Text(Item.Command, "start"); var end = CabGraphicValues.Text(Item.Command, "end");
        var pen = new Pen(brush, outline ? 1 : width)
        {
            StartLineCap = start == "r" ? PenLineCap.Round : PenLineCap.Square,
            EndLineCap = end == "r" ? PenLineCap.Round : PenLineCap.Square,
        };
        var dash = CabGraphicValues.Length(Item.Command, "D.line", _dpi);
        if (dash > 0) pen.DashStyle = new([dash / pen.Thickness, CabGraphicValues.Length(Item.Command, "D.gap", _dpi) / pen.Thickness], 0);
        context.DrawLine(pen, new(0, 0), new(length, 0));
        if (start == "a") Arrow(context, brush, -width / 2, width * 2, width);
        if (end == "a") Arrow(context, brush, length + width / 2, length - width * 2, width);
    }

    private static void Arrow(DrawingContext context, Brush brush, double tip, double basis, double width)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        { drawing.BeginFigure(new(tip, 0), true, true); drawing.LineTo(new(basis, -width / 2), true, false); drawing.LineTo(new(basis, width / 2), true, false); }
        context.DrawGeometry(brush, null, geometry);
    }

    private Brush Fill()
    {
        var fill = CabGraphicValues.Text(Item.Command, "F.value");
        if (Item.FillPattern is { PixelWidth: 32, PixelHeight: 32 } pattern)
        {
            try
            {
                return new ImageBrush(DecodeImage(pattern)) { TileMode = TileMode.Tile, Viewport = new(0, 0, 32, 32), ViewportUnits = BrushMappingMode.Absolute };
            }
            catch (Exception error) when (error is System.IO.IOException or NotSupportedException or ArgumentException) { return Brushes.Gray; }
        }
        if (double.TryParse(fill.TrimEnd('%'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var density)) return Gray(density);
        if (CabGraphicValues.Present(Item.Command, "S.start"))
        {
            var start = CabGraphicValues.Number(Item.Command, "S.start");
            if (!CabGraphicValues.Present(Item.Command, "S.end")) return Gray(start);
            var angle = CabGraphicValues.Number(Item.Command, "S.direction") * Math.PI / 180;
            return new LinearGradientBrush(GrayColor(start), GrayColor(CabGraphicValues.Number(Item.Command, "S.end")), new(.5 - Math.Cos(angle) / 2, .5 + Math.Sin(angle) / 2), new(.5 + Math.Cos(angle) / 2, .5 - Math.Sin(angle) / 2));
        }
        return fill.Length == 0 ? Ink : Pattern(fill);
    }
    private static Color GrayColor(double density)
    { var value = (byte)Math.Clamp(Math.Round(255 * (1 - density / 100)), 0, 255); return Color.FromRgb(value, value, value); }
    private static Brush Gray(double density) => new SolidColorBrush(GrayColor(density));

    private static Brush Pattern(string name)
    {
        var group = new DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.DrawRectangle(Brushes.White, null, new(0, 0, 8, 8));
            var pen = new Pen(Brushes.Black, 1);
            switch (name)
            {
                case "left": drawing.DrawLine(pen, new(0, 0), new(8, 8)); break;
                case "right": drawing.DrawLine(pen, new(0, 8), new(8, 0)); break;
                case "dots": drawing.DrawEllipse(Brushes.Black, null, new(4, 4), 1, 1); break;
                case "grid": drawing.DrawLine(pen, new(0, 4), new(8, 4)); drawing.DrawLine(pen, new(4, 0), new(4, 8)); break;
                case "diamond": drawing.DrawGeometry(null, pen, Geometry.Parse("M4,0 L8,4 4,8 0,4Z")); break;
                default: drawing.DrawRectangle(Brushes.Gray, null, new(0, 0, 8, 8)); break;
            }
        }
        return new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
    }
}
