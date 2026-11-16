using System.Data.Common;
using System.Text.Json;
using Testumgebung.Contracts;

namespace Testumgebung.Presentation.Settings;

public sealed class RemoteSettingsClient
{
    private bool _polling;

    public static string? StationIdFromEnvironment()
    {
        var id = Environment.GetEnvironmentVariable("TESTUMGEBUNG_REMOTE_ID");
        return IsValidStationId(id) ? id : null;
    }

    public static bool IsValidStationId(string? id) => id is { Length: > 0 and <= 64 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Snapshot and application stay on the desktop dispatcher, while the caller runs SQL off it.
    public async Task PollAsync(Func<RemoteSettingsReport, Task<RemoteSettingsChange?>> exchange,
        Func<RemoteSettingsReport> snapshot, Func<RemoteSettingsChange, string?> apply, Action<Exception> onError)
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var change = await exchange(snapshot());
            if (change is not null && apply(change) is { } status)
                await exchange(snapshot() with { Revision = change.Revision, Status = status });
        }
        catch (Exception ex) when (ex is DbException or OperationCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            onError(ex);
        }
        finally { _polling = false; }
    }
}
