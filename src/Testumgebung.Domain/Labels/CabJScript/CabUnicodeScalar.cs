using System.Globalization;
using System.Text;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabUnicodeScalar
{
    internal static bool TryParse(string value, bool allowDecimal, out int scalar)
    {
        scalar = -1;
        if (value.StartsWith('$')) return int.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out scalar) && Rune.IsValid(scalar);
        return allowDecimal && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out scalar) && Rune.IsValid(scalar);
    }
}
