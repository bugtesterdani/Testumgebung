using System.Globalization;
using System.Text.RegularExpressions;

namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabDataType { Number, Integer, Choice, Text, Name, Path, Binary }
public enum CabParameterUnit { None, CurrentLength, CurrentSpeed, Millimetres, Degrees, Milliseconds, Percent, Points, Bytes, Count }
public sealed record CabNumericRange(decimal? Minimum = null, decimal? Maximum = null,
    bool MinimumExclusive = false, bool MaximumExclusive = false)
{
    public bool Contains(decimal value)
        => (Minimum is null || (MinimumExclusive ? value > Minimum : value >= Minimum))
            && (Maximum is null || (MaximumExclusive ? value < Maximum : value <= Maximum));
}

/// <summary>A value rule describes an actual input, independently of its source spelling or UI control.</summary>
public sealed record CabParameterRule(CabCatalogEntry Entry, CabCatalogParameter Parameter, CabDataType DataType,
    CabParameterUnit Unit = CabParameterUnit.None, bool Required = false)
{
    public CabNumericRange? NumericRange { get; init; }
    public IReadOnlyDictionary<CabPrinterGeneration, CabNumericRange> GenerationRanges { get; init; }
        = new Dictionary<CabPrinterGeneration, CabNumericRange>();
    public int? MinimumLength { get; init; }
    public int? MaximumLength { get; init; }
    public IReadOnlyDictionary<CabPrinterGeneration, int> GenerationMaximumLengths { get; init; }
        = new Dictionary<CabPrinterGeneration, int>();
    public string? Pattern { get; init; }
    public string? NumericPrefix { get; init; }
    public bool AllowKnownValues { get; init; }
    public bool AllowFieldReference { get; init; }
    public bool EvenInteger { get; init; }
    public string? Literal { get; init; }
    public IReadOnlyList<string> AdditionalValues { get; init; } = [];
    public CabChoiceMode ChoiceMode { get; init; }
    public bool NumericChoices { get; init; }
    public bool AllowBarePrefix { get; init; }
    public IReadOnlyDictionary<CabPrinterGeneration, CabDataType> GenerationDataTypes { get; init; }
        = new Dictionary<CabPrinterGeneration, CabDataType>();

    public bool RecognizesDeclaredValue(string value)
        => Parameter.Values.Any(v => v.Value == value) || AdditionalValues.Contains(value, StringComparer.Ordinal);

    public string? Validate(string? value, CabPrinterGeneration generation = CabPrinterGeneration.X4)
    {
        if (value is null) return Required ? "Pflichtparameter fehlt." : null;
        if (AllowFieldReference && Regex.IsMatch(value, @"^\[\p{L}[\p{L}\p{Nd}]*\]$", RegexOptions.CultureInvariant)) return null;
        if (AllowKnownValues && RecognizesDeclaredValue(value) && !CabChoiceLexicon.IsSchematic(value)) return null;
        if (Literal is not null && value != Literal) return "Erwartet wird " + Literal + ".";
        if (MinimumLength is int min && value.Length < min) return "Wert ist zu kurz.";
        var maximum = GenerationMaximumLengths.GetValueOrDefault(generation, MaximumLength ?? int.MaxValue);
        if (value.Length > maximum) return "Wert ist zu lang.";
        if (Pattern is not null && !Regex.IsMatch(value, Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            return "Wert entspricht nicht dem dokumentierten Zeichenformat.";
        return ValidateType(value, generation);
    }

    private string? ValidateType(string value, CabPrinterGeneration generation)
    {
        var type = GenerationDataTypes.GetValueOrDefault(generation, DataType);
        if (type == CabDataType.Name && !Regex.IsMatch(value, @"^\p{L}[\p{L}\p{Nd}]*$", RegexOptions.CultureInvariant))
            return "Name muss mit einem Buchstaben beginnen und darf nur Buchstaben/Ziffern enthalten.";
        if (ChoiceMode != CabChoiceMode.None && !CabChoiceLexicon.Matches(this, value, ChoiceMode)) return "Unbekannter Auswahlwert.";
        if (type == CabDataType.Choice && ChoiceMode == CabChoiceMode.None && !CabChoiceLexicon.Matches(this, value, CabChoiceMode.Single)) return "Unbekannter Auswahlwert.";
        if (type is not (CabDataType.Number or CabDataType.Integer)) return null;
        value = value.Trim();
        if (AllowBarePrefix && value == NumericPrefix) return null;
        if (NumericPrefix is not null && value.StartsWith(NumericPrefix, StringComparison.Ordinal)) value = value[NumericPrefix.Length..];
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return "Invariante Dezimalzahl erwartet.";
        if (type == CabDataType.Integer && number != decimal.Truncate(number)) return "Ganzzahl erwartet.";
        if (EvenInteger && number % 2 != 0) return "Gerade Ganzzahl erwartet.";
        var range = GenerationRanges.TryGetValue(generation, out var specific) ? specific : NumericRange;
        if (range is not null && !range.Contains(number)) return "Wert liegt außerhalb des dokumentierten Bereichs.";
        return null;
    }
}
