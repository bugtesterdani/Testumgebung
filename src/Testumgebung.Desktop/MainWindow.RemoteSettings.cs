namespace Testumgebung.Desktop;

public partial class MainWindow
{
    private Task PollRemoteSettingsAsync() => _remoteSettings is null || _closing
        ? Task.CompletedTask
        : _remoteSettings.PollAsync(() => Shell.ReportRemoteSettings(_services.Current),
            change => _closing ? null : Shell.ApplyRemoteSettings(change), error => FaultLog.Write(error));
}
