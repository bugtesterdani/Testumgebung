using Testumgebung.Contracts;

namespace Testumgebung.Domain.Curves;

/// <summary>Per-channel state for optional slope suppression. Raw samples remain owned by the recording.</summary>
internal enum SlopeSuppressionReason { None, BelowThreshold, InitialException }

internal sealed class SlopeSampleFilter(SlopeLimits limits)
{
    private int? _pendingStart;
    private int _exceptions;
    public List<SuppressedSlopeSample> Suppressed { get; } = [];
    public HashSet<int> SuppressedIndices { get; } = [];
    public int EvaluatedCount { get; private set; }

    public (int First, double Value) Calculate(int first, int last, IReadOnlyList<double> times, IReadOnlyList<double> values)
    {
        var current = CalculateFrom(first, last, times, values);
        if (_pendingStart is not { } pending || !BelowThreshold(current)) return (first, current);
        return (pending, CalculateFrom(pending, last, times, values));
    }

    private double CalculateFrom(int first, int last, IReadOnlyList<double> times, IReadOnlyList<double> values)
    {
        var value = (values[last] - values[first]) / (times[last] - times[first]);
        if (!double.IsFinite(value) || !double.IsFinite(Math.Max(limits.Minimum - value, value - limits.Maximum)))
            throw new ArgumentException("Wertebereich der Steigungsauswertung überschritten.");
        return value;
    }

    public bool Observe(int first, int last, double value, IReadOnlyList<double> times,
        IReadOnlyList<double> values, int slopeIndex, bool touchAllowed)
    {
        var reason = Reason(value, touchAllowed);
        _pendingStart = reason == SlopeSuppressionReason.BelowThreshold ? first : null;
        if (reason != SlopeSuppressionReason.None)
        {
            SuppressedIndices.Add(slopeIndex);
            Suppressed.Add(new(times[last], value, times[first], values[first], values[last], Describe(reason)));
            return false;
        }
        EvaluatedCount++;
        return true;
    }

    private bool MatchesExceptionDirection(double value) => limits.ExceptionDirection switch
    {
        SlopeExceptionDirection.Falling => value < -limits.InitialExceptionAbove,
        SlopeExceptionDirection.Both => Math.Abs(value) > limits.InitialExceptionAbove,
        _ => value > limits.InitialExceptionAbove
    };
    private bool BelowThreshold(double value)
        => limits.HasSlopeThreshold && value <= limits.PositiveThreshold && value >= limits.NegativeThreshold;

    private string Describe(SlopeSuppressionReason reason) => reason switch
    {
        SlopeSuppressionReason.BelowThreshold => "Mindeststeigung nicht erreicht",
        SlopeSuppressionReason.InitialException => $"Anlaufausnahme {_exceptions} von {limits.InitialExceptionCount}",
        _ => ""
    };

    private SlopeSuppressionReason Reason(double value, bool touchAllowed)
    {
        if (BelowThreshold(value))
            return SlopeSuppressionReason.BelowThreshold;
        var inside = touchAllowed ? value >= limits.Minimum && value <= limits.Maximum
            : value > limits.Minimum && value < limits.Maximum;
        if (!inside && EvaluatedCount == 0 && _exceptions < limits.InitialExceptionCount && MatchesExceptionDirection(value))
        {
            _exceptions++;
            return SlopeSuppressionReason.InitialException;
        }
        return SlopeSuppressionReason.None;
    }
}
