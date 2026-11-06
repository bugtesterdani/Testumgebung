using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;

namespace Testumgebung.Engine.Blocks;

/// <summary>Eine Bewertung prüft eine Bedingung oder setzt ein bewusst gewähltes festes Ergebnis.</summary>
public static class EvaluationBlocks
{
    public static void Register(BlockCatalog catalog)
    {
        catalog.Register(new(BlockCatalog.Evaluate, "Bewertung", "Bewertung", "check",
            "Prüft eine Bedingung (PASS/FAIL) oder setzt ein festes Ergebnis einschließlich ERROR mit Meldung.",
            [
                new("Art", "Art", ParameterKind.Choice, "Bedingung prüfen", Choices: ["Bedingung prüfen", "Festes Ergebnis"]),
                new("Bedingung", "Bedingung", ParameterKind.Expression, "true", Required: true),
                new("Bewertung", "Festes Ergebnis", ParameterKind.Choice, "PASS", Choices: BlockCatalog.Ratings),
                new("Wert", "Angezeigter Wert", ParameterKind.Expression),
                new("Einheit", "Einheit", ParameterKind.Text),
                new("Meldung", "Meldung bei FAIL / festem Ergebnis", ParameterKind.Text)
            ], Create));
    }

    private static IStepAction Create(BlockParameters parameters)
    {
        var fixedResult = parameters.Choice("Art") == "Festes Ergebnis";
        var rating = fixedResult ? (ResultRating)Array.IndexOf(BlockCatalog.Ratings, parameters.Choice("Bewertung")) : ResultRating.NResult;
        var condition = fixedResult ? null : parameters.Expr("Bedingung");
        var shown = parameters.Expr("Wert");
        var unit = parameters.Optional("Einheit");
        var message = parameters.Optional("Meldung");
        return new DelegateAction(context =>
        {
            var resolver = new ContextResolver(context);
            var result = fixedResult ? rating : condition!.EvaluateBool(resolver) ? ResultRating.Pass : ResultRating.Fail;
            return Task.FromResult(new StepOutcome(result, shown?.Evaluate(resolver), unit,
                Message: fixedResult || result == ResultRating.Fail ? message : null));
        });
    }
}
