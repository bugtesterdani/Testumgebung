namespace Testumgebung.Presentation.ViewModels;

/// <summary>Assigns distinct side rails when bypass or repeat lines overlap vertically.</summary>
public sealed class GraphRailLayout
{
    public const double Spacing = 28;
    private readonly List<(double X, double Top, double Bottom)> _rails = [];

    public double Allocate(double minimumX, double startY, double endY)
    {
        var top = Math.Min(startY, endY); var bottom = Math.Max(startY, endY);
        var minimumXValue = minimumX;
        while (_rails.Any(rail => Math.Abs(rail.X - minimumXValue) < Spacing && rail.Top <= bottom && top <= rail.Bottom))
            minimumXValue += Spacing;
        _rails.Add((minimumXValue, top, bottom));
        return minimumXValue;
    }
}
