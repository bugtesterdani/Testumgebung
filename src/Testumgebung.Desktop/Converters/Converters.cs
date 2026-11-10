/**
 * @file
 * @brief Übersetzt Zustände und Werte für WPF-Bindings; die fachliche Bewertung wird nicht hier berechnet.
 */

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Testumgebung.Contracts;
using Testumgebung.Presentation.ViewModels;

namespace Testumgebung.Desktop.Converters;

internal static class Brushes2
{
    public static readonly SolidColorBrush Pass = Freeze(0x1E, 0x8E, 0x3E);
    public static readonly SolidColorBrush Fail = Freeze(0xD3, 0x2F, 0x2F);
    public static readonly SolidColorBrush Warn = Freeze(0xE8, 0xA2, 0x00);
    public static readonly SolidColorBrush Idle = Freeze(0x9A, 0xA5, 0xB1);
    public static readonly SolidColorBrush Primary = Freeze(0x15, 0x65, 0xD8);
    public static readonly SolidColorBrush Text = Freeze(0x14, 0x24, 0x3A);
    public static readonly SolidColorBrush Running = Freeze(0xE2, 0xED, 0xFC);
    public static readonly SolidColorBrush Paused = Freeze(0xFD, 0xEC, 0xEC);

    private static SolidColorBrush Freeze(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Bewertung → Farbe (PASS grün, FAIL/ERROR rot, NRESULT neutral).</summary>
public sealed class RatingToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ResultRating.Pass => Brushes2.Pass,
        ResultRating.Fail or ResultRating.Error => Brushes2.Fail,
        _ => Brushes2.Text,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bewertung → Text (PASS, FAIL, …) bzw. „—“.</summary>
public sealed class RatingToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ResultRating rating ? rating.ToDisplay() : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Bewertung → Glyphe (Häkchen, Kreuz, Ausrufezeichen).</summary>
public sealed class RatingToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ResultRating.Pass => "\uE73E",
        ResultRating.Fail => "\uE711",
        ResultRating.Error => "\uE783",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StationStatusKind.Running => Brushes2.Pass,
        StationStatusKind.Waiting => Brushes2.Warn,
        StationStatusKind.Paused => Brushes2.Primary,
        StationStatusKind.Aborting => Brushes2.Fail,
        _ => Brushes2.Idle,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Zustand der Ablaufübersicht → Glyphe.</summary>
public sealed class ItemStateToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ItemState.Done => "\uE73E",
        ItemState.Running => "\uE768",
        ItemState.Skipped => "\uE8D8",
        ItemState.Aborted => "\uE711",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ItemStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ItemState.Done => Brushes2.Pass,
        ItemState.Running => Brushes2.Primary,
        ItemState.Aborted => Brushes2.Fail,
        _ => Brushes2.Idle,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Kartensymbol → Segoe-Glyphe.</summary>
public sealed class NodeIconToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        NodeIcon.Section => "\uE8B7",
        NodeIcon.Group => "\uE7B8",
        NodeIcon.DutGroup => "\uE7B8",
        NodeIcon.ExitGroup => "\uE7E8",
        NodeIcon.Measure => "\uE9D2",
        NodeIcon.Arrow => "\uE72A",
        NodeIcon.Doc => "\uE8A5",
        NodeIcon.Check => "\uE73A",
        NodeIcon.Operator => "\uE77B",
        NodeIcon.Wait => "\uE823",
        NodeIcon.Error => "\uE783",
        NodeIcon.Digital => "\uE8AB",
        NodeIcon.Code => "\uE943",
        NodeIcon.LibraryBlock => "\uE74C",
        _ => "\uE8A5",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Laufmarkierung → Hintergrund der Karte.</summary>
public sealed class RunMarkerToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RunMarker.Running => Brushes2.Running,
        RunMarker.Paused => Brushes2.Paused,
        _ => System.Windows.Media.Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → Visibility; Parameter "invert" kehrt um.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>null/leer → Collapsed; Parameter "invert" kehrt um.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string text => text.Length > 0,
            System.Collections.ICollection iCollection => iCollection.Count > 0,
            _ => true,
        };
        if (parameter is "invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum-Wert == Parameter → true (für Navigations-RadioButtons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string text ? Enum.Parse(targetType, text) : Binding.DoNothing;
}

/// <summary>Anzeigetexte für Enums der Gruppeneigenschaften.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<object, string> DisplayNames = new Dictionary<object, string>
    {
        [Testumgebung.Domain.Definitions.BreakpointMode.Always] = "Immer",
        [Testumgebung.Domain.Definitions.BreakpointMode.FailOrError] = "Bei FAIL oder ERROR",
        [Testumgebung.Domain.Definitions.BreakpointMode.Fail] = "Nur bei FAIL",
        [Testumgebung.Domain.Definitions.BreakpointMode.Error] = "Nur bei ERROR",
        [Testumgebung.Domain.Definitions.BreakpointMode.Expression] = "Wenn Ausdruck wahr ist",
        [Testumgebung.Domain.Definitions.RepeatMode.Once] = "Einmal",
        [Testumgebung.Domain.Definitions.RepeatMode.Count] = "Anzahl",
        [Testumgebung.Domain.Definitions.RepeatMode.Duration] = "Zeitspanne",
        [Testumgebung.Domain.Definitions.RepeatMode.While] = "Solange",
        [Testumgebung.Domain.Definitions.RepeatMode.Until] = "Bis",
        [Testumgebung.Domain.Definitions.RepeatMode.Endless] = "Endlos (bis Werkerabbruch)",
        [Testumgebung.Domain.Definitions.RepeatResultPolicy.Keep] = "Ergebnis behalten",
        [Testumgebung.Domain.Definitions.RepeatResultPolicy.ResetToNResult] = "Auf NRESULT zurücksetzen",
        [Testumgebung.Hardware.CommKind.Stream] = "Byte-Strom (Seriell/TCP/UDP)",
        [Testumgebung.Hardware.CommKind.Modbus] = "Modbus",
        [Testumgebung.Hardware.CommKind.Can] = "CAN / CAN FD / UDS",
        [Testumgebung.Hardware.CommKind.Lin] = "LIN",
        [Testumgebung.Hardware.CommKind.DoIp] = "DoIP (UDS über Ethernet)",
        [Testumgebung.Hardware.CommKind.OpcUa] = "OPC UA",
        [Testumgebung.Domain.Definitions.ProgressPosition.Top] = "Oben",
        [Testumgebung.Domain.Definitions.ProgressPosition.Left] = "Links",
        [Testumgebung.Domain.Definitions.ProgressPosition.Bottom] = "Unten",
        [Testumgebung.Domain.Code.PortDirection.Input] = "Eingang",
        [Testumgebung.Domain.Code.PortDirection.Output] = "Ausgang",
        [Testumgebung.Domain.Code.PortDirection.InOut] = "Ein-/Ausgang",
        [VarType.Bool] = "bool",
        [VarType.Int] = "int",
        [VarType.Double] = "double",
        [VarType.String] = "string",
        [VarType.DateTime] = "DateTime",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "";
        return DisplayNames.TryGetValue(value, out var text) ? text : value.ToString() ?? "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Tiefe einer Karte → linker Einzug (für flache Listen).</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is int number ? number * 20 : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum-Wert == Parameter → Visible, sonst Collapsed.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Kurvenpunkte des ViewModels → PointCollection für eine Polyline.</summary>
public sealed class PlotPointsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var points = new System.Windows.Media.PointCollection();
        if (value is IEnumerable<Testumgebung.Presentation.ViewModels.PlotPoint> list)
            foreach (var plotPoint in list) points.Add(new Point(plotPoint.X, plotPoint.Y));
        points.Freeze();
        return points;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Kanalindex → Farbe (L1, L2, L3, …).</summary>
public sealed class SeriesBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush[] Brushes =
    [
        Freeze("#1F6FD1"), Freeze("#E07B00"), Freeze("#2E9E5B"), Freeze("#8E44AD"), Freeze("#C0392B"), Freeze("#16A085"),
    ];

    private static System.Windows.Media.Brush Freeze(string color)
    {
        var solidColorBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        solidColorBrush.Freeze();
        return solidColorBrush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Brushes[(value is int index ? index : 0) % Brushes.Length];

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true (Fehler) → FAIL-Farbe, sonst PASS-Farbe.</summary>
public sealed class ErrorBrushConverter : IValueConverter
{
    /// <summary>Parameter "invert": true (bestanden) → PASS-Farbe.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isError = value is true;
        if (parameter is "invert") isError = !isError;
        return Application.Current.FindResource(isError ? "FailBrush" : "PassBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Anteil 0…1 × Parameter (Pixel) → Balkenhöhe, mindestens 1 Pixel bei Werten &gt; 0.</summary>
public sealed class BarHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double number ? Math.Clamp(number, 0, 1) : 0;
        var max = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var position) ? position : 80;
        return fraction <= 0 ? 0.0 : Math.Max(1.0, fraction * max);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
