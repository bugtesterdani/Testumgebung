using System.Text.Json;
using Testumgebung.Contracts;
using Testumgebung.Domain.Curves;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

public static class TraceBlocks
{
    public const string Start = "verlauf.start", Point = "verlauf.punkt", Stop = "verlauf.bewerten";
    public static void Register(BlockCatalog catalog)
    {
        catalog.Register(new(Start, "Messwertverlauf beginnen", "Messwertverläufe", "measure", "Beginnt eine begrenzte Mehrkanalaufnahme. Grenzkurven haben Kanäle Untergrenze und Obergrenze mit derselben Einheit wie der Messkanal.",
        [Name(), new("Kanaele", "Kanalnamen (Komma)", ParameterKind.Text, "Spannung", Required: true),
         new("Einheiten", "Einheiten (Komma, gleiche Reihenfolge)", ParameterKind.Text, "V"),
         new("Grenzkurven", "Grenzkurven: ID@Version (Komma)", ParameterKind.Text, Required: true),
         new("MaxPunkte", "Punktlimit je Kanal (2..100000)", ParameterKind.Number, "10000"),
         new("MaxLuecke", "Maximale Messlücke [s]", ParameterKind.Number, "1"),
         new("MinDauer", "Mindestdauer der Aufnahme [s]", ParameterKind.Number, "0"),
         new("Beruehren", "Berühren einer Grenzlinie zulässig", ParameterKind.Bool, "true")], CreateStart));
        catalog.Register(new(Point, "Messpunkt ergänzen", "Messwertverläufe", "measure", "Ergänzt alle Kanäle mit gemeinsamer Empfangszeit. Geräte werden nacheinander abgefragt; keine zugesicherte simultane Abtastung.",
        [Name(), new("Quelle", "Datenquelle", ParameterKind.Choice, "Ausdrücke", Choices: ["Ausdrücke", "Geräte"]),
         new("Werte", "Wertausdrücke (Semikolon)", ParameterKind.Text, "Spannung"),
         new("Ressourcen", "Gerätenamen (Komma)", ParameterKind.Text),
         new("Zeit", "Zeit [s], leer = seit Aufnahmebeginn", ParameterKind.Expression)], CreatePoint));
        catalog.Register(new(Stop, "Verlauf beenden und bewerten", "Messwertverläufe", "measure", "Prüft die gesamte stückweise lineare Messkurve und speichert Messung, Grenzlinien und Herkunft. Unvollständig/abgebrochen ergibt ERROR, niemals PASS.",
        [Name(), new("ErgebnisZiel", "Auswertungsdetails → Stringvariable (JSON)", ParameterKind.Variable)], p => new DelegateAction(ctx =>
        {
            var target = p.Optional("ErgebnisZiel");
            if (target is not null && ctx.Get(target) is not string) throw new BlockConfigurationException("ErgebnisZiel muss eine Stringvariable sein.");
            var result = ctx.Recordings.Stop(ctx.ProcessOwner, p.Text("Aufnahme"), !ctx.IsOperatorAborted);
            if (target is not null) ctx.Set(target, JsonSerializer.Serialize(result.Channels));
            var message = !result.Complete ? "Aufnahme unvollständig, ungültig oder abgebrochen."
                : result.Passed ? "Alle Messverläufe innerhalb des Grenzbandes." : $"Grenzband verletzt: {result.Violations} Prüfstützpunkt(e). Details je Kanal in den Kurvendaten und der Ergebnisvariable.";
            return Task.FromResult(new StepOutcome(!result.Complete ? ResultRating.Error : result.Passed ? ResultRating.Pass : ResultRating.Fail, result.Data, Message: message));
        })));
    }
    private static ParameterDescriptor Name() => new("Aufnahme", "Aufnahmekennung", ParameterKind.Text, "Verlauf", Required: true);
    private static string[] Split(string value, char separator) => value.Split(separator, StringSplitOptions.TrimEntries);
    private static IStepAction CreateStart(BlockParameters p)
    {
        var names = Split(p.Text("Kanaele"), ','); var units = Split(p.Text("Einheiten"), ','); var curves = Split(p.Text("Grenzkurven"), ',');
        if (names.Length != units.Length || names.Length != curves.Length) throw new BlockConfigurationException("Je Messkanal eine Einheit und eine Grenzkurve angeben.");
        var maximum = p.Number("MaxPunkte") ?? 0;
        if (maximum != Math.Truncate(maximum) || maximum is < 2 or > 100000) throw new BlockConfigurationException("Ganzzahliges Punktlimit 2..100000 angeben.");
        return new DelegateAction(ctx =>
        {
            if (ctx.IsOperatorAborted) throw new InvalidOperationException("Nach Werkerabbruch keine Aufnahme beginnen.");
            var channels = names.Select((name, i) => new TraceChannelDefinition(name, units[i], Resolve(ctx, curves[i]))).ToArray();
            ctx.Recordings.Start(ctx.ProcessOwner, p.Text("Aufnahme"), channels, ctx.Clock.Now, (int)maximum, p.Number("MaxLuecke") ?? 0, p.Number("MinDauer") ?? 0, p.Bool("Beruehren"));
            return Task.FromResult(StepOutcome.Unrated(message: "Messwertaufzeichnung begonnen; Bewertung erst beim Beenden."));
        });
    }
    private static CurveDefinition Resolve(IStepContext ctx, string key)
    {
        var separator = key.LastIndexOf('@'); var id = separator < 0 ? key : key[..separator]; var version = separator < 0 ? null : key[(separator + 1)..];
        return ctx.FindCurve(id, version) ?? throw new BlockConfigurationException($"Grenzkurve {key} fehlt im Prüfprogramm.");
    }
    private static Expression[] ParseValues(string text)
    {
        try { return Split(text, ';').Select(Expression.Parse).ToArray(); }
        catch (ExpressionException ex) { throw new BlockConfigurationException("Wertausdrücke: " + ex.Message); }
    }
    private static IStepAction CreatePoint(BlockParameters p)
    {
        var devices = p.Choice("Quelle") == "Geräte";
        var resources = Split(p.Text("Ressourcen"), ',');
        var expressions = devices ? [] : ParseValues(p.Text("Werte"));
        var time = p.Expr("Zeit");
        return new DelegateAction(async ctx =>
        {
            try
            {
                var resolver = new ContextResolver(ctx); var values = new List<double>();
                if (devices)
                {
                    var units = new List<string>();
                    foreach (var resource in resources)
                    {
                        var reading = await ctx.UseDeviceAsync(resource, "Verlauf lesen", async (adapter, channel, token) =>
                            (Value: await adapter.ReadAsync(channel, token).ConfigureAwait(false), Unit: adapter.GetCapabilities(channel).Unit)).ConfigureAwait(false);
                        values.Add(reading.Value); units.Add(reading.Unit);
                    }
                    ctx.Recordings.CheckUnits(ctx.ProcessOwner, p.Text("Aufnahme"), units);
                }
                else foreach (var expression in expressions) values.Add(BlockCatalog.ToDouble(expression.Evaluate(resolver), "Messwert"));
                ctx.CancellationToken.ThrowIfCancellationRequested();
                double? x = time is null ? null : BlockCatalog.ToDouble(time.Evaluate(resolver), "Zeit");
                var preview = ctx.Recordings.Add(ctx.ProcessOwner, p.Text("Aufnahme"), x, values, ctx.Clock.Now);
                if (preview is not null) ctx.ReportTrace(preview.Data);
                return StepOutcome.Unrated(message: "Messpunkt aufgezeichnet.");
            }
            catch { ctx.Recordings.Invalidate(ctx.ProcessOwner, p.Text("Aufnahme")); throw; }
        });
    }
}