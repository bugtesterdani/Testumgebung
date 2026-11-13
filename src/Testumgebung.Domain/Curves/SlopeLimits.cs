namespace Testumgebung.Domain.Curves;

public enum SlopeWindowMode { Consecutive, Disjoint, Rolling }

public sealed record SlopeLimits(SlopeWindowMode Mode, int Points, double Minimum, double Maximum)
{
    internal void Validate(int maxPoints)
    {
        if (!Enum.IsDefined(Mode) || Points < 2 || Points > maxPoints ||
            (Mode == SlopeWindowMode.Consecutive && Points != 2))
            throw new ArgumentException("Die Steigung benötigt mindestens zwei Punkte je Fenster und muss ins Punktlimit passen.");
        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Minimum > Maximum)
            throw new ArgumentException("Endliche Steigungsgrenzen mit Minimum ≤ Maximum angeben.");
    }

    internal bool IsWindowEnd(int count) => count >= Points &&
        (Mode != SlopeWindowMode.Disjoint || count % Points == 0);
}
