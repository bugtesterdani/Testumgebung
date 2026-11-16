using Testumgebung.DatabaseAdapters;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Desktop;

public partial class MainWindow
{
    private readonly RemoteSettingsClient _remoteSettings = new();

    private Task PollRemoteSettingsAsync() => _remoteStationId is { } id
        ? PollConfiguredStationAsync(id)
        : Task.CompletedTask;

    private SqlRunDatabase? ActiveRemoteDatabase() => _closing ? null : _services.Database.Inner as SqlRunDatabase;

    private Task PollConfiguredStationAsync(string id)
    {
        if (ActiveRemoteDatabase() is not { } database) return Task.CompletedTask;
        return _remoteSettings.PollAsync(report => Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await database.RemoteSettings.ExchangeAsync(id, report, timeout.Token);
            }), () => Shell.ReportRemoteSettings(_services.Current),
            change => ActiveRemoteDatabase() == database ? Shell.ApplyRemoteSettings(change) : null,
            error => FaultLog.Write(error));
    }
}
