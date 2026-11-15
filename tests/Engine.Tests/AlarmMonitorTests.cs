using Testumgebung.Contracts;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;

namespace Testumgebung.Engine.Tests;

public sealed class AlarmMonitorTests
{
    private sealed class Capture : IRunNotifier
    {
        public List<RunNotification> Notifications { get; } = [];
        public void Notify(RunNotification notification) => Notifications.Add(notification);
    }

    private static ResultRun Run(string id = "run", string station = "P1", ResultRating rating = ResultRating.Error,
        bool development = false, ResultRunKind kind = ResultRunKind.Production, bool abort = false)
        => new() { RunId = id, DutId = "SN", Station = station, Rating = rating, IsDevelopmentRun = development, Kind = kind, OperatorAbort = abort };

    [Fact]
    public void Error_streak_is_per_station_and_rearms_after_a_regular_result_or_abort()
    {
        var capture = new Capture();
        var monitor = new AlarmMonitor(new(2, 0, false, false), capture, new VirtualClock());

        monitor.ObserveRun(Run());
        monitor.ObserveRun(Run(station: "P2"));
        monitor.ObserveRun(Run(development: true, rating: ResultRating.Pass));
        monitor.ObserveRun(Run(kind: ResultRunKind.GoodReference));
        Assert.Empty(capture.Notifications);
        monitor.ObserveRun(Run("trigger"));
        monitor.ObserveRun(Run());
        var alarm = Assert.Single(capture.Notifications);
        Assert.Equal(RunNotificationKind.Alarm, alarm.Kind);
        Assert.Equal("trigger", alarm.RunId);
        Assert.Equal("SN", alarm.DutId);

        monitor.ObserveRun(Run(rating: ResultRating.Fail));
        monitor.ObserveRun(Run());
        monitor.ObserveRun(Run(abort: true));
        monitor.ObserveRun(Run());
        Assert.Single(capture.Notifications);
        monitor.ObserveRun(Run("rearmed"));
        Assert.Equal("rearmed", capture.Notifications[1].RunId);
    }

    [Fact]
    public void Outbox_and_folder_emit_once_per_incident_and_rearm_independently()
    {
        var capture = new Capture();
        var monitor = new AlarmMonitor(new(0, 3, true, false), capture, new VirtualClock());
        var failed = new StorageReport("run", "SN", new("fallback", "unavailable", true), null);
        var available = failed with { File = new("result", null, false) };

        monitor.ObserveOutbox(2);
        monitor.ObserveOutbox(3);
        monitor.ObserveOutbox(4);
        monitor.ObserveStorage(failed);
        monitor.ObserveStorage(failed);
        Assert.Equal(2, capture.Notifications.Count);
        Assert.Contains("Outbox", capture.Notifications[0].Error);
        Assert.Contains("Ergebnisordner", capture.Notifications[1].Error);

        monitor.ObserveOutbox(2);
        monitor.ObserveStorage(available);
        monitor.ObserveStorage(failed);
        monitor.ObserveOutbox(3);
        Assert.Equal(4, capture.Notifications.Count);
    }

    [Fact]
    public void Calibration_warns_then_reports_overdue_and_can_rearm()
    {
        var capture = new Capture();
        var clock = new VirtualClock(new DateTimeOffset(2026, 1, 9, 12, 0, 0, TimeSpan.Zero), true);
        var monitor = new AlarmMonitor(new(0, 0, false, true), capture, clock);
        var date = DateOnly.FromDateTime(clock.Now.LocalDateTime);
        var device = new DeviceConfig
        {
            Adapter = "Real", Name = "Multimeter",
            Calibration = new DeviceCalibration("SN-1", date.AddDays(-8), 10, 2)
        };
        monitor.ObserveCalibration([device]);
        monitor.ObserveCalibration([device]);
        Assert.Contains("fällig", Assert.Single(capture.Notifications).Error);

        device.Calibration = device.Calibration with { CalibratedOn = date.AddDays(-11) };
        monitor.ObserveCalibration([device]);
        Assert.Contains("abgelaufen", capture.Notifications[1].Error);

        monitor.ObserveCalibration([]);
        monitor.ObserveCalibration([device]);
        Assert.Equal(3, capture.Notifications.Count);
    }

    [Fact]
    public void Disabled_rules_do_not_emit_alarms()
    {
        var capture = new Capture();
        var monitor = new AlarmMonitor(new(0, 0, false, false), capture, new VirtualClock());
        monitor.ObserveRun(Run());
        monitor.ObserveStorage(new("run", "SN", new(null, "unavailable", false), null));
        monitor.ObserveOutbox(100);
        monitor.ObserveCalibration([]);
        Assert.Empty(capture.Notifications);
    }
}
