using Testumgebung.Contracts;

namespace Testumgebung.Domain.Curves;

internal readonly record struct StopMargin(double Value, bool Absolute)
{
    public static StopMargin? Create(double? percent, double? absolute)
    {
        if (percent.HasValue && absolute.HasValue)
            throw new ArgumentException("Prozentuale oder absolute Abbruchabweichung auswählen.");
        var margin = absolute ?? percent;
        if (margin is not { } value) return null;
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentException("FAIL-Abbruchabweichung muss endlich und >= 0 sein.");
        return new(value, absolute.HasValue);
    }

    private double Offset(double bound) => Absolute ? Value : Math.Abs(bound) * (Value / 100);

    public EnvelopeEarlyStop? Check(string channel, double time, double measured, double lower, double upper)
    {
        // Decimal configuration and interpolation may differ by a few binary rounding bits.
        var lowTolerance = Math.Max(Math.Abs(measured), Math.Abs(lower)) * 1e-12;
        var highTolerance = Math.Max(Math.Abs(measured), Math.Abs(upper)) * 1e-12;
        if (!(measured < lower - Offset(lower) - lowTolerance ||
              measured > upper + Offset(upper) + highTolerance)) return null;

        return new(Absolute ? 0 : Value, channel, time, measured, lower, upper)
        {
            AbsoluteDeviation = Absolute ? Value : null
        };
    }
}
