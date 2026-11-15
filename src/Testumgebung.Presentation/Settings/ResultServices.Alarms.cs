using Testumgebung.Contracts;
using Testumgebung.Notifications;
using Testumgebung.Persistence.Results;
using Testumgebung.Persistence.Stations;

namespace Testumgebung.Presentation.Settings;

public sealed partial class ResultServices
{
    private readonly IClock _alarmClock;
    private AlarmMonitor? _alarmMonitor;
    private IReadOnlyList<QueuedNotifier> _alarmTargets = [];

    private void ConfigureAlarms(AlarmSettings settings)
    {
        var targets = new List<QueuedNotifier>();
        if (settings.SmtpEnabled) AddAlarmTarget(targets, CreateSmtp(settings), "SMTP: Server, Port sowie gültige Absender- und Empfängeradresse angeben.");
        if (settings.TeamsEnabled) AddAlarmTarget(targets, CreateTeams(settings), "Teams: eine HTTPS-Workflow-URL eingeben.");
        var previous = _alarmTargets;
        _alarmTargets = targets;
        var rules = new AlarmRules(settings.ErrorStreak, settings.OutboxThreshold, settings.ResultFolder, settings.Calibration);
        var monitor = targets.Count == 0 ? null : new AlarmMonitor(rules, new CompositeNotifier(targets), _alarmClock);
        Volatile.Write(ref _alarmMonitor, monitor);
        foreach (var target in previous) _ = target.DisposeAsync().AsTask();
        OnOutboxChanged();
    }

    private void AddAlarmTarget(List<QueuedNotifier> targets, QueuedNotifier? target, string validationError)
    {
        if (target is null)
        {
            ApplyWarning = Join(ApplyWarning, validationError);
            return;
        }
        target.Delivered += delivery => NotifyDelivered?.Invoke(delivery);
        targets.Add(target);
    }

    private static QueuedNotifier? CreateSmtp(AlarmSettings settings)
    {
        var options = new SmtpAlarmOptions(settings.SmtpHost?.Trim() ?? "", settings.SmtpPort, settings.SmtpStartTls,
            settings.SmtpUsername, settings.SmtpPassword, settings.SmtpSender, settings.SmtpRecipient);
        return options.IsValid ? new SmtpAlarmNotifier(options) : null;
    }

    private static QueuedNotifier? CreateTeams(AlarmSettings settings)
        => Uri.TryCreate(settings.TeamsUrl, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps
            ? new TeamsAlarmNotifier(endpoint) : null;

    private void OnRunReceived(ResultRun run) => Volatile.Read(ref _alarmMonitor)?.ObserveRun(run);
    private void OnStorageProcessed(StorageReport report) => Volatile.Read(ref _alarmMonitor)?.ObserveStorage(report);
    private void OnOutboxChanged() => Volatile.Read(ref _alarmMonitor)?.ObserveOutbox(Outbox.PendingCount);

    public void ObserveCalibration(IEnumerable<DeviceConfig> devices)
        => Volatile.Read(ref _alarmMonitor)?.ObserveCalibration(devices);

    private async Task DisposeAlarmsAsync()
    {
        Volatile.Write(ref _alarmMonitor, null);
        foreach (var target in _alarmTargets) await target.DisposeAsync().ConfigureAwait(false);
    }
}
