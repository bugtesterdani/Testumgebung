using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public static class TraceBlocks
{
    public const string Start = "verlauf.start", Point = "verlauf.punkt", Stop = "verlauf.bewerten", Record = "verlauf.aufnehmen";
    public const string PauseTime = "verlauf.messzeit";
    public const string AbortCurvesParameter = "Abbruchgrenzkurven";

    public static void Register(BlockCatalog catalog)
    {
        catalog.Register(new(Start, "Messwertverlauf beginnen", "Messwertverläufe", "measure", "Beginnt eine begrenzte Mehrkanalaufnahme. Bei eingestellter Dauer muss das Grenzband den gesamten Zeitraum 0..Dauer abdecken.",
            RecordingParameters(false), parameters =>
            {
                var settings = new TraceRecordingSettings(parameters);
                return new DelegateAction(ctx => { settings.Begin(ctx); return Task.FromResult(StepOutcome.Unrated(message: "Messwertaufzeichnung begonnen.")); });
            }));
        catalog.Register(new(PauseTime, "Messzeit pausieren / fortsetzen", "Messwertverläufe", "wait",
            "Hält die automatische Messzeit einer manuellen Aufnahme an. Bisherige Punkte und Grenzverletzungen bleiben erhalten.",
            [Name(), new("Aktion", "Aktion", ParameterKind.Choice, "Pausieren", Choices: ["Pausieren", "Fortsetzen"])],
            CreatePauseTime));
        catalog.Register(new(Point, "Messpunkt ergänzen", "Messwertverläufe", "measure", "Ergänzt alle Kanäle. Eine Grenzverletzung bleibt FAIL; die zusätzliche Abbruchabweichung beendet nur die Aufnahme. Bereits beendete Aufnahmen werden nicht weiter abgefragt.",
            new[] { Name() }.Concat(ReaderParameters()).Append(new("Zeit", "Zeit [s], leer = seit Aufnahmebeginn", ParameterKind.Expression)).ToArray(), CreatePoint));
        catalog.Register(new(Stop, "Verlauf beenden und bewerten", "Messwertverläufe", "measure", "Bewertet den Verlauf. Normale Grenzverletzungen und vorzeitiges Ende wegen Abweichung ergeben FAIL. Fehlende vollständige Daten ergeben ERROR.",
            [Name(), ResultTarget()], parameters => new DelegateAction(ctx =>
            {
                ValidateTarget(ctx, parameters.Optional("ErgebnisZiel"));
                var result = ctx.Recordings.Stop(ctx.ProcessOwner, parameters.Text("Aufnahme"), !ctx.IsOperatorAborted);
                WriteDetails(ctx, parameters.Optional("ErgebnisZiel"), result);
                return Task.FromResult(Outcome(result, !result.Finished));
            })));
        catalog.Register(new(Record, "Messwertverlauf aufnehmen", "Messwertverläufe", "measure", "Misst über die eingestellte Dauer im Abfragetakt. Jede Grenzverletzung ergibt FAIL; zusätzliche Abweichung beendet diese Aufnahme früher. Danach folgen die nächsten Schritte.",
            RecordingParameters(true).Concat(ReaderParameters()).Concat([new("Intervall", "Abfrageintervall [s]", ParameterKind.Number, "0.1"), ResultTarget()]).ToArray(),
            parameters => new TimedTraceAction(new(parameters), new(parameters), parameters.Number("Intervall") ?? 0, parameters.Optional("ErgebnisZiel"))));
    }
    private static ParameterDescriptor Name() => new("Aufnahme", "Aufnahmekennung", ParameterKind.Text, "Verlauf", Required: true);
    private static ParameterDescriptor ResultTarget() => new("ErgebnisZiel", "Auswertungsdetails → Stringvariable (JSON)", ParameterKind.Variable);
    private static ParameterDescriptor[] ReaderParameters() =>
    [new("Quelle", "Datenquelle", ParameterKind.Choice, "Ausdrücke", Choices: ["Ausdrücke", "Geräte"]),
     new("Werte", "Wertausdrücke (Semikolon)", ParameterKind.Text, "Spannung"), new("Ressourcen", "Gerätenamen (Komma)", ParameterKind.Text)];
    private static ParameterDescriptor[] RecordingParameters(bool timed) =>
    [Name(), new("Kanaele", "Kanalnamen (Komma)", ParameterKind.Text, "Spannung", Required: true),
     new("Einheiten", "Einheiten (Komma, gleiche Reihenfolge)", ParameterKind.Text, "V"),
     new("Grenzkurven", "Grenzkurven: ID@Version (Komma)", ParameterKind.Text, Required: true),
     new(AbortCurvesParameter, "Abbruchgrenzkurven: ID@Version (Komma)", ParameterKind.Text,
        Hint: "Optional je Kanal. Ein leerer Eintrag verwendet die Prozentregel. Eine eingetragene Kurve ersetzt die Prozentregel dieses Kanals."),
     new("Dauer", timed ? "Messdauer [s]" : "Messdauer [s], 0 = manuelles Ende", ParameterKind.Number, timed ? "1" : "0"),
     new("MaxPunkte", "Punktlimit je Kanal (2..100000)", ParameterKind.Number, "10000"),
     new("MaxLuecke", "Maximale Messlücke / Abfragedauer [s]", ParameterKind.Number, "1"),
     new("MinDauer", "Mindestdauer der Aufnahme [s]", ParameterKind.Number, "0"),
     new("Beruehren", "Berühren einer Grenzlinie zulässig", ParameterKind.Bool, "true"),
     new("FailAbbruch", "Aufnahme bei zusätzlicher Abweichung vorzeitig beenden (FAIL)", ParameterKind.Bool, "false"),
     new("AbweichungProzent", "Zusätzliche Abweichung [% vom Betrag des Grenzwerts]", ParameterKind.Number, "10")];
    private static IStepAction CreatePauseTime(BlockParameters parameters)
    {
        var name = parameters.Text("Aufnahme");
        var paused = parameters.Choice("Aktion") == "Pausieren";
        return new DelegateAction(ctx =>
        {
            ctx.Recordings.SetPaused(ctx.ProcessOwner, name, paused, ctx.Clock.Now);
            return Task.FromResult(StepOutcome.Unrated(message: paused ? "Messzeit pausiert." : "Messzeit fortgesetzt."));
        });
    }

    private static IStepAction CreatePoint(BlockParameters parameters)
    {
        var reader = new TraceSampleReader(parameters); var time = parameters.Expr("Zeit"); var name = parameters.Text("Aufnahme");
        return new DelegateAction(async ctx =>
        {
            if (ctx.Recordings.IsEnded(ctx.ProcessOwner, name)) return StepOutcome.Unrated(message: "Aufnahme bereits beendet; keine weitere Messung.");
            if (ctx.Recordings.IsPaused(ctx.ProcessOwner, name)) return StepOutcome.Unrated(message: "Messzeit pausiert; keine Messung.");
            try
            {
                double? xValue = time is null ? null : BlockCatalog.ToDouble(time.Evaluate(new ContextResolver(ctx)), "Zeit");
                var preview = await reader.AppendAsync(ctx, name, xValue).ConfigureAwait(false);
                if (preview is not null)
                {
                    if (preview.Finished) return Outcome(preview, true);
                    ctx.ReportTrace(preview.Data);
                }
                return ctx.Recordings.HasViolation(ctx.ProcessOwner, name)
                    ? StepOutcome.Fail("Grenzband verletzt; FAIL bleibt bestehen. Aufnahme läuft bis Dauer/Abbruchgrenze weiter.")
                    : StepOutcome.Unrated(message: "Messpunkt aufgezeichnet; noch keine abschließende PASS-Bewertung.");
            }
            catch { ctx.Recordings.Invalidate(ctx.ProcessOwner, name); throw; }
        });
    }
    internal static void ValidateTarget(IStepContext ctx, string? target)
    {
        if (target is not null && ctx.Get(target) is not string) throw new BlockConfigurationException("ErgebnisZiel muss eine Stringvariable sein.");
    }
    internal static void WriteDetails(IStepContext ctx, string? target, TraceEvaluation result)
    {
        if (target is not null) ctx.Set(target, JsonSerializer.Serialize(result.Channels));
    }
    private static string EarlyStopMessage(EnvelopeEarlyStop early)
    {
        var limit = early.AbortCurve is { } curve
            ? $"Abbruchgrenzkurve {curve}, Grenzen {early.Lower:G6}..{early.Upper:G6}"
            : $"Grenzband {early.Lower:G6}..{early.Upper:G6}, zusätzliche Abweichung {early.Percent:G6} %";
        return $"Aufnahme vorzeitig mit FAIL beendet: {early.Channel}, t={early.Time:G6} s, Wert {early.Value:G6}, {limit}. Nächste Prüfschritte werden ausgeführt.";
    }

    internal static StepOutcome Outcome(TraceEvaluation result, bool includeData)
    {
        var value = includeData ? result.Data : null;
        if (result.EarlyStop is { } early)
            return StepOutcome.Fail(EarlyStopMessage(early), value);
        if (!result.Complete) return new(ResultRating.Error, value, Message: "Aufnahme unvollständig, ungültig oder abgebrochen; vollständigen Zeitraum prüfen.");
        return result.Passed ? new(ResultRating.Pass, value, Message: "Vollständiger Messverlauf innerhalb des Grenzbandes.")
            : StepOutcome.Fail($"Grenzband verletzt: {result.Violations} Prüfstützpunkt(e).", value);
    }
}
