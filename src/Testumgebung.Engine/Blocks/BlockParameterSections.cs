namespace Testumgebung.Engine.Blocks;

/// <summary>Editor grouping belongs to block metadata; execution and saved parameter names are unchanged.</summary>
internal static class BlockParameterSections
{
    internal static ParameterDescriptor Apply(string type, ParameterDescriptor parameter) => type switch
    {
        TraceBlocks.Start or TraceBlocks.Record or TraceBlocks.Point or TraceBlocks.Stop => Trace(parameter),
        BusBlocks.UdsRequest => parameter with { Section = parameter.Name switch
        {
            "Ressource" or "Transport" => "Verbindung",
            "Dienst" or "Anfrage" or "AnfrageId" or "AntwortId" or "TesterAdresse" or "SteuergeraetAdresse" or "Nad" => "Anfrage",
            _ => parameter.Advanced ? "Transportdetails" : "Antwort und Auswertung"
        }},
        ProcessBlocks.Start => parameter with { Section = parameter.Name switch
        {
            "Art" or "Datei" or "Argumente" or "Typ" or "Python" => "Programm und Aufruf",
            "Arbeitsverzeichnis" or "Umgebung" or "Fenster" => "Prozessumgebung",
            "Modus" or "Warten" or "Zeitlimit" => "Lebensdauer und Warten",
            _ => "Ausgabe und Ergebnisse"
        }},
        _ => parameter
    };

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
            Advanced = parameter.Advanced || parameter.Name is "MaxPunkte" or "MaxLuecke" or "MinPunkte" or "MinDauer"
                or "Beruehren" or "Messversuche" or "ErgebnisZiel"
                or TraceBlocks.ExitDutOnEarlyStopParameter or TraceBlocks.ExitDutOnEndParameter,
            Section = Section(parameter.Name)
        };
    }

    private static readonly IReadOnlyDictionary<string, string> TraceSections = CreateTraceSections();
    private static IReadOnlyDictionary<string, string> CreateTraceSections()
    {
        var groups = new (string Title, string[] Fields)[]
        {
            ("Kanäle und Quelle", ["Aufnahme", "Kanaele", "Einheiten", "Quelle", "Werte", "Ressourcen", "Messversuche"]),
            ("Sicherheit", ["SicherheitMin", "SicherheitMax", TraceBlocks.StopEnabledParameter, TraceBlocks.StopMarginModeParameter, TraceBlocks.PercentMarginParameter, TraceBlocks.AbsoluteMarginParameter, TraceBlocks.AbortCurvesParameter]),
            ("Zeit und Vollständigkeit", ["Dauer", "Zeit", "Intervall", "MaxPunkte", "MaxLuecke", "MinPunkte", "MinDauer"]),
            ("Zielerkennung", [TraceBlocks.TargetEnabledParameter, "ZielbandMin", "ZielbandMax", "ZielbandPunkte", "StabilesZiel"]),
            ("Ende und Exit", ["ErgebnisZiel", "VorzeitigesEndeZiel", TraceBlocks.ExitDutOnEarlyStopParameter, TraceBlocks.ExitDutOnEndParameter])
        };
        return groups.SelectMany(group => group.Fields.Select(field => (field, group.Title)))
            .ToDictionary(entry => entry.field, entry => entry.Title, StringComparer.Ordinal);
    }
    private static string Section(string name) => TraceSections.GetValueOrDefault(name, "Grenzauswertung");
}