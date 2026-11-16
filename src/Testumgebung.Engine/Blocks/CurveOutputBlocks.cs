using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

public static class CurveOutputBlocks
{
    public const string ActionParameter = "Aktion";
    public const string Load = "Laden", Transfer = "Übertragen", Start = "Starten", Stop = "Stoppen";

    public static string? LegacyAction(string? blockType) => blockType switch
    {
        BlockCatalog.CurveLoad => Load,
        BlockCatalog.CurveTransfer => Transfer,
        BlockCatalog.CurveStart => Start,
        BlockCatalog.CurveStop => Stop,
        _ => null
    };

    internal static void Register(BlockCatalog catalog) => catalog.Register(new(
        BlockCatalog.CurveOutput, "Kurvenausgabe", "Kurvenausgabe", "arrow",
        "Lädt eine Kurve mit Geräteeignungsprüfung, überträgt, startet oder stoppt die Ausgabe.",
        [
            new(ActionParameter, "Aktion", ParameterKind.Choice, Load, Choices: [Load, Transfer, Start, Stop]),
            new("Ressource", "Ausgabegerät", ParameterKind.Resource, "AC_Kurvenquelle", Required: true),
            new("Kurve", "Kurve (ID)", ParameterKind.Text, Hint: "Beim Laden erforderlich."),
            new("Version", "Version", ParameterKind.Text, Hint: "Leer = neueste gespeicherte Version (für Freigaben feste Version angeben)"),
        ], Create));

    private static IStepAction Create(BlockParameters parameters)
    {
        var resource = parameters.Text("Ressource");
        var action = parameters.Choice(ActionParameter);
        if (action == Load)
        {
            var id = parameters.Text("Kurve");
            if (string.IsNullOrWhiteSpace(id)) throw new BlockConfigurationException("„Kurve (ID)“ ist erforderlich.");
            var version = parameters.Optional("Version");
            return new DelegateAction(ctx => BlockCatalog.LoadCurveAsync(ctx, resource, id, version));
        }

        var command = action switch
        {
            Transfer => CurveCommand.Transfer,
            Start => CurveCommand.Start,
            _ => CurveCommand.Stop
        };
        return new DelegateAction(async ctx =>
        {
            var state = await ctx.CurveOutputAsync(resource, command, null).ConfigureAwait(false);
            return StepOutcome.Unrated(state.ToString());
        });
    }
}
