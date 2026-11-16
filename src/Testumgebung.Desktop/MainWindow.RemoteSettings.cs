using Testumgebung.DatabaseAdapters;
using Testumgebung.Presentation.Settings;

namespace Testumgebung.Desktop;

public partial class MainWindow
{
    private readonly RemoteSettingsClient _remoteSettings = new();

    private Task PollRemoteSettingsAsync()
    {
        if (_closing || _remoteStationId is null || _services.Database.Inner is not SqlRunDatabase database)
            return Task.CompletedTask;

        var id = _remoteStationId;
        return _remoteSettings.PollAsync(report => Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await database.RemoteSettings.ExchangeAsync(id, report, timeout.Token);
            }), () => Shell.ReportRemoteSettings(_services.Current),
            change => _closing || _services.Database.Inner != database ? null : Shell.ApplyRemoteSettings(change),
            error => FaultLog.Write(error));
    }
}
