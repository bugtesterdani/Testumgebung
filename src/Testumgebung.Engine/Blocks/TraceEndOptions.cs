namespace Testumgebung.Engine.Blocks;

/// <summary>One presentation choice, preserving the execution flags of legacy projects.</summary>
public static class TraceEndOptions
{
    public const string Parameter = "NachAufnahmeende";
    public const string Continue = "Weiter mit nächstem Schritt", Early = "Exit bei vorzeitigem Ende",
        Error = "Exit bei ERROR", EarlyOrError = "Exit bei vorzeitigem Ende oder ERROR", Always = "Immer Exit (PASS / FAIL / ERROR)";
    public static readonly IReadOnlyList<string> Choices = [Continue, Early, Error, EarlyOrError, Always];

    public static bool Applies(string? type) => type is TraceBlocks.Record or TraceBlocks.Point or TraceBlocks.Stop;
    private static bool IsFlag(string name) => name is TraceBlocks.ExitDutOnEarlyStopParameter
        or TraceBlocks.ExitDutOnEndParameter or BlockCatalog.ExitDutOnErrorParameter;
    private static bool Read(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var text) && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("ja", StringComparison.OrdinalIgnoreCase));

    private static string Mode(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue(Parameter, out var mode))
        {
            if (!Choices.Contains(mode)) throw new BlockConfigurationException("Nach Aufnahmeende: ungültige Auswahl.");
            return mode;
        }
        if (Read(values, TraceBlocks.ExitDutOnEndParameter)) return Always;
        if (Read(values, TraceBlocks.ExitDutOnEarlyStopParameter))
            return Read(values, BlockCatalog.ExitDutOnErrorParameter) ? EarlyOrError : Early;
        return Read(values, BlockCatalog.ExitDutOnErrorParameter) ? Error : Continue;
    }

    public static IReadOnlyDictionary<string, string>? Migrate(string type, IReadOnlyDictionary<string, string>? values)
    {
        if (!Applies(type)) return values;
        var migrated = values is null ? new Dictionary<string, string>() : new Dictionary<string, string>(values);
        // Invalid selections must load with a diagnostic rather than prevent opening the project.
        if (!migrated.ContainsKey(Parameter)) migrated[Parameter] = Mode(migrated);
        return migrated;
    }

    internal static void Validate(string type, IReadOnlyDictionary<string, string> values)
    {
        if (Applies(type)) _ = Mode(values);
    }

    internal static bool TryFlag(string type, IReadOnlyDictionary<string, string> values, string name, out bool selected)
    {
        selected = false;
        if (!Applies(type) || !IsFlag(name) || !values.ContainsKey(Parameter)) return false;
        var mode = Mode(values);
        selected = name switch
        {
            TraceBlocks.ExitDutOnEndParameter => mode == Always,
            TraceBlocks.ExitDutOnEarlyStopParameter => mode is Early or EarlyOrError,
            _ => mode is Error or EarlyOrError or Always
        };
        return true;
    }

    internal static IReadOnlyList<ParameterDescriptor> Describe(IReadOnlyList<ParameterDescriptor> fields)
        => [.. fields.Select(field => IsFlag(field.Name) ? field with
            { Advanced = true, VisibleWhen = new(Parameter, AllowedValues: []) } : field),
            new(Parameter, "Nach Aufnahmeende", ParameterKind.Choice, Continue, Choices: Choices,
                Hint: "Überspringt bei der gewählten Bedingung reguläre Folgeschritte und führt die Exit-Gruppen aus.",
                Section: "Ende und Exit")];
}
