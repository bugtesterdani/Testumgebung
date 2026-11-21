namespace Testumgebung.Domain.Labels.CabJScript;

public sealed record CabPreviewField(CabBoundContent Content, string? Name, CabContentPreviewResult Preview);

public static class CabDocumentPreview
{
    public static IReadOnlyList<CabPreviewField> Evaluate(CabDocument document, CabPreviewSettings settings,
        CabDocumentSettings? validationSettings = null)
    {
        var validation = CabValidator.Validate(document, validationSettings);
        var contents = CabContentValidation.Bind(document, validation.Commands).ToLookup(c => c.Command);
        var result = new List<CabPreviewField>();
        var fields = new Dictionary<string, CabPreviewValue>(StringComparer.Ordinal);
        var culture = settings.Culture;
        var job = int.MinValue;
        foreach (var command in validation.Commands)
        {
            if (job != command.JobIndex) { fields.Clear(); job = command.JobIndex; }
            if (command.Definition.Catalog.Id == "JS-3.8") culture = CabPreviewLocales.Culture(command.Parameter("name")?.Value, settings.Culture);
            foreach (var content in contents[command])
            {
                var local = Copy(settings, culture, content.Parameter.Parts[0].CodePage);
                var preview = CabContentPreview.EvaluateSource(content.Source, local, fields, command.Definition.Catalog.Kind == "barcode");
                var name = CabDocumentRules.Declaration(command)?.Value;
                if (command.Syntax.Command == "R") name = command.Parameter("name")?.Value;
                result.Add(new(content, name, preview));
                if (name is { Length: > 0 }) fields[name] = preview.Value;
            }
        }
        return result.AsReadOnly();
    }

    private static CabPreviewSettings Copy(CabPreviewSettings source, string culture, string codePage) => new()
    {
        ExampleTime = source.ExampleTime, Culture = culture, InterfaceCodePage = codePage,
        MonthPolicy = source.MonthPolicy, WeekRule = source.WeekRule, FirstDayOfWeek = source.FirstDayOfWeek,
        VisibilityPolicy = source.VisibilityPolicy, SelectionExamples = source.SelectionExamples
    };
}

internal static class CabPreviewLocales
{
    // The printer uses DOS country codes: GR is Germany, GK Greece, SA South Africa.
    private static readonly IReadOnlyDictionary<string, string> Cultures = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BE"]="fr-BE", ["BF"]="nl-BE", ["BG"]="bg-BG", ["CZ"]="cs-CZ", ["DK"]="da-DK", ["EG"]="ar-EG",
        ["FR"]="fr-FR", ["GK"]="el-GR", ["GR"]="de-DE", ["HR"]="hr-HR", ["HU"]="hu-HU", ["IR"]="fa-IR",
        ["IT"]="it-IT", ["LA"]="es-MX", ["LT"]="lt-LT", ["MK"]="mk-MK", ["MX"]="es-MX", ["NL"]="nl-NL",
        ["NO"]="nb-NO", ["PL"]="pl-PL", ["PT"]="pt-PT", ["RO"]="ro-RO", ["RU"]="ru-RU", ["SA"]="en-ZA",
        ["SE"]="sv-SE", ["SF"]="fr-CH", ["SG"]="de-CH", ["SL"]="sl-SI", ["SP"]="es-ES", ["SR"]="sr-Latn-RS",
        ["SU"]="fi-FI", ["TH"]="th-TH", ["TR"]="tr-TR", ["UK"]="en-GB", ["US"]="en-US", ["ZH"]="zh-CN"
    };
    internal static string Culture(string? code, string setup) => code is not null ? Cultures.GetValueOrDefault(code, setup) : setup;
}
