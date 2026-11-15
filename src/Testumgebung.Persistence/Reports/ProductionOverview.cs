using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;

namespace Testumgebung.Persistence.Reports;

public sealed record ProductionSummary(int Pieces, int Retests, double? FirstPassYield, double? FinalYield, TimeSpan? MeanCycleTime)
{
    internal static ProductionSummary FromOrderedRuns(IReadOnlyList<ResultRun> runs)
    {
        var byDut = runs.Where(run => !string.IsNullOrWhiteSpace(run.DutId)).GroupBy(run => run.DutId, StringComparer.Ordinal).ToArray();
        var durations = runs.Where(run => run.Ended > run.Started).Select(run => (run.Ended - run.Started).TotalSeconds).ToArray();
        return new(byDut.Length, byDut.Sum(dut => dut.Count() - 1), Yield(byDut, dut => dut.First()),
            Yield(byDut, dut => dut.Last()), durations.Length == 0 ? null : TimeSpan.FromSeconds(durations.Average()));
    }

    private static double? Yield(IReadOnlyCollection<IGrouping<string, ResultRun>> duts, Func<IGrouping<string, ResultRun>, ResultRun> choose)
        => duts.Count == 0 ? null : (double)duts.Select(choose).Count(run => !run.OperatorAbort && run.Rating == ResultRating.Pass) / duts.Count;
}

public sealed record ProductionRow(string Station, DateTime ShiftStarted, string Order, ProductionSummary Metrics);

public static class ProductionOverview
{
    public static IReadOnlyList<ProductionRow> Compute(IEnumerable<ResultRun> all, StatisticsFilter filter,
        Func<string, IReadOnlyList<TimeOnly>> shiftsFor, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var shifts = new Dictionary<string, TimeOnly[]>(StringComparer.Ordinal);
        var runs = all.Where(run => run.Kind == ResultRunKind.Production && filter.Matches(run)).OrderBy(run => run.Started);
        return runs.GroupBy(run => (run.Station, Shift: StartOfShift(run), Order: OrderOf(run)))
            .Select(group => new ProductionRow(group.Key.Station, group.Key.Shift, group.Key.Order, ProductionSummary.FromOrderedRuns(group.ToArray())))
            .OrderByDescending(row => row.ShiftStarted).ThenBy(row => row.Station, StringComparer.Ordinal)
            .ThenBy(row => row.Order, StringComparer.Ordinal).ToArray();

        DateTime StartOfShift(ResultRun run)
        {
            if (!shifts.TryGetValue(run.Station, out var starts))
            {
                starts = shiftsFor(run.Station).Order().ToArray();
                if (starts.Length == 0) starts = [new(6, 0), new(14, 0), new(22, 0)];
                shifts.Add(run.Station, starts);
            }
            var local = TimeZoneInfo.ConvertTime(run.Started, zone).DateTime;
            var time = TimeOnly.FromDateTime(local);
            var start = starts.LastOrDefault(value => value <= time, starts[^1]);
            return local.Date.AddDays(start > time ? -1 : 0).Add(start.ToTimeSpan());
        }
    }

    private static string OrderOf(ResultRun run)
        => string.IsNullOrWhiteSpace(run.Order) ? Reports.OrderOf(run)?.Trim() ?? "" : run.Order.Trim();
}
