using Testumgebung.Contracts;

namespace Testumgebung.Domain.Results;

/// <summary>
/// Bewertung eines Elements, die noch nicht an die Eltern übergeben wurde.
/// Bei Wiederholung am Haltepunkt ersetzt die letzte Bewertung die ausstehende (keine doppelte Aggregation).
/// </summary>
public sealed class PendingResult
{
    public ResultRating Rating { get; private set; } = ResultRating.NResult;
    public bool Committed { get; private set; }

    /// <summary>Ersetzt die ausstehende Bewertung (z. B. nach „Erneut ausführen“).</summary>
    public void Replace(ResultRating rating)
    {
        if (Committed) throw new InvalidOperationException("Bewertung wurde bereits übergeben.");
        Rating = rating;
    }

    /// <summary>Überträgt die Bewertung genau einmal in das Elternergebnis.</summary>
    public void CommitTo(ResultAccumulator parent)
    {
        if (Committed) throw new InvalidOperationException("Bewertung wurde bereits übergeben.");
        Committed = true;
        parent.Merge(Rating);
    }
}

/// <summary>Zusammenführung von Bewertungen einer Gruppe (Maximum).</summary>
public sealed class ResultAccumulator
{
    private int _rating;

    public ResultRating Rating => (ResultRating)Volatile.Read(ref _rating);

    public void Merge(ResultRating rating)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _rating);
            if ((int)rating <= current) return;
        }
        while (Interlocked.CompareExchange(ref _rating, (int)rating, current) != current);
    }

    /// <summary>Vor einer Gruppenwiederholung (Policy ResetToNResult). Setzt keine Variablen zurück.</summary>
    public void Reset() => Interlocked.Exchange(ref _rating, (int)ResultRating.NResult);
}
