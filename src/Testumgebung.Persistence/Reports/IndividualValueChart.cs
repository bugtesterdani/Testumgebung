using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Reports;

public sealed record MeasurementPoint(DateTimeOffset Time, double Value);
public sealed record MeasurementSeriesKey(string Station, string Test, string Version, string Unit)
{
    public string Label => string.Join(" · ", new[] { Station, Test, Version, Unit }.Where(text => !string.IsNullOrWhiteSpace(text)));
}

public sealed record IndividualValueChart(MeasurementSeriesKey Series, IReadOnlyList<MeasurementPoint> Points)
{
    public double Mean => Points.Count == 0 ? 0 : Points.Average(point => point.Value);

    // I-MR chart, moving ranges of length two. 2.66 approximates 3 / d2.
    public double? ControlRadius => Points.Count < 2 ? null : 2.66 * AverageMovingRange();

    private double AverageMovingRange()
    {
        var sum = 0.0;
        for (var index = 1; index < Points.Count; index++) sum += Math.Abs(Points[index].Value - Points[index - 1].Value);
        return sum / (Points.Count - 1);
    }

    public (int Rising, int Falling) LongestTrends()
    {
        var rising = Math.Min(1, Points.Count);
        var falling = rising;
        var bestRising = rising;
        var bestFalling = falling;
        for (var index = 1; index < Points.Count; index++)
        {
            rising = Points[index].Value > Points[index - 1].Value ? rising + 1 : 1;
            falling = Points[index].Value < Points[index - 1].Value ? falling + 1 : 1;
            bestRising = Math.Max(bestRising, rising);
            bestFalling = Math.Max(bestFalling, falling);
        }
        return (bestRising, bestFalling);
    }

    public static int LongestFailStreak(IEnumerable<ResultRun> orderedRuns)
    {
        var longest = 0;
        foreach (var series in orderedRuns.GroupBy(run => (run.Station, run.TestName, run.TestVersion)))
        {
            var current = 0;
            foreach (var run in series)
            {
                current = !run.OperatorAbort && run.Rating == ResultRating.Fail ? current + 1 : 0;
                longest = Math.Max(longest, current);
            }
        }
        return longest;
    }
}
