using System.Windows;
using System.Windows.Input;

namespace Testumgebung.Desktop.Views;

/// <summary>Gemeinsame Windows-Ziehschwelle für Ablauf- und Ausdrucksblöcke.</summary>
internal static class VisualDragGesture
{
    public static bool CanStart(MouseButtonState button, object? payload, Point start, Point current)
    {
        if (button != MouseButtonState.Pressed || payload is null) return false;
        var delta = current - start;
        return Math.Max(Math.Abs(delta.X) / SystemParameters.MinimumHorizontalDragDistance,
            Math.Abs(delta.Y) / SystemParameters.MinimumVerticalDragDistance) >= 1;
    }
}
