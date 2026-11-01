/**
 * @file
 * @brief Liest bei bedingten nativen Haltepunkten Werte vor der Halteentscheidung; die Inspektion wird wiederverwendet.
 */
using System.Globalization;
using Testumgebung.Domain.Code;
namespace Testumgebung.Engine;
internal static class NativeBreakpointContext
{
    public static async Task<CodeDebugEvent> PrepareAsync(CodeDebugEvent e, bool inspect, CancellationToken token)
    {
        if (!inspect || e.Kind != "line" || e.InspectNativeAsync is null) return e;
        var native = await e.InspectNativeAsync(token).ConfigureAwait(false);
        var values = e.Variables.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        foreach (var (name, value) in native.Variables) values[name] = Scalar(value);
        return e with { Variables = values, InspectNativeAsync = _ => Task.FromResult(native) };
    }
    private static object Scalar(string value)
    {
        if (bool.TryParse(value, out var flag)) return flag;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return value;
    }
}
