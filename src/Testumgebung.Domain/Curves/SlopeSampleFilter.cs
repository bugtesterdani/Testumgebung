using Testumgebung.Contracts;

namespace Testumgebung.Domain.Curves;

/// <summary>Per-channel state for optional slope suppression. Raw samples remain owned by the recording.</summary>
internal sealed class SlopeSampleFilter(SlopeLimits limits)
{
    private int? _pendingStart;
    private int _exceptions;
    public List<SuppressedSlopeSample> Suppressed { get; } = [];
    public HashSet<int> SuppressedIndices { get; } = [];
    public int EvaluatedCount { get; private set; }

    public (int First, double Value) Calculate(int first, int last, IReadOnlyList<double> times, IReadOnlyList<double> values)
    {
        first = _pendingStart ?? first;
        var value = (values[last] - values[first]) / (times[last] - times[first]);
        if (!double.IsFinite(value) || !double.IsFinite(Math.Max(limits.Minimum - value, value - limits.Maximum)))
            throw new ArgumentException("Wertebereich der Steigungsauswertung überschritten.");
        return (first, value);
    }

    public bool Observe(int first, int last, double value, IReadOnlyList<double> times,
        IReadOnlyList<double> values, int slopeIndex, bool touchAllowed)
    {
        var reason = Reason(value, touchAllowed);
        _pendingStart = reason == "Mindeststeigung nicht erreicht" ? first : null;
        if (reason is not null)
        {
            SuppressedIndices.Add(slopeIndex);
            Suppressed.Add(new(times[last], value, times[first], values[first], values[last], reason));
            return false;
        }
        EvaluatedCount++;
        return true;
    }

    private string? Reason(double value, bool touchAllowed)
    {
        if (limits.HasSlopeThreshold && value <= limits.PositiveThreshold && value >= limits.NegativeThreshold)
            return "Mindeststeigung nicht erreicht";
        var inside = touchAllowed ? value >= limits.Minimum && value <= limits.Maximum
            : value > limits.Minimum && value < limits.Maximum;
        if (!inside && _exceptions < limits.InitialExceptionCount && value > limits.InitialExceptionAbove)
        {
            _exceptions++;
            return $"Anlaufausnahme {_exceptions} von {limits.InitialExceptionCount}";
        }
        return null;
    }
}
