/**
 * @file
 * @brief Zählt tatsächliche Treffer und wertet sichere, typisierte Haltebedingungen aus.
 */
using Testumgebung.Contracts;
using Testumgebung.Domain.Definitions;
using Testumgebung.Domain.Expressions;
namespace Testumgebung.Engine;

internal sealed class BreakpointState
{
    private long _hits;
    private readonly Expression? _expression;
    public BreakpointRule Rule { get; }
    public BreakpointState(BreakpointRule rule, bool code = false)
    {
        rule.Validate(code);
        Rule = rule;
        if (rule.Mode == BreakpointMode.Expression) _expression = Expression.Parse(rule.Condition!);
    }
    public void Reset() => Interlocked.Exchange(ref _hits, 0);
    public bool Evaluate(int iteration, ResultRating? rating, Func<string, object> resolve)
    {
        var hit = Interlocked.Increment(ref _hits);
        if (hit < Rule.FirstHit || (hit - Rule.FirstHit) % Rule.Every != 0) return false;
        return Rule.Mode switch
        {
            BreakpointMode.Always => true,
            BreakpointMode.FailOrError => rating is ResultRating.Fail or ResultRating.Error,
            BreakpointMode.Fail => rating == ResultRating.Fail,
            BreakpointMode.Error => rating == ResultRating.Error,
            BreakpointMode.Expression => _expression!.EvaluateBool(new Values(iteration, hit, rating, resolve)),
            _ => false,
        };
    }
    private sealed class Values(int iteration, long hit, ResultRating? rating, Func<string, object> fallback) : IValueResolver
    {
        public object Resolve(string name) => name switch
        {
            "Debug.Treffer" => hit,
            "Debug.Iteration" => (long)iteration,
            "Debug.Bewertung" when rating is { } value => value.ToDisplay(),
            _ => fallback(name),
        };
    }
}
