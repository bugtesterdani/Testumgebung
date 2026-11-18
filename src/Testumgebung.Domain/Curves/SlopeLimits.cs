namespace Testumgebung.Domain.Curves;

public enum SlopeWindowMode { Consecutive, Disjoint, Rolling }

public sealed record SlopeLimits(SlopeWindowMode Mode, int Points, double Minimum, double Maximum)
{
    public int InitialExceptionCount { get; init; }
    public double InitialExceptionAbove { get; init; }
    public double PositiveThreshold { get; init; }
    public double NegativeThreshold { get; init; }
    public bool HasSlopeThreshold => PositiveThreshold > 0 || NegativeThreshold < 0;

    internal void Validate(int maxPoints)
    {
        ValidateSuppression(maxPoints);
        if (!Enum.IsDefined(Mode) || Points < 2 || Points > maxPoints ||
            (Mode == SlopeWindowMode.Consecutive && Points != 2))
            throw new ArgumentException("Die Steigung benötigt mindestens zwei Punkte je Fenster und muss ins Punktlimit passen.");
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Minimum > Maximum)
            throw new ArgumentException("Endliche Steigungsgrenzen mit Minimum ≤ Maximum angeben.");
    }

    private void ValidateSuppression(int maxPoints)
    {
        if (InitialExceptionCount < 0 || InitialExceptionCount > maxPoints || !double.IsFinite(InitialExceptionAbove))
            throw new ArgumentException("Anlaufausnahmen benötigen eine Anzahl zwischen 0 und dem Punktlimit und eine endliche Schwelle.");
        if (!double.IsFinite(PositiveThreshold) || !double.IsFinite(NegativeThreshold)
            || PositiveThreshold < 0 || NegativeThreshold > 0)
            throw new ArgumentException("Die positive Mindeststeigung muss ≥ 0 und die negative ≤ 0 sein.");
    }

    internal bool IsWindowEnd(int count) => count >= Points &&
        (Mode != SlopeWindowMode.Disjoint || count % Points == 0);
}
