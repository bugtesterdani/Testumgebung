using Testumgebung.Domain.Expressions;

namespace Testumgebung.Domain.Curves;

/// <summary>A pair of numeric expressions evaluated at the recorded sample time.</summary>
public sealed class FormulaLimits
{
    private readonly Expression _lower, _upper;

    public FormulaLimits(string lower, string upper)
    {
        _lower = Parse(lower, "Untergrenze");
        _upper = Parse(upper, "Obergrenze");
    }

    public (double Lower, double Upper) At(double time)
    {
        var resolver = new TimeResolver(time);
        var lower = Evaluate(_lower, "Untergrenze", resolver);
        var upper = Evaluate(_upper, "Obergrenze", resolver);
        if (lower > upper)
            throw new ExpressionException($"Bei t={time:G6} s liegt die berechnete Untergrenze über der Obergrenze.");
        return (lower, upper);
    }

    private static Expression Parse(string source, string name)
    {
        try
        {
            var expression = Expression.Parse(source, 256, 32);
            if (expression.ReferencedNames.Any(variable => variable != "t"))
                throw new ExpressionException("Als Variable steht nur t (Messzeit in Sekunden) zur Verfügung.");
            return expression;
        }
        catch (ExpressionException exception)
        {
            throw new ExpressionException($"Formel für {name}: {exception.Message}");
        }
    }

    private static double Evaluate(Expression expression, string name, TimeResolver resolver)
    {
        try
        {
            var value = expression.Evaluate(resolver) switch
            {
                double number => number,
                long integer => integer,
                _ => double.NaN
            };
            if (double.IsFinite(value)) return value;
            throw new ExpressionException("Die Formel muss eine endliche Zahl ergeben.");
        }
        catch (ExpressionException exception)
        {
            throw new ExpressionException($"Formel für {name} bei t={resolver.Time:G6} s: {exception.Message}");
        }
    }

    private sealed class TimeResolver(double time) : IValueResolver
    {
        public double Time => time;
        public object Resolve(string name) => time;
    }
}
