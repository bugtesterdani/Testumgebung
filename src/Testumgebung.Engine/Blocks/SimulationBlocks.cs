using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;

namespace Testumgebung.Engine.Blocks;

/// <summary>Simulationsbausteine für Etappe 0 (Abschnitt 13, Punkt 4).</summary>
public static class Steps
{
    /// <summary>Wert setzen: Zielvariable = Ausdruck (unbewertet).</summary>
    public static StepDefinition SetValue(string id, string target, string expression, string? name = null)
        => new(id, name ?? $"{target} setzen", new SetValueAction(target, Expression.Parse(expression)));

    /// <summary>Messung liefern: liest eine Ressource, schreibt in Zielvariable, bewertet gegen Grenzwerte.</summary>
    public static StepDefinition Measure(string id, string name, string resource, string? target, double? low, double? high, string unit = "")
        => new(id, name, new MeasureAction(resource, target, low, high, unit));

    /// <summary>Ausgabe setzen (unbewertet), z. B. AC-Quelle.</summary>
    public static StepDefinition Output(string id, string name, string resource, double value)
        => new(id, name, new DelegateAction(async ctx =>
        {
            await ctx.WriteAsync(resource, value).ConfigureAwait(false);
            return StepOutcome.Unrated(value);
        }));

    /// <summary>Bewertung setzen.</summary>
    public static StepDefinition Rate(string id, ResultRating rating, string? name = null)
        => new(id, name ?? $"Bewertung {rating.ToDisplay()}", new DelegateAction(_ => Task.FromResult(new StepOutcome(rating))));

    /// <summary>Warten (kooperativ abbrechbar).</summary>
    public static StepDefinition Wait(string id, TimeSpan duration, string? name = null)
        => new(id, name ?? $"Warten {duration.TotalMilliseconds} ms", new DelegateAction(async ctx =>
        {
            await ctx.Clock.Delay(duration, ctx.CancellationToken).ConfigureAwait(false);
            return StepOutcome.Unrated();
        }));

    /// <summary>Fehler erzeugen (ERROR).</summary>
    public static StepDefinition Fail(string id, string message, string? name = null)
        => new(id, name ?? "Fehler erzeugen", new DelegateAction(_ => throw new InvalidOperationException(message)));

    /// <summary>Freier Schritt (in-flow CodeBlock-Ersatz für Tests und Prototypen).</summary>
    public static StepDefinition Code(string id, string name, Func<IStepContext, Task<StepOutcome>> body)
        => new(id, name, new DelegateAction(body));

    public static StepDefinition Code(string id, string name, Func<IStepContext, StepOutcome> body)
        => new(id, name, new DelegateAction(ctx => Task.FromResult(body(ctx))));
}

public sealed class DelegateAction(Func<IStepContext, Task<StepOutcome>> body) : IStepAction
{
    public Task<StepOutcome> ExecuteAsync(IStepContext context) => body(context);
}

public sealed class SetValueAction(string target, Expression expression) : IStepAction
{
    public string Target => target;

    public Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var value = expression.Evaluate(new ContextResolver(context));
        context.Set(target, value);
        return Task.FromResult(StepOutcome.Unrated(context.Get(target)));
    }
}

public sealed class MeasureAction(string resource, string? target, double? low, double? high, string unit) : IStepAction
{
    public async Task<StepOutcome> ExecuteAsync(IStepContext context)
    {
        var value = await context.ReadAsync(resource).ConfigureAwait(false);
        if (target is not null) context.Set(target, value);
        if (low is null && high is null) return new StepOutcome(ResultRating.NResult, value, unit);
        var ok = (low is null || value >= low) && (high is null || value <= high);
        return new StepOutcome(ok ? ResultRating.Pass : ResultRating.Fail, value, unit, low, high,
            ok ? null : $"{value} {unit} außerhalb {low}–{high} {unit}");
    }
}
