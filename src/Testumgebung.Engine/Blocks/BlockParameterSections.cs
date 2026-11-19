namespace Testumgebung.Engine.Blocks;

/// <summary>Editor grouping belongs to block metadata; execution and saved parameter names are unchanged.</summary>
internal static class BlockParameterSections
{
    internal static ParameterDescriptor Apply(string type, ParameterDescriptor parameter) => type switch
    {
        TraceBlocks.Start or TraceBlocks.Record or TraceBlocks.Point or TraceBlocks.Stop => Trace(parameter),
        BusBlocks.UdsRequest => parameter with { Section = UdsSection(parameter) },
        ProcessBlocks.Start => parameter with { Section = ProcessSections.GetValueOrDefault(parameter.Name, "Ausgabe und Ergebnisse") },
        _ => parameter
    };

    private static string UdsSection(ParameterDescriptor parameter)
    {
        if (parameter.Name is "Ressource" or "Transport") return "Verbindung";
        if (UdsRequestFields.Contains(parameter.Name)) return "Anfrage";
        return parameter.Advanced ? "Transportdetails" : "Antwort und Auswertung";
    }

    private static readonly HashSet<string> UdsRequestFields =
        ["Dienst", "Anfrage", "AnfrageId", "AntwortId", "TesterAdresse", "SteuergeraetAdresse", "Nad"];
    private static readonly HashSet<string> TraceAdvanced =
        ["MaxPunkte", "MaxLuecke", "MinPunkte", "MinDauer", "Beruehren", "Messversuche", "ErgebnisZiel",
         TraceBlocks.ExitDutOnEarlyStopParameter, TraceBlocks.ExitDutOnEndParameter];

    private static ParameterDescriptor Trace(ParameterDescriptor parameter)
    {
        var condition = parameter.Name switch
        {
            "Werte" => new ParameterVisibility("Quelle", "Ausdrücke"),
            "Ressourcen" => new ParameterVisibility("Quelle", "Geräte"),
            _ => parameter.VisibleWhen
        };
        return parameter with
        {
            VisibleWhen = condition,
            Advanced = parameter.Advanced || TraceAdvanced.Contains(parameter.Name),
            Section = TraceSections.GetValueOrDefault(parameter.Name, "Grenzauswertung")
        };
    }

    private static readonly IReadOnlyDictionary<string, string> ProcessSections = Sections(
        ("Programm und Aufruf", ["Art", "Datei", "Argumente", "Typ", "Python"]),
        ("Prozessumgebung", ["Arbeitsverzeichnis", "Umgebung", "Fenster"]),
        ("Lebensdauer und Warten", ["Modus", "Warten", "Zeitlimit"]));

    private static readonly IReadOnlyDictionary<string, string> TraceSections = Sections(
        ("Kanäle und Quelle", ["Aufnahme", "Kanaele", "Einheiten", "Quelle", "Werte", "Ressourcen", "Messversuche"]),
        ("Sicherheit", ["SicherheitMin", "SicherheitMax", TraceBlocks.StopEnabledParameter, TraceBlocks.StopMarginModeParameter,
            TraceBlocks.PercentMarginParameter, TraceBlocks.AbsoluteMarginParameter, TraceBlocks.AbortCurvesParameter]),
        ("Zeit und Vollständigkeit", ["Dauer", "Zeit", "Intervall", "MaxPunkte", "MaxLuecke", "MinPunkte", "MinDauer"]),
        ("Zielerkennung", [TraceBlocks.TargetEnabledParameter, "ZielbandMin", "ZielbandMax", "ZielbandPunkte", "StabilesZiel"]),
        ("Ende und Exit", ["ErgebnisZiel", "VorzeitigesEndeZiel", TraceBlocks.ExitDutOnEarlyStopParameter, TraceBlocks.ExitDutOnEndParameter]));

    private static IReadOnlyDictionary<string, string> Sections(params (string Title, string[] Fields)[] groups)
        => groups.SelectMany(group => group.Fields.Select(field => (field, group.Title)))
            .ToDictionary(entry => entry.field, entry => entry.Title, StringComparer.Ordinal);
}
