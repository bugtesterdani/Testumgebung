namespace Testumgebung.Domain.Labels.CabJScript;

public enum CabPropertyControl { Number, Selection, Text, Name, Path, Options, Resource, Answer }
public sealed record CabPropertyChoice(string Value, string Description, CabAvailability Availability)
{
    public bool IsEnabled => Availability != CabAvailability.Unsupported;
    public override string ToString() => Value + " · " + Description;
}

public sealed record CabPropertyDefinition(CabParameterRule Rule, CabPrinterGeneration Generation)
{
    public string Name => Rule.Parameter.Name;
    public CabAvailability Availability => Rule.Parameter.Availability(Generation, Rule.Entry.Availability(Generation));
    public bool IsEnabled => !Rule.Parameter.IsAnswer && Availability != CabAvailability.Unsupported;
    public bool IsOptional => !Rule.Required;
    public CabNumericRange? Range => Rule.GenerationRanges.TryGetValue(Generation, out var range) ? range : Rule.NumericRange;
    public string RangeText => Range is not { } range ? "" : string.Join(" · ", Bounds(range));
    private static IEnumerable<string> Bounds(CabNumericRange range)
    {
        if (range.Minimum is { } minimum) yield return (range.MinimumExclusive ? "> " : "≥ ") + minimum.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (range.Maximum is { } maximum) yield return (range.MaximumExclusive ? "< " : "≤ ") + maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    public string ControlText => Control switch
    {
        CabPropertyControl.Number => "Zahl", CabPropertyControl.Selection => "Auswahl", CabPropertyControl.Name => "Name",
        CabPropertyControl.Path => "Pfad", CabPropertyControl.Options => "Optionen", CabPropertyControl.Resource => "Dateidaten",
        CabPropertyControl.Answer => "Druckerantwort", _ => "Text",
    };
    public string AvailabilityText => Availability switch
    {
        CabAvailability.Supported => "verfügbar", CabAvailability.Unsupported => "nicht verfügbar", _ => "Verfügbarkeit nicht bestätigt",
    };
    public IReadOnlyList<CabPropertyChoice> Choices => Rule.Parameter.Values
        .Select(v => new CabPropertyChoice(v.Value, v.Description, v.Availability(Generation, Availability))).ToArray();
    public string Help => CabSourceCatalog.Tooltip(Rule.Entry) + "\n" + Rule.Parameter.Spelling + ": " + Rule.Parameter.Description;
    public string Unit => Rule.Unit switch
    {
        CabParameterUnit.CurrentLength => "aktuelle Längeneinheit", CabParameterUnit.CurrentSpeed => "aktuelle Geschwindigkeitseinheit",
        CabParameterUnit.Millimetres => "mm", CabParameterUnit.Degrees => "°", CabParameterUnit.Milliseconds => "ms",
        CabParameterUnit.Percent => "%", CabParameterUnit.Points => "pt", CabParameterUnit.Bytes => "Bytes",
        CabParameterUnit.Count => "Anzahl", _ => "",
    };
    public CabPropertyControl Control
    {
        get
        {
            if (Rule.Parameter.IsAnswer) return CabPropertyControl.Answer;
            if (Rule.ChoiceMode is CabChoiceMode.CommaList or CabChoiceMode.PlusList or CabChoiceMode.GraphicList) return CabPropertyControl.Options;
            var type = Rule.GenerationDataTypes.GetValueOrDefault(Generation, Rule.DataType);
            return type switch
            {
                CabDataType.Number or CabDataType.Integer => CabPropertyControl.Number,
                CabDataType.Choice => CabPropertyControl.Selection, CabDataType.Name => CabPropertyControl.Name,
                CabDataType.Path => CabPropertyControl.Path,
                CabDataType.Binary when Rule.Pattern is null => CabPropertyControl.Resource,
                _ => CabPropertyControl.Text,
            };
        }
    }
}

public static class CabPropertySchema
{
    public static IReadOnlyList<CabPropertyDefinition> ForEntry(string id, CabPrinterGeneration generation)
        => CabCatalog.Default[id].Parameters.Select(p => new CabPropertyDefinition(CabDefinitions.ParameterRule(id, p.Name), generation)).ToArray();

    public static IReadOnlyList<CabPropertyDefinition> All(CabPrinterGeneration generation)
        => CabCatalog.Default.Entries.SelectMany(e => ForEntry(e.Id, generation)).ToArray();
}
