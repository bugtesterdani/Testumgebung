using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

internal static class CabRuleBounds
{
    internal static CabParameterRule Apply(CabParameterRule rule)
    {
        if (!rule.Parameter.Metadata.TryGetProperty("bereich", out var range)) return rule;
        if (range.ValueKind == JsonValueKind.String) return ApplyText(rule, range.GetString()!);
        if (range.ValueKind != JsonValueKind.Object) return rule;
        var numbers = new Dictionary<CabPrinterGeneration, CabNumericRange>();
        var lengths = new Dictionary<CabPrinterGeneration, int>();
        foreach (var cell in range.EnumerateObject())
        {
            if (!Enum.TryParse<CabPrinterGeneration>(cell.Name, out var generation)) continue;
            var text = cell.Value.GetString()!;
            if (text.Contains("Zeichen", StringComparison.Ordinal))
            {
                var maximum = LengthBounds(text).Maximum;
                if (maximum is int count) lengths[generation] = count;
            }
            else if (NumberBounds(text) is { } numeric) numbers[generation] = numeric;
        }
        return rule with { GenerationRanges = numbers, GenerationMaximumLengths = lengths };
    }

    private static CabParameterRule ApplyText(CabParameterRule rule, string text)
    {
        if (text.Contains("Zeichen", StringComparison.Ordinal))
        {
            var length = LengthBounds(text);
            return rule with { MinimumLength = length.Minimum ?? rule.MinimumLength, MaximumLength = length.Maximum ?? rule.MaximumLength };
        }
        if (rule.DataType is CabDataType.Number or CabDataType.Integer)
            return rule with { NumericRange = NumberBounds(text) ?? rule.NumericRange };
        return rule;
    }

    private static (int? Minimum, int? Maximum) LengthBounds(string text)
    {
        var interval = Regex.Match(text, @"(?<min>\d+)…(?<max>\d+) Zeichen", RegexOptions.CultureInvariant);
        if (interval.Success) return (int.Parse(interval.Groups["min"].Value, CultureInfo.InvariantCulture), int.Parse(interval.Groups["max"].Value, CultureInfo.InvariantCulture));
        var maximum = Regex.Match(text, @"höchstens (?<max>\d+) Zeichen", RegexOptions.CultureInvariant);
        if (maximum.Success) return (null, int.Parse(maximum.Groups["max"].Value, CultureInfo.InvariantCulture));
        return text.Contains("ein Zeichen", StringComparison.Ordinal) ? (text.StartsWith("optional", StringComparison.Ordinal) ? 0 : 1, 1) : (null, null);
    }

    private static CabNumericRange? NumberBounds(string text)
    {
        var matches = Regex.Matches(text, @"(?<min>-?\d+(?:[.,]\d+)?)…(?<max>-?\d+(?:[.,]\d+)?)", RegexOptions.CultureInvariant);
        if (matches.Count == 1)
            return new(Number(matches[0].Groups["min"].Value), Number(matches[0].Groups["max"].Value));
        var maximum = Regex.Match(text, @"höchstens (?<max>\d+)", RegexOptions.CultureInvariant);
        if (maximum.Success) return new(Maximum: Number(maximum.Groups["max"].Value));
        if (text == "positiv") return new(0, MinimumExclusive: true);
        if (text.Contains("nichtnegative", StringComparison.Ordinal) || text.Contains("0 oder positive", StringComparison.Ordinal)) return new(0);
        return null;
    }

    private static decimal Number(string value) => decimal.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
}
