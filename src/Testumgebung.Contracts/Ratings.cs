/**
 * @file
 * @brief Legt die Rangfolge der Bewertungen fest und trennt fachliches Prüfergebnis vom Ausführungszustand.
 */

namespace Testumgebung.Contracts;

/// <summary>
/// Bewertung eines Schritts oder einer Gruppe. Rangfolge: NRESULT &lt; PASS &lt; FAIL &lt; ERROR (Abschnitt 2.3).
/// Die numerischen Werte bilden die Rangfolge ab; Zusammenführen = Maximum.
/// </summary>
public enum ResultRating
{
    /// <summary>Keine Bewertung.</summary>
    NResult = 0,
    /// <summary>Bestandene Prüfung.</summary>
    Pass = 1,
    /// <summary>Nicht bestandene Prüfung.</summary>
    Fail = 2,
    /// <summary>Ausführungsfehler.</summary>
    Error = 3,
}

public static class ResultRatingExtensions
{
    /// <summary>Zusammenführen zweier Bewertungen: Maximum. Eine spätere niedrigere Bewertung verändert nichts.</summary>
    public static ResultRating Merge(this ResultRating current, ResultRating other)
        => (ResultRating)Math.Max((int)current, (int)other);

    public static string ToDisplay(this ResultRating rating) => rating switch
    {
        ResultRating.NResult => "NRESULT",
        ResultRating.Pass => "PASS",
        ResultRating.Fail => "FAIL",
        ResultRating.Error => "ERROR",
        _ => rating.ToString(),
    };
}

/// <summary>
/// Ablaufstatus eines Laufs (Abschnitt 11). Bewertung und WERROR werden separat geführt.
/// </summary>
public enum EngineState
{
    Idle,
    Initializing,
    Testing,
    WaitingResource,
    AwaitingOperator,
    Executing,
    PausedBeforeResultReturn,
    Exiting,
    Finalizing,
    Completed,
}

/// <summary>
/// Gesamtstatus eines Laufs bzw. Prüflingstests: Bewertung plus separates WERROR-Kennzeichen.
/// Ein Lauf kann z. B. FAIL und WERROR zugleich besitzen.
/// </summary>
public readonly record struct RunStatus(ResultRating Rating, bool OperatorAbort)
{
    public override string ToString()
        => OperatorAbort ? $"{Rating.ToDisplay()} + WERROR" : Rating.ToDisplay();
}
