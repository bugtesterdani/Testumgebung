/**
 * @file
 * @brief Beschreibt Haltefilter unabhängig von aktiven, nur sitzungsbezogenen Haltepunkten.
 */
using Testumgebung.Domain.Expressions;
namespace Testumgebung.Domain.Definitions;

public enum BreakpointMode { Always, FailOrError, Fail, Error, Expression }

/// <summary>Standard: jeder Treffer. Die tatsächliche Aktivierung bleibt beim Debugger, nicht beim freigegebenen Programm.</summary>
public sealed record BreakpointRule(BreakpointMode Mode = BreakpointMode.Always, string? Condition = null, long FirstHit = 1, long Every = 1)
{
    public static BreakpointRule Default { get; } = new();
    public void Validate(bool code = false)
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("Unbekannte Haltebedingung.");
        if (FirstHit < 1 || Every < 1) throw new ArgumentException("Trefferzahlen müssen mindestens 1 sein.");
        if (code && Mode is not (BreakpointMode.Always or BreakpointMode.Expression)) throw new ArgumentException("Codezeilen haben noch keine Schrittbewertung. Immer oder Ausdruck wählen.");
        if (Mode == BreakpointMode.Expression)
        {
            if (string.IsNullOrWhiteSpace(Condition)) throw new ArgumentException("Halteausdruck fehlt.");
            _ = Expressions.Expression.Parse(Condition);
        }
    }
}
