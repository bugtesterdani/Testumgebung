using System.Globalization;
using Testumgebung.Domain.Labels.CabJScript;

namespace Testumgebung.Presentation.ViewModels;

public static class CabGraphicValues
{
    public static double Number(CabBoundCommand command, string name, double fallback = 0)
    {
        var parameter = command.Parameter(name);
        var text = parameter?.Value?.Trim();
        if (parameter?.Rule.NumericPrefix is { Length: > 0 } prefix && text?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true) text = text[prefix.Length..];
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? (double)value : fallback;
    }
    public static double Length(CabBoundCommand command, string name, int dpi, double fallback = 0)
        => command.Parameter(name)?.IsPresent == true ? Dots(Number(command, name), command.Syntax.Unit, dpi) : fallback;
    public static double Dots(double value, CabUnit unit, int dpi) => value * dpi / (unit == CabUnit.Inches ? 1 : 25.4);
    public static string Text(CabBoundCommand command, string name) => command.Parameter(name)?.Value ?? "";
    public static bool Present(CabBoundCommand command, string name) => command.Parameter(name)?.IsPresent == true;
    public static double FontSize(CabBoundCommand command, int dpi)
        => Present(command, "size.pt") ? Number(command, "size.pt", 10) * dpi / 72 : Length(command, "size", dpi, dpi * 3 / 25.4);
    public static IReadOnlySet<string> Effects(CabBoundCommand command)
        => Text(command, "effects").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}
