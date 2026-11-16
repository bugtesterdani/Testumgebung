using Testumgebung.Contracts;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;

namespace Testumgebung.Notifications;

public sealed record AlarmRules(int ErrorStreak, int OutboxThreshold, bool ResultFolder, bool Calibration);

/// <summary>Tracks transitions per application session. Rearms after the condition clears.</summary>
public sealed class AlarmMonitor(AlarmRules rules, IRunNotifier notifier, IClock clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);

    public void ObserveRun(ResultRun run)
    {
        if (rules.ErrorStreak <= 0 || run.IsDevelopmentRun || run.Kind != ResultRunKind.Production) return;
        lock (_gate)
            ReadErrorStreak(run);
    }

    public void ObserveStorage(StorageReport report)
    {
        if (!rules.ResultFolder || report.File is not { } file) return;
        lock (_gate)
            Check("folder", !file.Success, "Ergebnisordner nicht erreichbar. Ergebnisablage prüfen.");
    }

    private void ReadErrorStreak(ResultRun run)
    {
        var count = run is { OperatorAbort: false, Rating: ResultRating.Error }
            ? Math.Min(_errors.GetValueOrDefault(run.Station) + 1L, rules.ErrorStreak) : 0;
        _errors[run.Station] = (int)count;
        Check("error:" + run.Station, count >= rules.ErrorStreak,
            $"{run.Station}: {rules.ErrorStreak} Prüfläufe in Folge mit ERROR.", run.RunId, run.DutId);
    }

    public void ObserveOutbox(int count)
    {
        if (rules.OutboxThreshold <= 0) return;
        lock (_gate)
            Check("outbox", count >= rules.OutboxThreshold, $"Outbox: {count} offene Einträge.");
    }

    public void ObserveCalibration(IEnumerable<DeviceConfig> devices)
    {
        if (!rules.Calibration) return;
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(clock.Now.LocalDateTime);
            var current = StationHardware.CalibrationWarnings(devices, today).ToHashSet(StringComparer.Ordinal);
            _active.RemoveWhere(key => key.StartsWith("calibration:", StringComparison.Ordinal) && !current.Contains(key["calibration:".Length..]));
            foreach (var message in current) Check("calibration:" + message, true, message);
        }
    }

    private void Check(string key, bool active, string message, string runId = "", string dutId = "")
    {
        if (!active) { _active.Remove(key); return; }
        if (_active.Add(key)) notifier.Notify(new(RunNotificationKind.Alarm, runId, dutId, clock.Now, 0, message));
    }
}
