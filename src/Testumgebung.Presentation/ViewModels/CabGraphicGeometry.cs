namespace Testumgebung.Presentation.ViewModels;

public readonly record struct CabGraphicPoint(double X, double Y);
public readonly record struct CabGraphicRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public IReadOnlyList<CabGraphicPoint> Corners => [new(X, Y), new(Right, Y), new(Right, Bottom), new(X, Bottom)];
    public CabGraphicRect Inflate(double left, double top, double right, double bottom)
        => new(X - left, Y - top, Width + left + right, Height + top + bottom);
}

/// <summary>Printer coordinates have a downward Y axis; positive JScript angles turn counterclockwise.</summary>
public readonly record struct CabGraphicTransform(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static CabGraphicTransform Identity => new(1, 0, 0, 1, 0, 0);
    public CabGraphicPoint Apply(CabGraphicPoint point)
        => new(point.X * M11 + point.Y * M21 + OffsetX, point.X * M12 + point.Y * M22 + OffsetY);
    public CabGraphicPoint InverseVector(CabGraphicPoint vector)
    {
        var determinant = M11 * M22 - M12 * M21;
        if (Math.Abs(determinant) < 1e-12) throw new InvalidOperationException("Die Vorschautransformation ist nicht umkehrbar.");
        return new((vector.X * M22 - vector.Y * M21) / determinant, (-vector.X * M12 + vector.Y * M11) / determinant);
    }
    public CabGraphicTransform Then(CabGraphicTransform next)
    {
        var origin = next.Apply(new(OffsetX, OffsetY));
        return new(M11 * next.M11 + M12 * next.M21, M11 * next.M12 + M12 * next.M22,
            M21 * next.M11 + M22 * next.M21, M21 * next.M12 + M22 * next.M22, origin.X, origin.Y);
    }
    public CabGraphicRect Bounds(CabGraphicRect rect)
    {
        var points = rect.Corners.Select(Apply).ToArray();
        var x = points.Min(p => p.X); var y = points.Min(p => p.Y);
        return new(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }
    public static CabGraphicTransform Placement(double x, double y, double degrees)
    {
        var radians = -degrees * Math.PI / 180;
        var cosine = Math.Cos(radians); var sine = Math.Sin(radians);
        return new(cosine, sine, -sine, cosine, x, y);
    }
}
